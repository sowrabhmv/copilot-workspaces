namespace Workspace.Server;

public sealed class WorkspaceService(WorkspaceStore store)
{
    public WorkspaceDocument Create(CreateWorkspaceRequest request)
    {
        var objective = BoundedText(request.Objective, 12000, "Describe the outcome in 1-12,000 characters.");
        if (request.Provider is not ("copilot" or "demo"))
            throw new WorkspaceException("invalid_provider", "Choose Copilot or the explicitly labelled demo.");
        return store.Create(objective, request.Provider);
    }

    public WorkspaceDocument Answer(string id, AnswerRequest request) =>
        store.Update(id, "clarification.answered", "Your decisions were saved. Drafting is queued.", document =>
        {
            if (document.Clarification is null || document.ClarificationId != request.ClarificationId)
                throw new WorkspaceException("stale_clarification", "This form is no longer current. Refresh the workspace.", 409);
            if (document.Jobs.Any(job => job.Kind == "clarify" && job.Status is "queued" or "running" or "cancelling"))
                throw new WorkspaceException("clarification_refreshing", "The question layout is being refreshed. Wait or cancel that run before confirming answers.", 409);
            EnsureQueueCapacity(document);
            document.Answers = SpecValidator.ValidateAnswers(document.Clarification, request.Answers);
            document.Clarification = null;
            document.ClarificationId = null;
            document.Jobs.Add(NewJob(document, "answers"));
        });

    public WorkspaceDocument RefreshClarification(string id, RefreshClarificationRequest request) =>
        store.Update(id, "clarification.refresh_requested", "The coordinator is updating the question layout with appropriate visual controls.", document =>
        {
            if (document.Clarification is null || document.ClarificationId != request.ClarificationId)
                throw new WorkspaceException("stale_clarification", "Only the current unanswered form can be refreshed.", 409);
            if (document.Jobs.Any(job => job.Status is "queued" or "running" or "cancelling"))
                throw new WorkspaceException("workspace_busy", "Finish or cancel active work before regenerating the question layout.", 409);
            EnsureQueueCapacity(document);
            document.Jobs.Add(NewJob(document, "clarify"));
        });

    public WorkspaceDocument Feedback(string id, FeedbackRequest request)
    {
        var text = BoundedText(request.Text, 4000, "Direction must contain 1-4,000 characters.");
        if (request.ClientRequestId is not { Length: >= 1 and <= 128 } requestId ||
            !IsRequestIdCharacter(requestId[0], punctuation: false) ||
            requestId.Any(character => !IsRequestIdCharacter(character, punctuation: true)))
            throw new WorkspaceException("invalid_request_id", "Feedback needs a stable 1-128 character request identifier.");
        return store.Change(id, "feedback.queued", "Your direction is saved in the queue. You can keep working.", document =>
        {
            var existing = document.Feedback.Find(f => f.ClientRequestId == request.ClientRequestId);
            if (existing is not null)
            {
                if (existing.Text != text || existing.ArtifactId != request.ArtifactId ||
                    existing.ElementId != request.ElementId || existing.Revision != request.Revision)
                    throw new WorkspaceException("idempotency_conflict", "This request identifier belongs to different feedback.", 409);
                return false;
            }
            EnsureQueueCapacity(document);
            var label = "Workspace direction";
            string? quote = null;
            if (request.ArtifactId is not null)
            {
                var artifact = Artifact(document, request.ArtifactId);
                var revision = artifact.Revisions.Find(r => r.Revision == request.Revision)
                    ?? throw new WorkspaceException("invalid_anchor", "Select a saved artifact revision.");
                if (request.ElementId is null || !revision.Spec.Elements.TryGetValue(request.ElementId, out var element))
                    throw new WorkspaceException("invalid_anchor", "Select a block in this artifact.");
                quote = SpecValidator.ElementText(element);
                label = quote is { Length: > 0 } ? quote[..Math.Min(quote.Length, 120)] : $"{artifact.Title} / {element.Type}";
            }
            else if (request.ElementId is not null || request.Revision is not null)
                throw new WorkspaceException("invalid_anchor", "A block anchor must include its artifact.");
            var feedbackId = Guid.NewGuid().ToString("N");
            var job = NewJob(document, "feedback", feedbackId);
            document.Jobs.Add(job);
            document.Feedback.Add(new()
            {
                Id = feedbackId,
                ClientRequestId = request.ClientRequestId,
                JobId = job.Id,
                Text = text,
                ArtifactId = request.ArtifactId,
                ElementId = request.ElementId,
                Revision = request.Revision,
                TargetLabel = label,
                QuotedText = quote,
                Status = "queued"
            });
            return true;
        });
    }

