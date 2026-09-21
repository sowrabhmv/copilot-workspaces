using System.Runtime.CompilerServices;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

[assembly: InternalsVisibleTo("Workspace.Server.Tests")]

namespace Workspace.Server.Providers;

internal interface ICopilotRuntimeFactory
{
    ICopilotRuntime Create(CopilotClientOptions options);
}

internal interface ICopilotRuntime : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<GetStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken cancellationToken);
    Task<IList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
    Task<ICopilotSession> OpenSessionAsync(CopilotSessionStart start, CancellationToken cancellationToken);
    Task PingAsync(CancellationToken cancellationToken);
    Task StopAsync();
    Task ForceStopAsync();
}

internal interface ICopilotSession
{
    string SessionId { get; }
    Task<CopilotModelPolicy> ApplyModelPolicyAsync(CancellationToken cancellationToken);
    Task<CopilotSessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<string?> SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken);
    Task AbortAsync(CancellationToken cancellationToken);
}

internal sealed record CopilotSessionStart(
    string SessionId, bool Resume, string WorkingDirectory, string Instructions,
    Action<CopilotRuntimeEvent> OnEvent);

internal sealed record CopilotModelPolicy(
    IReadOnlyList<string>? AllowedModels, IReadOnlyList<string>? EffectiveAllowedModels,
    string? FallbackModel);

internal sealed record CopilotSessionSnapshot(
    string? Model, string? ReasoningEffort, IReadOnlyList<string>? Tools);

internal enum CopilotRuntimeEventKind
{
    Delta,
    ToolStarted,
    Error
}

internal readonly record struct CopilotRuntimeEvent(CopilotRuntimeEventKind Kind, int Characters = 0);

internal sealed class CopilotSdkRuntimeFactory : ICopilotRuntimeFactory
{
    public ICopilotRuntime Create(CopilotClientOptions options) =>
        new CopilotSdkRuntime(new CopilotClient(options));
}

// The pinned SDK exposes policy RPCs and permission decisions as GHCP001.
#pragma warning disable GHCP001
internal sealed class CopilotSdkRuntime(CopilotClient client) : ICopilotRuntime
{
    public Task StartAsync(CancellationToken cancellationToken) => client.StartAsync(cancellationToken);
    public Task<GetStatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        client.GetStatusAsync(cancellationToken);
    public Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken cancellationToken) =>
        client.GetAuthStatusAsync(cancellationToken);
    public Task<IList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) =>
        client.ListModelsAsync(cancellationToken);

    public async Task<ICopilotSession> OpenSessionAsync(
        CopilotSessionStart start, CancellationToken cancellationToken)
    {
        var config = CreateConfiguration(start);
        var session = config switch
        {
            ResumeSessionConfig resume => await client.ResumeSessionAsync(
                start.SessionId, resume, cancellationToken),
            SessionConfig create => await client.CreateSessionAsync(create, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported session configuration.")
        };
        return new CopilotSdkSession(session);
    }

    internal static SessionConfigBase CreateConfiguration(CopilotSessionStart start)
    {
        SessionConfigBase config = start.Resume
            ? new ResumeSessionConfig { ContinuePendingWork = false }
            : new SessionConfig { SessionId = start.SessionId };

        config.ClientName = "copilot-workspaces";
        config.Model = WorkspaceOptions.Model;
        config.ReasoningEffort = WorkspaceOptions.ReasoningEffort;
        config.Streaming = true;
        config.WorkingDirectory = start.WorkingDirectory;
        config.AdditionalDirectories = [];
        config.AvailableTools = [];
        config.ExcludedTools = ["builtin:*", "mcp:*", "custom:*"];
        config.Tools = [];
        config.McpServers = new Dictionary<string, McpServerConfig>();
        config.DisabledMcpServers = ["github-mcp-server"];
        config.CustomAgents = [];
        config.CustomAgentsLocalOnly = true;
        config.Commands = [];
        config.SkillDirectories = [];
        config.IncludedBuiltinSkills = [];
        config.PluginDirectories = [];
        config.InstructionDirectories = [];
        config.EnableConfigDiscovery = false;
        config.SkipCustomInstructions = true;
        config.EnableOnDemandInstructionDiscovery = false;
        config.EnableFileHooks = false;
        config.EnableHostGitOperations = false;
        config.EnableSkills = false;
        config.EnableSessionStore = false;
        config.EnableSessionTelemetry = false;
        config.EnableExperimentalMode = false;
        config.EnableFileChangeTracking = false;
        config.SkipEmbeddingRetrieval = true;
        config.EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory;
        config.McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory;
        config.Memory = new MemoryConfiguration { Enabled = false };
        config.RequestExtensions = false;
        config.RequestCanvasRenderer = false;
        config.ManageScheduleEnabled = false;
        config.RemoteSession = RemoteSessionMode.Off;
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = true };
        config.SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Append,
            Content = start.Instructions
        };
        config.OnPermissionRequest = (_, _) =>
            Task.FromResult(PermissionDecision.Reject("Workspace agents cannot execute tools or external actions."));
        config.OnEvent = evt => ObserveEvent(evt, start.OnEvent);
        return config;
    }

    internal static void ObserveEvent(SessionEvent evt, Action<CopilotRuntimeEvent> onEvent)
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                onEvent(delta.Data?.DeltaContent is { } content
                    ? new(CopilotRuntimeEventKind.Delta, content.Length)
                    : new(CopilotRuntimeEventKind.Error));
                break;
            case ToolExecutionStartEvent:
                onEvent(new(CopilotRuntimeEventKind.ToolStarted));
                break;
            case SessionErrorEvent:
                onEvent(new(CopilotRuntimeEventKind.Error));
                break;
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken) =>
        _ = await client.PingAsync(cancellationToken: cancellationToken);
    public Task StopAsync() => client.StopAsync();
    public Task ForceStopAsync() => client.ForceStopAsync();
    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal sealed class CopilotSdkSession(CopilotSession session) : ICopilotSession
{
    public string SessionId => session.SessionId;

    public async Task<CopilotModelPolicy> ApplyModelPolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await session.Rpc.Model.SetAllowedModelsAsync(
            allowedModels: null, cancellationToken: cancellationToken);
        return new(policy.AllowedModels?.ToArray(), policy.EffectiveAllowedModels?.ToArray(),
            policy.FallbackModel);
    }

    public async Task<CopilotSessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var model = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
        await session.Rpc.Tools.InitializeAndValidateAsync(cancellationToken);
        var tools = await session.Rpc.Tools.GetCurrentMetadataAsync(cancellationToken);
        return new(model.ModelId, model.ReasoningEffort, tools.Tools?.Select(tool => tool.Name).ToArray());
    }

    public async Task<string?> SendAndWaitAsync(
        string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = prompt,
            Source = MessageSource.System,
            Mode = "enqueue"
        }, timeout, cancellationToken);
        return response?.Data.Content;
    }

    public Task AbortAsync(CancellationToken cancellationToken) => session.AbortAsync(cancellationToken);
}
#pragma warning restore GHCP001
