namespace Workspace.Server;

public sealed class WorkspaceOptions
{
    public int Port { get; set; } = 5080;
    public string DataDirectory { get; set; } = "";
    public string? CopilotExecutable { get; set; }
    public int ProviderTimeoutSeconds { get; set; } = 180;
    public int DemoDelayMilliseconds { get; set; } = 500;
    public string? AgentPackDirectory { get; set; }
    public const string Model = "auto";
    public const string? ReasoningEffort = null;
    public const int MaximumSpecialists = 4;
    public const int MaximumSavedSpecialists = 12;
}

public sealed record ProviderStatus(
    string Id, string State, string Message, string Model, string? ReasoningEffort,
    string? SdkVersion = null, string? CliVersion = null, string? SetupCommand = null);

public sealed record AgentRequest(
    string WorkspaceId, string AgentId, string? SessionId,
    string Instructions, string Prompt, string OutputKind);

public sealed record AgentSignal(
    string AgentId, string Kind, string Message, string? SessionId = null,
    IReadOnlyList<AgentAssignment>? Assignments = null);

public sealed record AgentAssignment(string Id, string Name, string Role, string Kind);

public sealed record AgentReply(string Text, string SessionId);

public interface IAgentProvider
{
    string Name { get; }
    Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<AgentReply> CompleteAsync(
        AgentRequest request, Func<AgentSignal, Task> onSignal,
        CancellationToken cancellationToken);
}

public sealed class AgentProviderException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record WorkflowRequest(WorkspaceDocument Workspace, JobState Job);
public sealed record DraftArtifact(string? ArtifactId, string Title, UiSpec Spec);
public sealed record PlannerResponse(
    string Summary, UiSpec? Clarification,
    List<AgentAssignment>? Agents = null, List<DraftArtifact>? Artifacts = null);
public sealed record ProducerResponse(List<DraftArtifact> Artifacts);
public sealed record ReviewerResponse(string Summary);
public sealed record WorkflowResult(
    string Summary, UiSpec? Clarification, List<DraftArtifact> Artifacts, string? Review);

public interface IWorkspaceWorkflow
{
    Task<WorkflowResult> ExecuteAsync(
        WorkflowRequest request, Func<AgentSignal, Task> onSignal,
        CancellationToken cancellationToken);
}
