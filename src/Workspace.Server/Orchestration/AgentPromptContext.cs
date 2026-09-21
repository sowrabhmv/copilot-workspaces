using System.Text;
using System.Text.Json;

namespace Workspace.Server.Orchestration;

internal sealed record AgentPromptContext(
    int Version, string WorkspaceId, string Objective, string JobKind,
    bool ClarificationAllowed, Dictionary<string, string> Answers,
    UiSpec? Clarification, FeedbackContext? Feedback,
    List<WorkingArtifactContext> WorkingArtifacts,
    List<SavedAgentContext> ExistingAgents,
    string? PlannerSummary = null, List<DraftArtifact>? Proposals = null,
    AgentAssignment? Assignment = null, List<AgentAssignment>? Team = null,
    List<SpecialistReview>? Reviews = null, int ReviewCharacterLimit = 8000)
{
    public static AgentPromptContext Create(WorkflowRequest request)
    {
        var workspace = request.Workspace;
        if (string.IsNullOrWhiteSpace(workspace.Id) || string.IsNullOrWhiteSpace(workspace.Objective) ||
            request.Job.Kind is not ("initial" or "answers" or "feedback" or "clarify"))
            throw Invalid("The workflow request is missing its task or has an unsupported job kind.");
        if (request.Job.Kind == "clarify")
        {
            if (workspace.Clarification is null)
                throw Invalid("A clarification refresh needs the currently saved form.");
            SpecValidator.Validate(workspace.Clarification, "clarification");
        }

        FeedbackState? feedback = null;
        if (request.Job.Kind == "feedback")
        {
            feedback = workspace.Feedback.SingleOrDefault(item => item.Id == request.Job.FeedbackId)
                ?? throw Invalid("The job's original feedback is missing.");
            if (string.IsNullOrWhiteSpace(feedback.Text) ||
                (feedback.ArtifactId is not null && feedback.Revision is null) ||
                (feedback.ArtifactId is null && (feedback.ElementId is not null || feedback.Revision is not null)))
                throw Invalid("The original feedback target is incomplete.");
        }

        var artifacts = new List<WorkingArtifactContext>();
        foreach (var artifact in workspace.Artifacts)
        {
            if (feedback?.ArtifactId is not null && artifact.Id != feedback.ArtifactId)
                continue;
            var revision = artifact.Revisions.SingleOrDefault(item => item.Revision == artifact.CurrentRevision)
                ?? throw Invalid("An artifact's latest working revision is missing.");
            SpecValidator.Validate(revision.Spec, "artifact");
            artifacts.Add(new(artifact.Id, artifact.Title, revision.Revision, artifact.AcceptedRevision, revision.Spec));
        }
        if (feedback?.ArtifactId is not null && artifacts.Count != 1)
            throw Invalid("The feedback target is not in this workspace.");

        return new(
            2, workspace.Id, workspace.Objective, request.Job.Kind,
            request.Job.Kind == "clarify" ||
                (request.Job.Kind == "initial" && workspace.Answers.Count == 0 && workspace.Artifacts.Count == 0),
            new(workspace.Answers, StringComparer.Ordinal), workspace.Clarification,
            feedback is null ? null : new(feedback.Text, feedback.ArtifactId, feedback.ElementId,
                feedback.Revision, feedback.TargetLabel, feedback.QuotedText),
            artifacts,
            workspace.Agents.Where(agent => agent.Id != AgentPlanPolicy.CoordinatorId)
                .Select(agent => new SavedAgentContext(agent.Id, agent.Name, agent.Role, agent.Assigned)).ToList());
    }

    public string ToPrompt()
    {
        var json = JsonSerializer.Serialize(this, JsonDefaults.Options);
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024)
            throw new AgentProviderException("context_too_large",
                "This workflow context is too large. Submit direction for a single artifact.");
        return json;
    }

    private static WorkspaceException Invalid(string message) => new("invalid_workflow_request", message, 500);
}

internal sealed record FeedbackContext(
    string Text, string? ArtifactId, string? ElementId, int? Revision, string TargetLabel, string? Quote);

internal sealed record WorkingArtifactContext(
    string ArtifactId, string Title, int Revision, int? AcceptedRevision, UiSpec Spec);

internal sealed record SavedAgentContext(string Id, string Name, string Role, bool Assigned);

internal sealed record SpecialistReview(string AgentId, string Name, int CandidateVersion, string Summary);
