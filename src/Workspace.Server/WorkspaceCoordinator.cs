using System.Collections.Concurrent;
using System.Diagnostics;

namespace Workspace.Server;

public sealed class WorkspaceCoordinator(
    WorkspaceStore store,
    IWorkspaceWorkflow workflow,
    WorkspaceOptions options,
    ILogger<WorkspaceCoordinator> logger) : BackgroundService
{
    private sealed class RunningJob(string jobId) : IDisposable
    {
        public string JobId { get; } = jobId;
        public CancellationTokenSource Cancellation { get; } = new();
        public void Dispose() => Cancellation.Dispose();
    }

    private readonly ConcurrentDictionary<string, RunningJob> _active = new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        store.RecoverInterrupted();
        return base.StartAsync(cancellationToken);
    }

    public void CancelActive(string workspaceId, string jobId)
    {
        if (_active.TryGetValue(workspaceId, out var running) && running.JobId == jobId)
        {
            try { running.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* The completed run has already released its cancellation source. */ }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var completed in tasks.Where(t => t.IsCompleted).ToList())
                {
                    await completed;
                    tasks.Remove(completed);
                }

                foreach (var candidate in store.List().Where(w => w.Jobs.Any(j => j.Status == "queued"))
                    .OrderBy(w => w.Jobs.Where(j => j.Status == "queued").Min(j => j.CreatedAt)))
                {
                    if (_active.Count >= 2) break;
                    if (_active.ContainsKey(candidate.Id)) continue;
                    JobState? claimed = null;
                    var snapshot = store.Change(candidate.Id, "job.started", "Agents are working on the next request.", document =>
                    {
                        if (document.Jobs.Any(j => j.Status is "running" or "cancelling")) return false;
                        var next = document.Jobs.Where(j => j.Status == "queued").MinBy(j => j.Order);
                        if (next is null) return false;
                        next.Status = "running";
                        next.StartedAt = DateTimeOffset.UtcNow;
                        next.Message = "Preparing the task context.";
                        next.BaseRevisions = document.Artifacts.ToDictionary(a => a.Id, a => a.CurrentRevision);
                        var feedback = document.Feedback.Find(f => f.Id == next.FeedbackId);
                        if (feedback is not null) feedback.Status = "running";
                        foreach (var agent in document.Agents)
                        {
                            agent.Assigned = agent.Id == AgentPlanPolicy.CoordinatorId;
                            if (agent.Assigned)
                            {
                                agent.Name = "Coordinator";
                                agent.Role = "Understands the task and recruits help only when needed";
                                agent.Status = "idle";
                            }
                            agent.ActiveJobId = null;
                        }
                        claimed = next;
                        return true;
                    });
                    if (claimed is null) continue;
                    var running = new RunningJob(claimed.Id);
                    if (!_active.TryAdd(snapshot.Id, running))
                    {
                        running.Dispose();
                        throw new InvalidOperationException("A workspace was claimed twice.");
                    }
                    tasks.Add(RunAsync(snapshot, claimed, running, stoppingToken));
                }
                await Task.Delay(150, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown is handled by each run so durable state never claims completion.
        }
        finally
        {
            foreach (var (workspaceId, running) in _active.ToArray()) CancelActive(workspaceId, running.JobId);
            await Task.WhenAll(tasks);
        }
    }

    private async Task RunAsync(
        WorkspaceDocument snapshot, JobState job, RunningJob running, CancellationToken stoppingToken)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(options.ProviderTimeoutSeconds, 10, 600) *
                (WorkspaceOptions.MaximumSpecialists + 1) + 30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            running.Cancellation.Token, deadline.Token, stoppingToken);
        using var progressGate = new SemaphoreSlim(1);
        var lastDelta = new Dictionary<string, long>();
        var participants = new HashSet<string>(StringComparer.Ordinal) { AgentPlanPolicy.CoordinatorId };
        var rosterDeclared = false;
        try
        {
            if (WorkspaceService.Job(store.Get(snapshot.Id), job.Id).Status == "cancelling")
                running.Cancellation.Cancel();
            linked.Token.ThrowIfCancellationRequested();
            ValidateCurrentAnchor(snapshot, job);
            var result = await workflow.ExecuteAsync(new(ExecutionContext(snapshot, job), job), OnSignal, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            ValidateResult(snapshot, job, result);
            store.Change(snapshot.Id, "job.completed",
                result.Clarification is null ? "Agent proposals are ready for your review." : "A few decisions need your input.",
                document =>
                {
                    var active = WorkspaceService.Job(document, job.Id);
                    if (active.Status != "running") return false;
                    Publish(document, active, result);
                    active.Status = "completed";
                    active.Message = result.Clarification is null ? "Ready for your review." : "Waiting for your decisions.";
                    active.CompletedAt = DateTimeOffset.UtcNow;
                    foreach (var agent in document.Agents.Where(agent => agent.Assigned))
                    {
                        agent.Status = agent.ActiveJobId == job.Id ? "complete" : result.Clarification is null ? "idle" : "waiting";
                        if (agent.ActiveJobId == job.Id) agent.Message = "This stage is complete.";
                        agent.ActiveJobId = null;
                    }
                    var feedback = document.Feedback.Find(f => f.Id == job.FeedbackId);
                    if (feedback is not null) feedback.Status = result.Artifacts.Count > 0 ? "review" : "completed";
                    return true;
                }, job.Id);
            if (WorkspaceService.Job(store.Get(snapshot.Id), job.Id).Status == "cancelling")
                FinishFailure(snapshot.Id, job.Id, "cancelled", "cancelled", "Cancelled. No late proposal was published.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            FinishFailure(snapshot.Id, job.Id, "interrupted", "service_stopped", "Service stopped. Your input is saved; retry to continue.");
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            FinishFailure(snapshot.Id, job.Id, "cancelled", "cancelled", "Cancelled. Your existing artifacts are unchanged.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            FinishFailure(snapshot.Id, job.Id, "failed", "workflow_timeout", "The bounded workflow timed out. Review the saved state before retrying.");
        }
        catch (AgentProviderException error)
        {
            logger.LogWarning("Provider run {JobId} failed with {Code}.", job.Id, error.Code);
            FinishFailure(snapshot.Id, job.Id, "failed", error.Code, SafeMessage(error.Message));
        }
        catch (WorkspaceException error)
        {
            logger.LogWarning("Workspace run {JobId} rejected output with {Code}.", job.Id, error.Code);
            FinishFailure(snapshot.Id, job.Id, "failed", error.Code, SafeMessage(error.Message));
        }
        catch (Exception error)
        {
            logger.LogError("Workspace run {JobId} failed unexpectedly with {ErrorType}.", job.Id, error.GetType().Name);
            FinishFailure(snapshot.Id, job.Id, "failed", "workflow_error",
                "This run failed unexpectedly. Your input and previous artifacts are saved; inspect the local service log and retry.");
        }
        finally
        {
            _active.TryRemove(snapshot.Id, out _);
            running.Dispose();
        }

        async Task OnSignal(AgentSignal signal)
        {
            if (signal.Kind is not ("session" or "status" or "delta" or "roster"))
                throw new AgentProviderException("invalid_agent_signal", "An agent emitted an unsupported progress event.");
            await progressGate.WaitAsync();
            try
            {
                if (signal.Kind == "roster")
                {
                    if (signal.AgentId != AgentPlanPolicy.CoordinatorId || rosterDeclared || signal.Assignments is null)
                        throw new WorkspaceException("invalid_agent_plan", "Only the coordinator can declare one specialist roster for this run.");
                    AgentPlanPolicy.ValidateAssignments(signal.Assignments);
                    linked.Token.ThrowIfCancellationRequested();
                    var applied = false;
                    store.Change(snapshot.Id, "agent.roster", SafeMessage(signal.Message), document =>
                    {
                        if (WorkspaceService.Job(document, job.Id).Status != "running") return false;
                        AgentPlanPolicy.ApplyRoster(document, signal.Assignments);
                        document.Summary = SafeMessage(signal.Message);
                        applied = true;
                        return true;
                    }, job.Id, signal.AgentId);
                    if (!applied)
                        throw new OperationCanceledException("This run no longer accepts a new agent roster.", linked.Token);
                    participants.UnionWith(signal.Assignments.Select(assignment => assignment.Id));
                    rosterDeclared = true;
                    return;
                }
                if (!participants.Contains(signal.AgentId))
                    throw new AgentProviderException("invalid_agent_signal", "An unplanned agent tried to participate in this run.");
                if (signal.Kind == "delta")
                {
                    var now = Stopwatch.GetTimestamp();
                    if (lastDelta.TryGetValue(signal.AgentId, out var previous) &&
                        Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(1)) return;
                    lastDelta[signal.AgentId] = now;
                }
                var message = signal.Kind == "session" ? $"{signal.AgentId} session is ready." : SafeMessage(signal.Message);
                store.Change(snapshot.Id, $"agent.{signal.Kind}", message, document =>
                {
                    var currentJob = WorkspaceService.Job(document, job.Id);
                    if (currentJob.Status is not ("running" or "cancelling")) return false;
                    var agent = document.Agents.Single(a => a.Id == signal.AgentId);
                    if (signal.Kind == "session")
                    {
                        if (string.IsNullOrWhiteSpace(signal.SessionId) || signal.SessionId.Length > 200)
                            throw new AgentProviderException("invalid_session", "The provider returned an invalid session reference.");
                        agent.SessionId = signal.SessionId;
                        return true;
                    }
                    if (currentJob.Status != "running") return false;
                    foreach (var previous in document.Agents.Where(a => a.Assigned && a.Status == "working" && a.Id != signal.AgentId))
                    {
                        previous.Status = "complete";
                        previous.Message = "This stage is complete.";
                    }
                    agent.Status = "working";
                    agent.Message = message;
                    agent.ActiveJobId = job.Id;
                    currentJob.Message = message;
                    return true;
                }, job.Id, signal.AgentId);
            }
            finally { progressGate.Release(); }
        }
    }

    private void FinishFailure(string workspaceId, string jobId, string status, string code, string message)
    {
        store.Change(workspaceId, $"job.{status}", message, document =>
        {
            var job = WorkspaceService.Job(document, jobId);
            if (job.Status is not ("running" or "cancelling")) return false;
            job.Status = status;
            job.ErrorCode = code;
            job.Message = message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            var feedback = document.Feedback.Find(f => f.Id == job.FeedbackId);
            if (feedback is not null) feedback.Status = status;
            if (code == "copilot_cleanup_failed")
            {
                foreach (var queued in document.Jobs.Where(j => j.Status == "queued"))
                {
                    queued.Status = "interrupted";
                    queued.ErrorCode = "provider_cleanup_unconfirmed";
                    queued.Message = "An earlier runtime did not confirm cleanup. Verify it has stopped before explicitly retrying.";
                    queued.CompletedAt = DateTimeOffset.UtcNow;
                    var pendingFeedback = document.Feedback.Find(f => f.Id == queued.FeedbackId);
                    if (pendingFeedback is not null) pendingFeedback.Status = "interrupted";
                }
            }
            foreach (var agent in document.Agents.Where(a => a.ActiveJobId == jobId))
            {
                agent.Status = status == "cancelled" ? "cancelled" : "error";
                agent.Message = message;
                agent.ActiveJobId = null;
            }
            return true;
        }, jobId);
    }

    public static void ValidateResult(WorkspaceDocument snapshot, JobState job, WorkflowResult result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > 4000 ||
            result.Artifacts is null || result.Review?.Length > 8000)
            throw new WorkspaceException("invalid_agent_output", "The agent returned an invalid workflow result.");
        var target = snapshot.Feedback.Find(f => f.Id == job.FeedbackId);
        if (result.Clarification is not null)
        {
            if (result.Artifacts.Count != 0 || job.Kind == "answers" || target?.ArtifactId is not null)
                throw new WorkspaceException("unexpected_clarification", "This run requested clarification after drafting was already authorized.");
            SpecValidator.Validate(result.Clarification, "clarification");
            return;
        }
        if (job.Kind == "clarify")
            throw new WorkspaceException("invalid_agent_output", "A question-refresh run must return a new clarification form.");
        if (result.Artifacts.Count is < 1 or > 3 || result.Review is not null && string.IsNullOrWhiteSpace(result.Review))
            throw new WorkspaceException("invalid_agent_output", "A completed run needs 1-3 artifacts; any specialist review must contain a summary.");
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in result.Artifacts)
        {
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.Title) || artifact.Title.Length > 180)
                throw new WorkspaceException("invalid_agent_output", "Every artifact needs a bounded title.");
            if (artifact.ArtifactId is not null &&
                (!snapshot.Artifacts.Any(a => a.Id == artifact.ArtifactId) || !updatedIds.Add(artifact.ArtifactId)))
                throw new WorkspaceException("invalid_agent_output", "An artifact update has an unknown or duplicate identifier.");
            SpecValidator.Validate(artifact.Spec, "artifact");
        }
        if (target?.ArtifactId is string targetId &&
            (result.Artifacts.Count != 1 || result.Artifacts[0].ArtifactId != targetId))
            throw new WorkspaceException("wrong_feedback_target", "The generated revision does not match your selected artifact.");
        if (snapshot.Artifacts.Count + result.Artifacts.Count(a => a.ArtifactId is null) > 12)
            throw new WorkspaceException("artifact_limit", "This MVP supports twelve artifact surfaces per workspace.");
    }

    public static void Publish(WorkspaceDocument document, JobState job, WorkflowResult result)
    {
        document.Summary = result.Summary;
        if (result.Clarification is not null)
        {
            document.Clarification = result.Clarification;
            document.ClarificationId = Guid.NewGuid().ToString("N");
            return;
        }
        document.Clarification = null;
        document.ClarificationId = null;
        foreach (var draft in result.Artifacts)
        {
            ArtifactState artifact;
            if (draft.ArtifactId is null)
            {
                artifact = new() { Id = Guid.NewGuid().ToString("N"), Title = draft.Title };
                document.Artifacts.Add(artifact);
                var index = document.Artifacts.Count - 1;
                document.Layout.Positions[$"artifact-{artifact.Id}"] = new(550 + index % 2 * 680, 350 + index / 2 * 720);
            }
            else artifact = WorkspaceService.Artifact(document, draft.ArtifactId);
            var baseRevision = job.BaseRevisions.GetValueOrDefault(artifact.Id);
            var conflict = baseRevision != artifact.CurrentRevision ||
                job.StartedAt is { } started && document.Decisions.Any(d =>
                    d.ArtifactId == artifact.Id && d.Kind == "reject" && d.CreatedAt >= started);
            var number = artifact.Revisions.Count == 0 ? 1 : artifact.Revisions.Max(r => r.Revision) + 1;
            artifact.Revisions.Add(new()
            {
                Revision = number,
                BaseRevision = baseRevision == 0 ? null : baseRevision,
                Spec = draft.Spec,
                Source = document.Provider == "demo" ? "demo" : "agent",
                JobId = job.Id,
                Review = result.Review,
                Conflict = conflict
            });
            if (!conflict)
            {
                var previous = artifact.Revisions.Find(r => r.Revision == artifact.CurrentRevision);
                if (previous?.Status == "proposed")
                {
                    previous.Status = "superseded";
                    WorkspaceService.RefreshFeedbackReview(document, previous.JobId);
                }
                artifact.CurrentRevision = number;
                artifact.Title = draft.Title;
            }
        }
    }

    public static WorkspaceDocument ExecutionContext(WorkspaceDocument snapshot, JobState job)
    {
        var context = JsonDefaults.Clone(snapshot);
        var current = context.Feedback.Find(f => f.Id == job.FeedbackId);
        var prior = context.Feedback.Where(f => f.Id != job.FeedbackId &&
            f.Status is "review" or "accepted" or "completed" &&
            context.Jobs.Any(j => j.Id == f.JobId && j.Order < job.Order && j.Status == "completed"))
            .TakeLast(11).ToList();
        if (current is not null) prior.Add(current);
        context.Feedback = prior;
        context.Jobs = context.Jobs.Where(j => j.Id == job.Id || prior.Any(f => f.JobId == j.Id)).ToList();
        context.Decisions = context.Decisions.TakeLast(20).ToList();
        foreach (var artifact in context.Artifacts)
            artifact.Revisions = artifact.Revisions.Where(r =>
                r.Revision == artifact.CurrentRevision || r.Revision == artifact.AcceptedRevision ||
                current?.ArtifactId == artifact.Id && r.Revision == current.Revision).ToList();
        return context;
    }

    private static void ValidateCurrentAnchor(WorkspaceDocument snapshot, JobState job)
    {
        var feedback = snapshot.Feedback.Find(f => f.Id == job.FeedbackId);
        if (feedback?.ArtifactId is not string artifactId) return;
        var artifact = WorkspaceService.Artifact(snapshot, artifactId);
        var latest = artifact.Revisions.Single(r => r.Revision == artifact.CurrentRevision);
        if (feedback.ElementId is null || !latest.Spec.Elements.ContainsKey(feedback.ElementId))
            throw new WorkspaceException("anchor_changed",
                "An earlier revision removed this block. Select a current block and submit new direction; your original note is retained.", 409);
    }

    private static string SafeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The agent is working.";
        return message.Length <= 1000 ? message : message[..1000];
    }
}
