using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task FeedbackRunsFifoAgainstLatestDraftWhileRetainingOriginalAnchors()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var workflow = new RecordingWorkflow();
        using var coordinator = Coordinator(fixture, workflow);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback("First direction"));
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback("Second direction"));
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Count(j => j.Status == "completed") == 3);
            var calls = workflow.Calls.ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal(1, calls[0].WorkingRevision);
            Assert.Equal(2, calls[1].WorkingRevision);
            Assert.All(calls, call => Assert.Equal(1, call.OriginalRevision));
            Assert.DoesNotContain("Second direction", workflow.VisibleNotes.First());
            Assert.Contains("First direction", workflow.VisibleNotes.Last());
            var saved = fixture.Store.Get(workspace.Id);
            var current = saved.Artifacts[0].Revisions.Single(r => r.Revision == saved.Artifacts[0].CurrentRevision);
            var text = current.Spec.Elements["body"].Props["text"]!.GetValue<string>();
            Assert.Contains("First direction", text);
            Assert.Contains("Second direction", text);
            Assert.Equal(1, saved.Artifacts[0].AcceptedRevision);
            Assert.Equal("proposed", current.Status);
            Assert.Equal("completed", saved.Feedback[0].Status);
            Assert.Equal("review", saved.Feedback[1].Status);
            Assert.Equal(1, workflow.MaxConcurrent);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellingActiveWorkAbortsTheWorkflowAndNeverPublishesLateOutput()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var workflow = new BlockingWorkflow();
        using var coordinator = Coordinator(fixture, workflow);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var queued = fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
            var job = queued.Jobs.Last();
            await workflow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Service.Cancel(workspace.Id, job.Id);
            coordinator.CancelActive(workspace.Id, job.Id);
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Last().Status == "cancelled");
            Assert.True(workflow.ObservedCancellation);
            Assert.Single(fixture.Store.Get(workspace.Id).Artifacts[0].Revisions);
            Assert.Equal("saved-session", fixture.Store.Get(workspace.Id).Agents.Single(a => a.Id == "planner").SessionId);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ShutdownMarksActiveWorkInterruptedAndKeepsInput()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var workflow = new BlockingWorkflow();
        using var coordinator = Coordinator(fixture, workflow);
        await coordinator.StartAsync(CancellationToken.None);
        fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback("Keep this exact note"));
        await workflow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(CancellationToken.None);
        fixture.Reopen();
        var saved = fixture.Store.Get(workspace.Id);
        Assert.Equal("interrupted", saved.Jobs.Last().Status);
        Assert.Equal("Keep this exact note", saved.Feedback.Single().Text);
        Assert.Equal(1, saved.Artifacts[0].CurrentRevision);
    }

    [Fact]
    public async Task UnconfirmedProviderCleanupIsAFailureAndPreventsAutomaticQueueContinuation()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var workflow = new UnconfirmedCancellationWorkflow();
        using var coordinator = Coordinator(fixture, workflow);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var first = fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback("First request"));
            var jobId = first.Jobs.Last().Id;
            await workflow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback("Next request"));
            fixture.Service.Cancel(workspace.Id, jobId);
            coordinator.CancelActive(workspace.Id, jobId);
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Any(j => j.ErrorCode == "copilot_cleanup_failed"));
            var saved = fixture.Store.Get(workspace.Id);
            Assert.Equal("failed", saved.Jobs.Single(j => j.Id == jobId).Status);
            Assert.Equal("interrupted", saved.Jobs.Last().Status);
            Assert.Equal("provider_cleanup_unconfirmed", saved.Jobs.Last().ErrorCode);
            Assert.Single(saved.Artifacts[0].Revisions);
            Assert.Equal(1, workflow.Calls);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ProviderFailureIsVisibleAndDoesNotSubstituteDemoSuccess()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        using var coordinator = Coordinator(fixture, new FailureWorkflow());
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Last().Status == "failed");
            var saved = fixture.Store.Get(workspace.Id);
            Assert.Equal("copilot_auth_required", saved.Jobs.Last().ErrorCode);
            Assert.Equal("failed", saved.Feedback.Single().Status);
            Assert.Single(saved.Artifacts[0].Revisions);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task IndependentWorkspacesRunInParallelButNeverExceedTwoActiveWorkflows()
    {
        using var fixture = new ServiceFixture();
        var workspaces = Enumerable.Range(0, 3).Select(_ => ServiceTestData.Seed(fixture.Store)).ToArray();
        var workflow = new BoundedParallelWorkflow();
        using var coordinator = Coordinator(fixture, workflow);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            foreach (var workspace in workspaces)
                fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
            await Until(() => workflow.Started == 2);
            Assert.Equal(2, fixture.Store.List().Count(w => w.Jobs.Any(j => j.Status == "running")));
            Assert.Single(fixture.Store.List(), w => w.Jobs.Any(j => j.Status == "queued"));
            workflow.Release.TrySetResult();
            await Until(() => workspaces.All(w => fixture.Store.Get(w.Id).Jobs.Last().Status == "completed"));
            Assert.Equal(2, workflow.Peak);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task MissingAnchorFailsWithoutApplyingFeedbackToAnUnrelatedBlock()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var workflow = new RecordingWorkflow();
        using var coordinator = Coordinator(fixture, workflow);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            fixture.Store.Update(workspace.Id, "test.restructured", "Block removed.", document =>
            {
                var spec = ServiceTestData.Artifact();
                spec.Elements["document"].Children.Remove("body");
                spec.Elements.Remove("body");
                document.Artifacts[0].Revisions.Add(new() { Revision = 2, Spec = spec, Source = "human", Status = "accepted" });
                document.Artifacts[0].CurrentRevision = 2;
            });
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Last().Status == "failed");
            Assert.Equal("anchor_changed", fixture.Store.Get(workspace.Id).Jobs.Last().ErrorCode);
            Assert.Empty(workflow.Calls);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OnlyDeclaredSpecialistsAreRegisteredAndTheirSessionsPersistBeforeResults()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        using var coordinator = Coordinator(fixture, new DeclaredTeamWorkflow());
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Last().Status == "completed");
            var saved = fixture.Store.Get(workspace.Id);
            Assert.Equal(["planner", "options-analyst", "risk-checker"],
                saved.Agents.Where(agent => agent.Assigned).Select(agent => agent.Id));
            Assert.Equal("Options analyst", saved.Agents.Single(agent => agent.Id == "options-analyst").Name);
            Assert.Equal("session-options-analyst", saved.Agents.Single(agent => agent.Id == "options-analyst").SessionId);
            Assert.Equal("session-risk-checker", saved.Agents.Single(agent => agent.Id == "risk-checker").SessionId);
            Assert.Contains(fixture.Store.Events(workspace.Id, 0), item => item.Type == "agent.roster");
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false, "invalid_agent_signal")]
    [InlineData(true, "invalid_agent_plan")]
    public async Task UnplannedAgentsAndRepeatedRecruitmentAreRejected(bool secondRoster, string code)
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        using var coordinator = Coordinator(fixture, new InvalidTeamWorkflow(secondRoster));
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
            await Until(() => fixture.Store.Get(workspace.Id).Jobs.Last().Status == "failed");
            var saved = fixture.Store.Get(workspace.Id);
            Assert.Equal(code, saved.Jobs.Last().ErrorCode);
            Assert.Single(saved.Agents);
            Assert.Single(saved.Artifacts[0].Revisions);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void DirectCoordinatorArtifactsDoNotNeedAnInventedSpecialistReview()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var job = workspace.Jobs[0];
        var result = new WorkflowResult("Handled directly.", null,
            [new("artifact-one", "Direct draft", ServiceTestData.Artifact())], null);
        WorkspaceCoordinator.ValidateResult(workspace, job, result);
        job.BaseRevisions["artifact-one"] = 1;
        WorkspaceCoordinator.Publish(workspace, job, result);
        Assert.Null(workspace.Artifacts[0].Revisions.Last().Review);
        Assert.Equal("proposed", workspace.Artifacts[0].Revisions.Last().Status);
    }

    private static WorkspaceCoordinator Coordinator(ServiceFixture fixture, IWorkspaceWorkflow workflow) =>
        new(fixture.Store, workflow, fixture.Options, NullLogger<WorkspaceCoordinator>.Instance);

    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), "The expected durable state transition did not occur.");
            await Task.Delay(30);
        }
    }

    private sealed class RecordingWorkflow : IWorkspaceWorkflow
    {
        public ConcurrentQueue<(int WorkingRevision, int? OriginalRevision)> Calls { get; } = new();
        public ConcurrentQueue<string[]> VisibleNotes { get; } = new();
        private int _concurrent;
        public int MaxConcurrent { get; private set; }

        public async Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, active);
            try
            {
                var artifact = request.Workspace.Artifacts.Single();
                var feedback = request.Workspace.Feedback.Single(f => f.Id == request.Job.FeedbackId);
                Calls.Enqueue((artifact.CurrentRevision, feedback.Revision));
                VisibleNotes.Enqueue(request.Workspace.Feedback.Select(f => f.Text).ToArray());
                await onSignal(new("planner", "roster", "This task needs no specialists.", Assignments: []));
                await onSignal(new("planner", "status", "Creating a bounded revision."));
                await Task.Delay(80, cancellationToken);
                var current = artifact.Revisions.Single(r => r.Revision == artifact.CurrentRevision);
                var text = current.Spec.Elements["body"].Props["text"]!.GetValue<string>() + " / " + feedback.Text;
                return new("A revised working draft.", null,
                    [new(artifact.Id, artifact.Title, ServiceTestData.Artifact(text))], "This remains a proposal for human review.");
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }

    private sealed class BlockingWorkflow : IWorkspaceWorkflow
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        public async Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
        {
            await onSignal(new("planner", "session", "Session ready.", "saved-session"));
            await onSignal(new("planner", "status", "Working."));
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            throw new InvalidOperationException("An infinite wait unexpectedly completed.");
        }
    }

    private sealed class FailureWorkflow : IWorkspaceWorkflow
    {
        public Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken) =>
            throw new AgentProviderException("copilot_auth_required", "Sign in using the documented isolated CLI profile.");
    }

    private sealed class DeclaredTeamWorkflow : IWorkspaceWorkflow
    {
        public async Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
        {
            AgentAssignment[] assignments =
            [
                new("options-analyst", "Options analyst", "Compare the alternatives.", "produce"),
                new("risk-checker", "Risk checker", "Review material assumptions.", "review")
            ];
            await onSignal(new("planner", "status", "Selecting only the relevant specialists."));
            await onSignal(new("planner", "roster", "This comparison needs an analyst and a risk check.", Assignments: assignments));
            foreach (var assignment in assignments)
            {
                await onSignal(new(assignment.Id, "session", "Connected.", $"session-{assignment.Id}"));
                await onSignal(new(assignment.Id, "status", assignment.Role));
            }
            return new("A comparison and optional specialist check.", null,
                [new("artifact-one", "Compared alternatives", ServiceTestData.Artifact())], "Risk checker: Review assumptions.");
        }
    }

    private sealed class InvalidTeamWorkflow(bool secondRoster) : IWorkspaceWorkflow
    {
        public async Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
        {
            if (secondRoster)
            {
                await onSignal(new("planner", "roster", "No specialists needed.", Assignments: []));
                await onSignal(new("planner", "roster", "A second plan is not allowed.", Assignments: []));
            }
            else await onSignal(new("unplanned-agent", "status", "This agent was never recruited."));
            throw new InvalidOperationException("An invalid team should have been rejected.");
        }
    }

    private sealed class UnconfirmedCancellationWorkflow : IWorkspaceWorkflow
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                throw new AgentProviderException("copilot_cleanup_failed", "The provider could not confirm runtime cleanup.");
            }
            throw new InvalidOperationException("An infinite wait unexpectedly completed.");
        }
    }

    private sealed class BoundedParallelWorkflow : IWorkspaceWorkflow
    {
        private int _active;
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public int Peak { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowResult> ExecuteAsync(
            WorkflowRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _active);
            Peak = Math.Max(Peak, count);
            Interlocked.Increment(ref _started);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new("Ready.", null,
                    [new("artifact-one", "Draft", ServiceTestData.Artifact())], "A reviewable proposal.");
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
