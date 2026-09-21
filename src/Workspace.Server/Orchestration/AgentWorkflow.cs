using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI.Workflows;

namespace Workspace.Server.Orchestration;

public sealed class AgentWorkflow : IWorkspaceWorkflow
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;
    private readonly string _packDirectory;

    public AgentWorkflow(IEnumerable<IAgentProvider> providers, WorkspaceOptions options)
    {
        _providers = providers.ToDictionary(provider => provider.Name, StringComparer.Ordinal);
        _packDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(options.AgentPackDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "agent-pack") : options.AgentPackDirectory);
    }

    public async Task<WorkflowResult> ExecuteAsync(
        WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new WorkflowRequest(JsonDefaults.Clone(request.Workspace), JsonDefaults.Clone(request.Job));
        if (snapshot.Workspace.Provider is not ("copilot" or "demo") ||
            !_providers.TryGetValue(snapshot.Workspace.Provider, out var provider))
            throw new AgentProviderException("provider_unavailable", "The selected workspace provider is not registered.");
        if (!snapshot.Workspace.Agents.Any(agent => agent.Id == AgentPlanPolicy.CoordinatorId))
            throw new WorkspaceException("invalid_agent_plan", "The workspace coordinator is missing.", 500);

        var execution = new ExecutionContext(snapshot, provider, TrustedAgentPack.Load(_packDirectory), onSignal, cancellationToken);
        var input = AgentPromptContext.Create(snapshot);
        var coordinator = new CoordinatorExecutor(execution);
        var planningGraph = new WorkflowBuilder(coordinator)
            .WithName("workspace-coordination-v2")
            .WithOutputFrom(coordinator)
            .Build();
        var plan = await RunGraphAsync<AgentPromptContext, ValidatedAgentPlan>(
            planningGraph, input, execution, snapshot.Job.Id + "-coordination", cancellationToken);
        var candidates = InitialCandidates(plan, input);

        cancellationToken.ThrowIfCancellationRequested();
        await onSignal(new(AgentPlanPolicy.CoordinatorId, "roster", plan.Summary, Assignments: plan.Agents.AsReadOnly()));
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Clarification is not null)
            return new(plan.Summary, plan.Clarification, [], null);
        if (plan.Agents.Count == 0)
            return new(plan.Summary, null, plan.Artifacts, null);

        var context = input with
        {
            PlannerSummary = plan.Summary, Team = plan.Agents,
            ReviewCharacterLimit = ReviewBudget(plan.Agents)
        };
        var initial = new CandidateState(context, candidates, [], candidates.Count == 0 ? 0 : 1);
        var specialists = plan.Agents.Select(assignment => new SpecialistExecutor(execution, assignment)).ToArray();
        var builder = new WorkflowBuilder(specialists[0]).WithName("workspace-task-specialists-v2");
        for (var index = 1; index < specialists.Length; index++)
            builder.AddEdge(specialists[index - 1], specialists[index]);
        var specialistGraph = builder.WithOutputFrom(specialists[^1]).Build();
        var completed = await RunGraphAsync<CandidateState, CandidateState>(
            specialistGraph, initial, execution, snapshot.Job.Id + "-specialists", cancellationToken);
        if (completed.Artifacts.Count is < 1 or > 3)
            throw new WorkspaceException("invalid_agent_plan", "The selected team did not produce a bounded candidate set.");
        var review = completed.Reviews.Count == 0 ? null : string.Join("\n\n",
            completed.Reviews.Select(item => ReviewPrefix(item.Name, item.CandidateVersion) + item.Summary));
        if (review?.Length > 8000)
            throw new AgentProviderException("invalid_output", "The combined specialist reviews exceed the review limit.");
        return new(plan.Summary, null, completed.Artifacts, review);
    }

    private static List<DraftArtifact> InitialCandidates(ValidatedAgentPlan plan, AgentPromptContext input)
    {
        if (plan.Artifacts.Count != 0) return plan.Artifacts;
        if (plan.Agents.FirstOrDefault()?.Kind != "review") return [];
        if (input.WorkingArtifacts.Count is < 1 or > 3)
            throw new WorkspaceException("invalid_agent_plan", "Choose one to three existing drafts before a review-only team.");
        return input.WorkingArtifacts.Select(item => new DraftArtifact(item.ArtifactId, item.Title, item.Spec)).ToList();
    }

    private static int ReviewBudget(IReadOnlyList<AgentAssignment> assignments)
    {
        var reviewers = assignments.Where(assignment => assignment.Kind == "review").ToArray();
        if (reviewers.Length == 0) return 8000;
        var headings = reviewers.Sum(assignment => ReviewPrefix(assignment.Name, WorkspaceOptions.MaximumSpecialists + 1).Length);
        return (8000 - headings - (reviewers.Length - 1) * 2) / reviewers.Length;
    }

    private static string ReviewPrefix(string name, int version) => $"{name} (draft {version}):\n";

    private static async Task<TOutput> RunGraphAsync<TInput, TOutput>(
        Workflow workflow, TInput input, ExecutionContext execution, string sessionId, CancellationToken cancellationToken)
        where TInput : notnull where TOutput : class
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow, input, sessionId: sessionId, cancellationToken: cancellationToken);
        TOutput? result = null;
        try
        {
            await foreach (var workflowEvent in run.WatchStreamAsync(
                blockOnPendingRequest: false, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (workflowEvent)
                {
                    case ExecutorFailedEvent failed:
                        Rethrow(failed.Data as Exception);
                        break;
                    case WorkflowErrorEvent error:
                        Rethrow(error.Exception);
                        break;
                    case WorkflowOutputEvent output:
                        if (output.Data is not TOutput terminal || result is not null)
                            throw new WorkspaceException("workflow_failed", "The workflow returned an invalid terminal result.", 500);
                        result = terminal;
                        break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result ?? throw new WorkspaceException("workflow_failed", "The workflow ended without a result.", 500);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await run.CancelRunAsync();
            execution.ThrowProviderFailure();
            throw;
        }
    }

    private static void Rethrow(Exception? exception)
    {
        if (exception is null)
            throw new WorkspaceException("workflow_failed", "A workflow executor failed without a result.", 500);
        var current = exception;
        while (current is not (AgentProviderException or WorkspaceException or OperationCanceledException) &&
               current.InnerException is not null)
            current = current.InnerException;
        ExceptionDispatchInfo.Capture(current).Throw();
    }

    private sealed record CandidateState(
        AgentPromptContext Context, List<DraftArtifact> Artifacts, List<SpecialistReview> Reviews, int Version);

    private sealed record ExecutionContext(
        WorkflowRequest Request, IAgentProvider Provider, TrustedAgentPack Pack,
        Func<AgentSignal, Task> OnSignal, CancellationToken RunCancellation)
    {
        private AgentProviderException? _providerFailure;

        public void ThrowProviderFailure()
        {
            if (Volatile.Read(ref _providerFailure) is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public async Task<string> CompleteAsync(
            string role, string agentId, string message, AgentPromptContext input, CancellationToken executorCancellation)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(RunCancellation, executorCancellation);
            cancellation.Token.ThrowIfCancellationRequested();
            var saved = Request.Workspace.Agents.SingleOrDefault(item => item.Id == agentId);
            await OnSignal(new(agentId, "status", message));
            Task Forward(AgentSignal signal)
            {
                if (signal.AgentId != agentId || signal.Kind is not ("session" or "status" or "delta") || signal.Assignments is not null)
                    throw new AgentProviderException("invalid_provider_signal", "The provider emitted an unauthorized agent signal.");
                return OnSignal(signal);
            }
            AgentReply reply;
            try
            {
                reply = await Provider.CompleteAsync(
                    new(Request.Workspace.Id, agentId, saved?.SessionId, Pack.ForRole(role), input.ToPrompt(), role),
                    Forward, cancellation.Token);
            }
            catch (AgentProviderException exception)
            {
                // A cancelled event reader must not hide an unconfirmed provider abort.
                Interlocked.CompareExchange(ref _providerFailure, exception, null);
                throw;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(reply.SessionId))
                throw new AgentProviderException("invalid_output", "The provider did not return an agent session identifier.");
            return reply.Text;
        }
    }

    private sealed class CoordinatorExecutor(ExecutionContext execution)
        : Executor<AgentPromptContext, ValidatedAgentPlan>(AgentPlanPolicy.CoordinatorId)
    {
        public override async ValueTask<ValidatedAgentPlan> HandleAsync(
            AgentPromptContext input, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var text = await execution.CompleteAsync("planner", AgentPlanPolicy.CoordinatorId,
                "Choosing the next decisions and only the help this task needs.", input, cancellationToken);
            return AgentOutputValidator.Planner(text, input);
        }
    }

    private sealed class SpecialistExecutor(ExecutionContext execution, AgentAssignment assignment)
        : Executor<CandidateState, CandidateState>(assignment.Id)
    {
        public override async ValueTask<CandidateState> HandleAsync(
            CandidateState input, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var prompt = input.Context with { Assignment = assignment, Proposals = input.Artifacts, Reviews = input.Reviews };
            if (assignment.Kind == "produce")
            {
                var text = await execution.CompleteAsync("producer", assignment.Id,
                    $"{assignment.Name}: preparing the candidate artifacts.", prompt, cancellationToken);
                var produced = AgentOutputValidator.Producer(text, prompt);
                return input with { Artifacts = produced.Artifacts, Version = input.Version + 1 };
            }
            if (input.Artifacts.Count == 0)
                throw new WorkspaceException("invalid_agent_plan", "A review specialist cannot run without a draft.");
            var reviewText = await execution.CompleteAsync("reviewer", assignment.Id,
                $"{assignment.Name}: reviewing the current candidate.", prompt, cancellationToken);
            var review = AgentOutputValidator.Reviewer(reviewText, prompt.ReviewCharacterLimit);
            return input with
            {
                Reviews = [.. input.Reviews, new(assignment.Id, assignment.Name, input.Version, review.Summary)]
            };
        }
    }
}
