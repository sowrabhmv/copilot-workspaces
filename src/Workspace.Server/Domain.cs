using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workspace.Server;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)
        ?? throw new InvalidDataException("Stored workspace data could not be read.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiSpec(string Root, Dictionary<string, UiElement> Elements);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiElement(string Type, JsonObject Props, List<string> Children);

public sealed class WorkspaceDocument
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public required string Objective { get; init; }
    public required string Provider { get; init; }
    public string Status { get; set; } = "queued";
    public string Summary { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Revision { get; set; }
    public long EventSequence { get; set; }
    public string? ClarificationId { get; set; }
    public UiSpec? Clarification { get; set; }
    public Dictionary<string, string> Answers { get; set; } = [];
    public List<AgentState> Agents { get; set; } = [];
    public List<ArtifactState> Artifacts { get; set; } = [];
    public List<JobState> Jobs { get; set; } = [];
    public List<FeedbackState> Feedback { get; set; } = [];
    public List<HumanDecision> Decisions { get; set; } = [];
    public CanvasLayout Layout { get; set; } = new([], new(36, 36, 0.85));
}

public sealed class AgentState : IJsonOnDeserialized
{
    private bool _assigned = true;
    private bool _assignmentSpecified;

    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Role { get; set; }
    public bool Assigned
    {
        get => _assigned;
        set { _assigned = value; _assignmentSpecified = true; }
    }
    public string Status { get; set; } = "idle";
    public string Message { get; set; } = "Ready when you are.";
    public string? SessionId { get; set; }
    public string? ActiveJobId { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        // The original app pre-created idle specialist placeholders before any recruitment.
        if (!_assignmentSpecified && Id != AgentPlanPolicy.CoordinatorId &&
            string.IsNullOrEmpty(SessionId) && Status is "idle" or "waiting")
            _assigned = false;
    }
}

public sealed class ArtifactState
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public int CurrentRevision { get; set; }
    public int? AcceptedRevision { get; set; }
    public List<ArtifactRevision> Revisions { get; set; } = [];
}

public sealed class ArtifactRevision
{
    public required int Revision { get; init; }
    public int? BaseRevision { get; init; }
    public required UiSpec Spec { get; init; }
    public string Status { get; set; } = "proposed";
    public required string Source { get; init; }
    public string? JobId { get; init; }
    public string? Review { get; init; }
    public bool Conflict { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class JobState
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required int Order { get; init; }
    public string Status { get; set; } = "queued";
    public string Message { get; set; } = "Waiting for an agent.";
    public string? FeedbackId { get; init; }
    public string? RetryOf { get; init; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Dictionary<string, int> BaseRevisions { get; set; } = [];
}

public sealed class FeedbackState
{
    public required string Id { get; init; }
    public required string ClientRequestId { get; init; }
    public required string JobId { get; set; }
    public required string Text { get; init; }
    public string? ArtifactId { get; init; }
    public string? ElementId { get; init; }
    public int? Revision { get; init; }
    public required string TargetLabel { get; init; }
    public string? QuotedText { get; init; }
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record HumanDecision(
    string Id, string ArtifactId, int Revision, string Kind, DateTimeOffset CreatedAt);

public sealed record CanvasPosition(double X, double Y);
public sealed record CanvasViewport(double X, double Y, double Zoom);
public sealed record CanvasLayout(Dictionary<string, CanvasPosition> Positions, CanvasViewport Viewport);

public sealed record WorkspaceSummary(
    string Id, string Title, string Objective, string Provider, string Status,
    DateTimeOffset UpdatedAt, int ActiveJobs, int PendingFeedback, int PendingReviews, int ArtifactCount);

public sealed record WorkspaceEvent(
    long Sequence, string WorkspaceId, string Type, string Message,
    string? JobId, string? AgentId, DateTimeOffset CreatedAt);

public sealed record CreateWorkspaceRequest(string Objective, string Provider = "copilot");
public sealed record AnswerRequest(string ClarificationId, Dictionary<string, string> Answers);
public sealed record RefreshClarificationRequest(string ClarificationId);
public sealed record FeedbackRequest(
    string Text, string ClientRequestId, string? ArtifactId = null,
    string? ElementId = null, int? Revision = null);
public sealed record ReviewRequest(int Revision, string Decision);
public sealed record EditRequest(int Revision, string ElementId, string Text);
public sealed record ApiError(string Code, string Message);

public sealed class WorkspaceException(string code, string message, int statusCode = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
