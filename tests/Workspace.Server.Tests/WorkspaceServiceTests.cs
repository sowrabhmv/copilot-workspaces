using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class WorkspaceServiceTests
{
    [Fact]
    public async Task ConcurrentFeedbackIsDurableOrderedAndAnchoredWithoutWaitingForAgents()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            Task.Run(() => fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback($"Direction {index}")))));
        fixture.Reopen();
        var saved = fixture.Store.Get(workspace.Id);
        Assert.Equal(12, saved.Feedback.Count);
        Assert.Equal(12, saved.Jobs.Count(j => j.Status == "queued"));
        Assert.Equal(13, saved.Jobs.Select(j => j.Order).Distinct().Count());
        Assert.All(saved.Feedback, feedback =>
        {
            Assert.Equal(1, feedback.Revision);
            Assert.Equal("body", feedback.ElementId);
            Assert.Equal("Original working draft", feedback.QuotedText);
        });
        Assert.Equal(12, fixture.Store.Events(workspace.Id, workspace.EventSequence).Count);
    }

    [Fact]
    public void FeedbackRetriesAreIdempotentButKeyReuseForDifferentContentIsRejected()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var request = ServiceTestData.Feedback(requestId: $"feedback-{Guid.NewGuid()}");
        var first = fixture.Service.Feedback(workspace.Id, request);
        var second = fixture.Service.Feedback(workspace.Id, request);
        Assert.Equal(first.EventSequence, second.EventSequence);
        Assert.Single(second.Feedback);
        var error = Assert.Throws<WorkspaceException>(() => fixture.Service.Feedback(workspace.Id, request with { Text = "Different" }));
        Assert.Equal(409, error.StatusCode);
        Assert.Single(fixture.Store.Get(workspace.Id).Feedback);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("bad\nkey")]
    [InlineData("../path")]
    public void RejectsMalformedOpaqueRequestIdentifiers(string key)
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var error = Assert.Throws<WorkspaceException>(() =>
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback(requestId: key)));
        Assert.Equal("invalid_request_id", error.Code);
        Assert.Empty(fixture.Store.Get(workspace.Id).Feedback);
    }

    [Fact]
    public void ExactQueueLimitIsEnforcedWithoutDroppingEarlierNotes()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        for (var index = 0; index < 20; index++)
            fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback($"Note {index}"));
        var error = Assert.Throws<WorkspaceException>(() => fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback("Overflow")));
        Assert.Equal(429, error.StatusCode);
        Assert.Equal(20, fixture.Store.Get(workspace.Id).Feedback.Count);
    }

    [Fact]
    public void WorkspacesCannotReferenceEachOthersArtifactsAndMalformedAnchorsNeverQueue()
    {
        using var fixture = new ServiceFixture();
        ServiceTestData.Seed(fixture.Store);
        var second = fixture.Service.Create(new("Another task", "demo"));
        Assert.Throws<WorkspaceException>(() => fixture.Service.Feedback(second.Id, ServiceTestData.Feedback()));
        Assert.Throws<WorkspaceException>(() => fixture.Service.Feedback(second.Id,
            new("Bad target", Guid.NewGuid().ToString(), null, "body", 1)));
        Assert.Empty(fixture.Store.Get(second.Id).Feedback);
    }

    [Fact]
    public void HumanEditWinsOverLateAgentOutputAndConflictsCannotBeAccepted()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var queued = fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
        var job = queued.Jobs.Last();
        job.BaseRevisions["artifact-one"] = 1;
        var edited = fixture.Service.Edit(workspace.Id, "artifact-one", new(1, "body", "The human's decision"));
        Assert.Equal(2, edited.Artifacts[0].CurrentRevision);
        var result = new WorkflowResult("Refined", null,
            [new("artifact-one", "Working draft", ServiceTestData.Artifact("Late agent change"))], "Review this proposal.");
        fixture.Store.Update(workspace.Id, "test.proposal", "A late proposal arrived.", doc => WorkspaceCoordinator.Publish(doc, job, result));
        var saved = fixture.Store.Get(workspace.Id).Artifacts[0];
        Assert.Equal(2, saved.CurrentRevision);
        Assert.Equal(2, saved.AcceptedRevision);
        Assert.True(saved.Revisions.Last().Conflict);
        Assert.Equal("The human's decision", saved.Revisions[1].Spec.Elements["body"].Props["text"]!.GetValue<string>());
        Assert.Throws<WorkspaceException>(() => fixture.Service.Review(workspace.Id, "artifact-one", new(3, "accept")));
        fixture.Service.Review(workspace.Id, "artifact-one", new(3, "reject"));
        Assert.Equal(2, fixture.Store.Get(workspace.Id).Artifacts[0].CurrentRevision);
    }

    [Fact]
    public void StaleEditsAndDoubleReviewAreRejectedAndHistoryIsPreserved()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var job = new JobState { Id = "proposal-job", Kind = "feedback", Order = 2, BaseRevisions = new() { ["artifact-one"] = 1 } };
        fixture.Store.Update(workspace.Id, "test.proposal", "New draft.", doc =>
            WorkspaceCoordinator.Publish(doc, job, new("Ready", null, [new("artifact-one", "Draft", ServiceTestData.Artifact("Candidate"))], "Human judgment required.")));
        Assert.Throws<WorkspaceException>(() => fixture.Service.Edit(workspace.Id, "artifact-one", new(1, "body", "Stale")));
        var accepted = fixture.Service.Review(workspace.Id, "artifact-one", new(2, "accept"));
        Assert.Equal(2, accepted.Artifacts[0].AcceptedRevision);
        Assert.Equal(2, accepted.Artifacts[0].Revisions.Count);
        Assert.Single(accepted.Decisions);
        Assert.Throws<WorkspaceException>(() => fixture.Service.Review(workspace.Id, "artifact-one", new(2, "accept")));
    }

    [Fact]
    public void RejectingAnUnacceptedDraftDuringGenerationMakesTheLateResultConflicted()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        fixture.Store.Update(workspace.Id, "test.unaccepted", "An unaccepted initial draft.", document =>
        {
            document.Artifacts[0].AcceptedRevision = null;
            document.Artifacts[0].Revisions[0].Status = "proposed";
        });
        var job = new JobState
        {
            Id = "active-job", Kind = "feedback", Order = 2,
            BaseRevisions = new() { ["artifact-one"] = 1 }, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        fixture.Service.Review(workspace.Id, "artifact-one", new(1, "reject"));
        fixture.Store.Update(workspace.Id, "test.late", "Late proposal.", document =>
            WorkspaceCoordinator.Publish(document, job, new("Ready", null,
                [new("artifact-one", "Draft", ServiceTestData.Artifact("A late suggestion"))], "Review before accepting.")));
        var saved = fixture.Store.Get(workspace.Id).Artifacts[0];
        Assert.Equal(1, saved.CurrentRevision);
        Assert.True(saved.Revisions.Last().Conflict);
        Assert.Throws<WorkspaceException>(() => fixture.Service.Review(workspace.Id, "artifact-one", new(2, "accept")));
    }

    [Fact]
    public void RestartKeepsStateAndMarksUnfinishedJobsInterruptedRatherThanReplaying()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var queued = fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
        fixture.Service.Layout(workspace.Id, new(new() { ["context"] = new(123, 456) }, new(20, 30, 0.75)));
        fixture.Reopen();
        fixture.Store.RecoverInterrupted();
        var recovered = fixture.Store.Get(workspace.Id);
        Assert.Equal("interrupted", recovered.Jobs.Last().Status);
        Assert.Equal("interrupted", recovered.Feedback.Single().Status);
        Assert.Equal(1, recovered.Artifacts[0].AcceptedRevision);
        Assert.Equal(new(123, 456), recovered.Layout.Positions["context"]);
        var retry = fixture.Service.Retry(workspace.Id, queued.Jobs.Last().Id);
        Assert.Equal("queued", retry.Jobs.Last().Status);
        Assert.Equal(queued.Jobs.Last().Id, retry.Jobs.Last().RetryOf);
        Assert.Throws<WorkspaceException>(() => fixture.Service.Retry(workspace.Id, queued.Jobs.Last().Id));
    }

    [Fact]
    public void CancelledQueuedJobsNeverMasqueradeAsCompleted()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var queued = fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
        var cancelled = fixture.Service.Cancel(workspace.Id, queued.Jobs.Last().Id);
        Assert.Equal("cancelled", cancelled.Jobs.Last().Status);
        Assert.Equal("cancelled", cancelled.Feedback.Single().Status);
        Assert.NotNull(cancelled.Jobs.Last().CompletedAt);
        Assert.Throws<WorkspaceException>(() => fixture.Service.Cancel(workspace.Id, queued.Jobs.Last().Id));
    }

    [Fact]
    public void FreshSessionRequiresAnExplicitIdleCopilotActionAndPreservesTheAuditReference()
    {
        using var fixture = new ServiceFixture();
        var workspace = fixture.Service.Create(new("A live task with saved user judgment", "copilot"));
        var oldSession = Guid.NewGuid().ToString();
        var otherSession = Guid.NewGuid().ToString();
        fixture.Store.Update(workspace.Id, "test.sessions", "Session references saved.", document =>
        {
            document.Agents[0].SessionId = oldSession;
            document.Agents.Add(new()
            {
                Id = "writer", Name = "Earlier specialist", Role = "A saved role from an earlier task pass",
                SessionId = otherSession, Assigned = false
            });
            document.Artifacts.Add(new()
            {
                Id = "artifact-one", Title = "Keep my work", CurrentRevision = 1, AcceptedRevision = 1,
                Revisions = [new() { Revision = 1, Spec = ServiceTestData.Artifact("Human judgment"), Source = "human", Status = "accepted" }]
            });
        });
        var busy = Assert.Throws<WorkspaceException>(() => fixture.Service.ResetSession(workspace.Id, "planner"));
        Assert.Equal(409, busy.StatusCode);
        fixture.Service.Cancel(workspace.Id, workspace.Jobs[0].Id);
        var recovered = fixture.Service.ResetSession(workspace.Id, "planner");
        Assert.Null(recovered.Agents[0].SessionId);
        Assert.Equal(otherSession, recovered.Agents[1].SessionId);
        Assert.Equal(1, recovered.Artifacts[0].AcceptedRevision);
        var audit = Assert.Single(fixture.Store.Events(workspace.Id, 0), e => e.Type == "agent.session_reset");
        Assert.Contains(oldSession, audit.Message);
        Assert.Equal("planner", audit.AgentId);
        Assert.Equal(recovered.EventSequence, fixture.Service.ResetSession(workspace.Id, "planner").EventSequence);
        Assert.Throws<WorkspaceException>(() => fixture.Service.ResetSession(workspace.Id, "foreign-agent"));
        var demo = fixture.Service.Create(new("A demo task", "demo"));
        Assert.Throws<WorkspaceException>(() => fixture.Service.ResetSession(demo.Id, "planner"));
    }

    [Fact]
    public void FormSubmissionIsRevisionBoundAndLayoutRejectsForeignNodes()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        fixture.Store.Update(workspace.Id, "test.questions", "Needs input.", doc =>
        {
            doc.ClarificationId = "form-current";
            doc.Clarification = ServiceTestData.Questions();
        });
        Assert.Throws<WorkspaceException>(() => fixture.Service.Answer(workspace.Id, new("form-old", new() { ["audience"] = "team" })));
        var answered = fixture.Service.Answer(workspace.Id, new("form-current", new() { ["audience"] = "team" }));
        Assert.Null(answered.Clarification);
        Assert.Equal("team", answered.Answers["audience"]);
        Assert.Equal("answers", answered.Jobs.Last().Kind);
        Assert.Throws<WorkspaceException>(() => fixture.Service.Layout(workspace.Id,
            new(new() { ["foreign"] = new(0, 0) }, new(0, 0, 1))));
        Assert.Throws<WorkspaceException>(() => fixture.Service.Layout(workspace.Id,
            new([], new(0, 0, 0.299))));
    }

    [Fact]
    public async Task EventReplayIsWorkspaceScopedAndDisconnectDoesNotCancelJobs()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        var other = fixture.Service.Create(new("Independent workspace", "demo"));
        fixture.Service.Feedback(other.Id, new("Unrelated note", Guid.NewGuid().ToString()));
        var changed = fixture.Service.Feedback(workspace.Id, ServiceTestData.Feedback());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var stream = HttpApi.Stream(fixture.Store, workspace.Id, workspace.EventSequence, cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal("connected", stream.Current.Data.Type);
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(changed.EventSequence, stream.Current.Data.Sequence);
            Assert.Equal(workspace.Id, stream.Current.Data.WorkspaceId);
        }
        Assert.Equal("queued", fixture.Store.Get(workspace.Id).Jobs.Last().Status);
    }
}