    public WorkspaceDocument Cancel(string id, string jobId) =>
        store.Update(id, "job.cancel_requested", "Cancellation requested.", document =>
        {
            var job = Job(document, jobId);
            if (job.Status is not ("queued" or "running" or "cancelling"))
                throw new WorkspaceException("job_not_active", "This run is no longer active.", 409);
            job.Status = job.Status == "queued" ? "cancelled" : "cancelling";
            job.Message = job.Status == "cancelled" ? "Cancelled before execution." : "Stopping the active agent safely.";
            if (job.Status == "cancelled") job.CompletedAt = DateTimeOffset.UtcNow;
            var feedback = document.Feedback.Find(f => f.Id == job.FeedbackId);
            if (feedback is not null) feedback.Status = job.Status;
        }, jobId);

    public WorkspaceDocument Retry(string id, string jobId) =>
        store.Update(id, "job.retried", "A new attempt is queued with the original context.", document =>
        {
            var previous = Job(document, jobId);
            if (previous.Status is not ("failed" or "cancelled" or "interrupted"))
                throw new WorkspaceException("job_not_retryable", "Only failed, cancelled or interrupted work can be retried.", 409);
            if (document.Jobs.Any(j => j.RetryOf == previous.Id))
                throw new WorkspaceException("already_retried", "This run already has a retry. Use that attempt's controls.", 409);
            EnsureQueueCapacity(document);
            var next = NewJob(document, previous.Kind, previous.FeedbackId, previous.Id);
            document.Jobs.Add(next);
            var feedback = document.Feedback.Find(f => f.Id == previous.FeedbackId);
            if (feedback is not null)
            {
                feedback.JobId = next.Id;
                feedback.Status = "queued";
            }
        }, jobId);

    public WorkspaceDocument ResetSession(string id, string agentId)
    {
        var previous = store.Get(id).Agents.Find(a => a.Id == agentId)
            ?? throw new WorkspaceException("agent_not_found", "This agent does not belong to the workspace.", 404);
        var message = $"You requested a fresh {previous.Name} conversation. Previous session reference: {previous.SessionId ?? "none"}. Artifacts and feedback are preserved.";
        return store.Change(id, "agent.session_reset", message, document =>
        {
            if (document.Provider != "copilot")
                throw new WorkspaceException("no_live_session", "Demo workspaces do not have live Copilot conversations to reset.");
            if (document.Jobs.Any(j => j.Status is "queued" or "running" or "cancelling"))
                throw new WorkspaceException("workspace_busy", "Finish or cancel all queued work before starting a fresh session.", 409);
            var agent = document.Agents.Single(a => a.Id == agentId);
            if (agent.SessionId is null) return false;
            agent.SessionId = null;
            agent.Status = "idle";
            agent.ActiveJobId = null;
            agent.Message = "A fresh Copilot conversation will start on your next explicit retry. Your workspace context is preserved.";
            return true;
        }, agentId: agentId);
    }

    public WorkspaceDocument Review(string id, string artifactId, ReviewRequest request) =>
        store.Update(id, "artifact.reviewed", request.Decision == "accept" ? "You accepted this revision." : "You kept control by rejecting this proposal.",
            document =>
            {
                if (request.Decision is not ("accept" or "reject"))
                    throw new WorkspaceException("invalid_decision", "Choose accept or reject.");
                var artifact = Artifact(document, artifactId);
                var revision = artifact.Revisions.Find(r => r.Revision == request.Revision)
                    ?? throw new WorkspaceException("revision_not_found", "This revision was not found.", 404);
                if (revision.Status != "proposed")
                    throw new WorkspaceException("review_not_pending", "This proposal has already been resolved or superseded.", 409);
                if (request.Decision == "accept" && (revision.Conflict || artifact.CurrentRevision != revision.Revision))
                    throw new WorkspaceException("revision_conflict", "Your working version changed. Review the conflict and request a fresh revision.", 409);
                revision.Status = request.Decision == "accept" ? "accepted" : "rejected";
                if (request.Decision == "accept") artifact.AcceptedRevision = revision.Revision;
                else if (artifact.CurrentRevision == revision.Revision && artifact.AcceptedRevision is int accepted)
                    artifact.CurrentRevision = accepted;
                document.Decisions.Add(new(Guid.NewGuid().ToString("N"), artifactId, revision.Revision,
                    request.Decision, DateTimeOffset.UtcNow));
                RefreshFeedbackReview(document, revision.JobId);
            });

    public WorkspaceDocument Edit(string id, string artifactId, EditRequest request) =>
        store.Update(id, "artifact.edited", "Your edit is saved as a human-approved revision.", document =>
        {
            var artifact = Artifact(document, artifactId);
            if (artifact.CurrentRevision != request.Revision)
                throw new WorkspaceException("revision_conflict", "This artifact changed. Review the newest version before editing.", 409);
            var previous = artifact.Revisions.Single(r => r.Revision == request.Revision);
            var spec = JsonDefaults.Clone(previous.Spec);
            if (string.IsNullOrWhiteSpace(request.ElementId) ||
                !spec.Elements.TryGetValue(request.ElementId, out var element) || element.Type is not ("Text" or "Heading"))
                throw new WorkspaceException("not_text_editable", "Direct editing is available for Text and Heading blocks.");
            element.Props["text"] = BoundedText(request.Text, element.Type == "Heading" ? 180 : 8000,
                "Enter text within this block's length limit.");
            SpecValidator.Validate(spec, "artifact");
            if (previous.Status == "proposed") previous.Status = "superseded";
            var number = artifact.Revisions.Max(r => r.Revision) + 1;
            artifact.Revisions.Add(new()
            {
                Revision = number, BaseRevision = previous.Revision, Spec = spec,
                Source = "human", Status = "accepted", Review = "Edited and approved by you."
            });
            artifact.CurrentRevision = number;
            artifact.AcceptedRevision = number;
            document.Decisions.Add(new(Guid.NewGuid().ToString("N"), artifactId, number, "edit", DateTimeOffset.UtcNow));
            RefreshFeedbackReview(document, previous.JobId);
        });

    public WorkspaceDocument Layout(string id, CanvasLayout layout)
    {
        if (layout.Positions is null || layout.Viewport is null || layout.Positions.Count > 32 ||
            !Finite(layout.Viewport.X) || !Finite(layout.Viewport.Y) ||
            !double.IsFinite(layout.Viewport.Zoom) || layout.Viewport.Zoom is < 0.3 or > 2)
            throw new WorkspaceException("invalid_layout", "Canvas coordinates or zoom are outside the supported range.");
        return store.Change(id, "canvas.moved", "Canvas arrangement saved.", document =>
        {
            var ids = new HashSet<string>(document.Agents.Select(a => $"agent-{a.Id}")) { "context" };
            ids.UnionWith(document.Artifacts.Select(a => $"artifact-{a.Id}"));
            foreach (var (nodeId, position) in layout.Positions)
                if (!ids.Contains(nodeId) || position is null || !Finite(position.X) || !Finite(position.Y))
                    throw new WorkspaceException("invalid_layout", "This canvas node or coordinate is not valid.");
            if (document.Layout.Viewport == layout.Viewport &&
                document.Layout.Positions.Count == layout.Positions.Count &&
                layout.Positions.All(p => document.Layout.Positions.TryGetValue(p.Key, out var old) && old == p.Value))
                return false;
            document.Layout = layout;
            return true;
        });
    }

    public static ArtifactState Artifact(WorkspaceDocument document, string id) =>
        document.Artifacts.Find(a => a.Id == id)
        ?? throw new WorkspaceException("artifact_not_found", "This artifact does not belong to the workspace.", 404);

    public static JobState Job(WorkspaceDocument document, string id) =>
        document.Jobs.Find(j => j.Id == id)
        ?? throw new WorkspaceException("job_not_found", "This run does not belong to the workspace.", 404);

    public static void RefreshFeedbackReview(WorkspaceDocument document, string? jobId)
    {
        if (jobId is null) return;
        var feedback = document.Feedback.Find(f => f.JobId == jobId);
        if (feedback is null) return;
        var revisions = document.Artifacts.SelectMany(a => a.Revisions).Where(r => r.JobId == jobId).ToList();
        feedback.Status = revisions.Any(r => r.Status == "proposed") ? "review"
            : revisions.Any(r => r.Status == "accepted") ? "accepted"
            : revisions.Any(r => r.Status == "rejected") ? "rejected" : "completed";
    }

    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 20000;

    private static bool IsRequestIdCharacter(char character, bool punctuation) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        punctuation && character is '-' or '_' or '.' or ':';

    private static JobState NewJob(WorkspaceDocument document, string kind, string? feedback = null, string? retry = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind,
            Order = document.Jobs.Count == 0 ? 1 : document.Jobs.Max(j => j.Order) + 1,
            FeedbackId = feedback, RetryOf = retry
        };

    private static void EnsureQueueCapacity(WorkspaceDocument document)
    {
        if (document.Jobs.Count >= 300)
            throw new WorkspaceException("history_limit", "This MVP workspace reached its 300-run history limit. Start a new task.", 409);
        if (document.Jobs.Count(j => j.Status is "queued" or "running" or "cancelling") >= 20)
            throw new WorkspaceException("queue_full", "Twenty requests are already active or queued. Wait for one to finish.", 429);
    }

    private static string BoundedText(string? text, int max, string message)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Length > max)
            throw new WorkspaceException("invalid_input", message);
        return value;
    }
}
