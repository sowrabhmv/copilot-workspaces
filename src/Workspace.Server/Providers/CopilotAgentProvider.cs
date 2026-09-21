using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Workspace.Server.Providers;

public sealed class CopilotAgentProvider : IAgentProvider
{
    internal const int MaximumResponseCharacters = 1_048_576;
    private const string SdkVersion = "1.0.14";
    private readonly WorkspaceOptions _options;
    private readonly ILogger<CopilotAgentProvider> _logger;
    private readonly ICopilotRuntimeFactory _runtimeFactory;
    private readonly CopilotProviderTiming _timing;

    public CopilotAgentProvider(WorkspaceOptions options, ILogger<CopilotAgentProvider> logger)
        : this(options, logger, new CopilotSdkRuntimeFactory(), CopilotProviderTiming.Default) { }

    internal CopilotAgentProvider(
        WorkspaceOptions options, ILogger<CopilotAgentProvider> logger,
        ICopilotRuntimeFactory runtimeFactory, CopilotProviderTiming timing)
    {
        _options = options;
        _logger = logger;
        _runtimeFactory = runtimeFactory;
        _timing = timing;
    }

    public string Name => "copilot";

    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var operation = new CopilotOperation();
        try
        {
            return await RunOwnedAsync(operation, _timing.StatusTimeout, cancellationToken,
                async (runtime, token) =>
                {
                    await PreflightAsync(runtime, operation, token);
                    return Status(operation, "ready",
                        "Copilot is authenticated. Auto selects an available model for the task; every run verifies Auto selection and the no-tools policy.");
                });
        }
        catch (AgentProviderException error)
        {
            var state = error.Code switch
            {
                "copilot_auth_required" or "copilot_executable_missing" => "setupRequired",
                "copilot_protocol_unsupported" or "copilot_model_unsupported" or
                    "copilot_policy_unverified" => "unsupported",
                _ => "unavailable"
            };
            return Status(operation, state, error.Message,
                error.Code == "copilot_auth_required" ? operation.SetupCommand : null);
        }
    }

    public async Task<AgentReply> CompleteAsync(
        AgentRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(onSignal);
        if (_options.ProviderTimeoutSeconds is < 1 or > 900)
            throw new AgentProviderException("copilot_configuration_invalid",
                "ProviderTimeoutSeconds must be between 1 and 900.");

        var operation = new CopilotOperation { WorkspaceId = request.WorkspaceId };
        var timeout = TimeSpan.FromSeconds(_options.ProviderTimeoutSeconds);
        return await RunOwnedAsync(operation, timeout, cancellationToken, async (runtime, token) =>
        {
            await PublishAsync(onSignal, new(request.AgentId, "status", "Checking Copilot availability."), token);
            await PreflightAsync(runtime, operation, token);
            operation.Stage = CopilotStage.Session;
            var events = new CopilotEventState();
            var sessionId = request.SessionId ?? Guid.NewGuid().ToString("D");
            var start = new CopilotSessionStart(
                sessionId, request.SessionId is not null, operation.WorkingDirectory!,
                request.Instructions, events.Observe);
            token.ThrowIfCancellationRequested();
            var session = await runtime.OpenSessionAsync(start, token).WaitAsync(token);
            operation.Session = session;
            if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                throw new AgentProviderException("copilot_session_mismatch",
                    "Copilot did not return the requested session. The run was stopped to protect workspace isolation.");

            await PublishSessionAsync(onSignal,
                new(request.AgentId, "session", "Copilot session connected.", session.SessionId));
            token.ThrowIfCancellationRequested();
            events.ThrowIfFaulted();
            operation.Stage = CopilotStage.Policy;
            var policy = await session.ApplyModelPolicyAsync(token).WaitAsync(token);
            RequireModelPolicy(policy);
            RequireSnapshot(await session.GetSnapshotAsync(token).WaitAsync(token));
            events.ThrowIfFaulted();
            await PublishAsync(onSignal,
                new(request.AgentId, "status", "Working with Copilot Auto model selection."), token);
            operation.Stage = CopilotStage.Turn;
            token.ThrowIfCancellationRequested();
            var turn = session.SendAndWaitAsync(request.Prompt, timeout, token);
            ObserveLateFailure(turn);
            var text = await WaitForTurnAsync(runtime, turn, events, request.AgentId, onSignal, token);
            events.ThrowIfFaulted();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumResponseCharacters)
                throw new AgentProviderException("copilot_invalid_output",
                    "Copilot did not return a nonempty response within the supported output limit.");

            operation.Stage = CopilotStage.Policy;
            RequireSnapshot(await session.GetSnapshotAsync(token).WaitAsync(token));
            events.ThrowIfFaulted();
            token.ThrowIfCancellationRequested();
            return new AgentReply(text, session.SessionId);
        });
    }

    private async Task<T> RunOwnedAsync<T>(
        CopilotOperation operation, TimeSpan timeout, CancellationToken callerToken,
        Func<ICopilotRuntime, CancellationToken, Task<T>> body)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        var operationToken = linked.Token;
        ICopilotRuntime? runtime = null;
        var succeeded = false;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var prepare = Task.Run(() => PrepareDirectories(operation, operationToken), operationToken);
            ObserveLateFailure(prepare);
            await prepare.WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            runtime = _runtimeFactory.Create(CreateClientOptions(operation));
            operation.Stage = CopilotStage.Start;
            await runtime.StartAsync(linked.Token).WaitAsync(linked.Token);
            var work = body(runtime, operationToken);
            ObserveLateFailure(work);
            var result = await work.WaitAsync(operationToken);
            linked.Token.ThrowIfCancellationRequested();
            succeeded = true;
            return result;
        }
        catch (Exception error)
        {
            _logger.LogWarning("Copilot operation failed at {Stage} ({ErrorType}).",
                operation.Stage, error.GetType().Name);
            if (callerToken.IsCancellationRequested)
                throw new OperationCanceledException("The Copilot operation was cancelled.", callerToken);
            if (deadline.IsCancellationRequested || error is TimeoutException)
                throw new AgentProviderException("copilot_timeout",
                    "The Copilot operation exceeded its deadline and was stopped. Retry explicitly when ready.");
            if (error is AgentProviderException) throw;
            if (error.Message.StartsWith("SDK protocol version mismatch:", StringComparison.Ordinal))
                throw new AgentProviderException("copilot_protocol_unsupported",
                    "This Copilot CLI does not support SDK protocol version 3. Configure a compatible executable.");
            throw new AgentProviderException(
                operation.Stage switch
                {
                    CopilotStage.Policy => "copilot_policy_unverified",
                    CopilotStage.Session => "copilot_session_unavailable",
                    _ => "copilot_unavailable"
                },
                operation.Stage switch
                {
                    CopilotStage.Configuration => "Copilot's application-owned runtime directories could not be prepared.",
                    CopilotStage.Start => "Copilot CLI could not start. Check the configured executable and its runtime installation.",
                    CopilotStage.Session => "The Copilot session could not be opened. Existing context was not replaced or silently reset.",
                    CopilotStage.Policy => "Copilot could not verify the required model and no-tools policy. This runtime is not permitted to run the task.",
                    _ => "Copilot became unavailable. No replacement provider or demo response was used."
                });
        }
        finally
        {
            if (!succeeded)
                await TryCleanupAsync("cancel pending waits", _ => linked.CancelAsync(), _timing.DisposeTimeout);
            if (runtime is not null)
            {
                var cleanup = await CleanupAsync(runtime, operation.Session, abort: !succeeded);
                if (cleanup == CopilotCleanup.Failed || (succeeded && cleanup == CopilotCleanup.Forced))
                    throw new AgentProviderException("copilot_cleanup_failed",
                        "Copilot could not shut down cleanly. The run is not confirmed complete; restart the local service before retrying.");
            }
        }
    }

    private async Task PreflightAsync(
        ICopilotRuntime runtime, CopilotOperation operation, CancellationToken token)
    {
        operation.Stage = CopilotStage.Preflight;
        var status = await runtime.GetStatusAsync(token).WaitAsync(token);
        if (status.ProtocolVersion != 3 || string.IsNullOrEmpty(status.Version) ||
            status.Version.Length > 80 ||
            !Regex.IsMatch(status.Version, @"^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant))
            throw new AgentProviderException("copilot_protocol_unsupported",
                "Copilot CLI did not report compatible protocol version 3 and valid version metadata.");
        operation.CliVersion = status.Version;
        var auth = await runtime.GetAuthStatusAsync(token).WaitAsync(token);
        if (!auth.IsAuthenticated)
            throw new AgentProviderException("copilot_auth_required",
                "Copilot sign-in is required for this isolated application home. Run the supplied setup command in a separate PowerShell window, then check again.");
        if (auth.AuthType == "api-key")
            throw new AgentProviderException("copilot_model_unsupported",
                "A GitHub Copilot account is required. A custom API-key provider is not supported by this workspace.");

        var models = await runtime.ListModelsAsync(token).WaitAsync(token);
        var autoSelections = models.Where(model => model.Id == WorkspaceOptions.Model).ToArray();
        if (autoSelections.Length > 1 ||
            autoSelections.Any(model => model.Policy is not null && model.Policy.State != "enabled") ||
            !models.Any(model => !string.IsNullOrWhiteSpace(model.Id) &&
                (model.Policy is null || model.Policy.State == "enabled")))
            throw ModelUnavailable();
    }

    private static AgentProviderException ModelUnavailable() => new("copilot_model_unsupported",
        "This Copilot account does not advertise an available model for Auto selection, or Auto is disabled by policy.");

    // A timed-out operation can finish after its public failure and owned-runtime cleanup.
    private static void ObserveLateFailure(Task task) =>
        _ = task.ContinueWith(completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static void RequireModelPolicy(CopilotModelPolicy policy)
    {
        if (policy.AllowedModels is not null || policy.EffectiveAllowedModels is { Count: 0 })
            throw new AgentProviderException("copilot_policy_unverified",
                "Copilot did not clear the app's previous fixed-model restriction or has no permitted models for Auto.");
    }

    internal static void RequireSnapshot(CopilotSessionSnapshot snapshot)
    {
        if (snapshot.Model != WorkspaceOptions.Model ||
            snapshot.Tools is null || snapshot.Tools.Count != 0)
            throw new AgentProviderException("copilot_policy_unverified",
                "Copilot did not confirm Auto selection and an initialized empty tool set.");
    }

    private async Task<string?> WaitForTurnAsync(
        ICopilotRuntime runtime, Task<string?> turn, CopilotEventState events,
        string agentId, Func<AgentSignal, Task> onSignal, CancellationToken token)
    {
        var progressAt = Stopwatch.GetTimestamp();
        var pingAt = progressAt;
        long publishedCharacters = 0;
        while (!turn.IsCompleted)
        {
            token.ThrowIfCancellationRequested();
            events.ThrowIfFaulted();
            var pulse = Task.Delay(_timing.PollInterval, token);
            await Task.WhenAny(turn, events.Fault, pulse);
            token.ThrowIfCancellationRequested();
            events.ThrowIfFaulted();
            if (Stopwatch.GetElapsedTime(pingAt) >= _timing.HeartbeatInterval)
            {
                await runtime.PingAsync(token).WaitAsync(_timing.HeartbeatTimeout, token);
                pingAt = Stopwatch.GetTimestamp();
            }
            var characters = events.Characters;
            if (characters > publishedCharacters &&
                Stopwatch.GetElapsedTime(progressAt) >= _timing.ProgressInterval)
            {
                await PublishAsync(onSignal, new(agentId, "delta",
                    $"Generating a draft ({characters.ToString("N0", CultureInfo.InvariantCulture)} characters received)."), token);
                publishedCharacters = characters;
                progressAt = Stopwatch.GetTimestamp();
            }
        }
        token.ThrowIfCancellationRequested();
        events.ThrowIfFaulted();
        return await turn.WaitAsync(token);
    }

    private async Task PublishSessionAsync(Func<AgentSignal, Task> onSignal, AgentSignal signal)
    {
        var notification = PublishAsync(onSignal, signal, CancellationToken.None);
        ObserveLateFailure(notification);
        try { await notification.WaitAsync(_timing.SessionSignalTimeout); }
        catch (TimeoutException)
        {
            _logger.LogWarning("Copilot session notification exceeded its persistence deadline.");
            throw new AgentProviderException("copilot_signal_failed",
                "The workspace could not confirm durable storage of the Copilot session identifier. No prompt was sent.");
        }
    }

    private async Task PublishAsync(
        Func<AgentSignal, Task> onSignal, AgentSignal signal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await onSignal(signal).WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger.LogWarning("Copilot workspace callback failed ({ErrorType}).", error.GetType().Name);
            throw new AgentProviderException("copilot_signal_failed",
                "The workspace could not persist an agent update. Copilot work was stopped rather than continuing without durable tracking.");
        }
    }

    private async Task<CopilotCleanup> CleanupAsync(
        ICopilotRuntime runtime, ICopilotSession? session, bool abort)
    {
        var force = false;
        if (abort && session is not null)
            force = !await TryCleanupAsync("abort", session.AbortAsync, _timing.AbortTimeout);
        if (!force)
            force = !await TryCleanupAsync("stop", _ => runtime.StopAsync(), _timing.StopTimeout);
        var stopped = !force ||
            await TryCleanupAsync("force-stop", _ => runtime.ForceStopAsync(), _timing.ForceStopTimeout);
        var disposed = await TryCleanupAsync("dispose", _ => runtime.DisposeAsync().AsTask(), _timing.DisposeTimeout);
        if (!stopped || !disposed) return CopilotCleanup.Failed;
        return force ? CopilotCleanup.Forced : CopilotCleanup.Graceful;
    }

    private async Task<bool> TryCleanupAsync(
        string stage, Func<CancellationToken, Task> action, TimeSpan timeout)
    {
        using var cleanup = new CancellationTokenSource(timeout);
        try
        {
            await action(cleanup.Token).WaitAsync(cleanup.Token);
            return true;
        }
        catch (Exception error)
        {
            _logger.LogWarning("Copilot owned-runtime {CleanupStage} failed ({ErrorType}).",
                stage, error.GetType().Name);
            return false;
        }
    }

    private void PrepareDirectories(CopilotOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_options.DataDirectory))
            throw new AgentProviderException("copilot_configuration_invalid",
                "Configure an application-owned DataDirectory before using Copilot.");
        operation.Executable = CopilotExecutableResolver.Resolve(_options.CopilotExecutable);
        token.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetFullPath(_options.DataDirectory), "copilot");
        operation.Home = Path.Combine(root, "home");
        var workspace = operation.WorkspaceId is null ? "preflight" :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation.WorkspaceId))).ToLowerInvariant();
        operation.WorkingDirectory = Path.Combine(root, "workspaces", workspace);
        Directory.CreateDirectory(operation.Home);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(operation.WorkingDirectory);
        token.ThrowIfCancellationRequested();
        operation.SetupCommand =
            $"$env:COPILOT_HOME = {PowerShellLiteral(operation.Home)}; " +
            "$env:COPILOT_DISABLE_KEYTAR = '1'; " +
            $"& {PowerShellLiteral(operation.Executable)} login --device-code";
    }

    internal static CopilotClientOptions CreateClientOptions(CopilotOperation operation) => new()
    {
        Connection = RuntimeConnection.ForStdio(operation.Executable,
        [
            "--disable-builtin-mcps", "--no-custom-instructions",
            "--no-remote", "--no-remote-export", "--no-experimental"
        ]),
        Mode = CopilotClientMode.Empty,
        BaseDirectory = operation.Home,
        WorkingDirectory = operation.WorkingDirectory,
        UseLoggedInUser = true,
        EnableRemoteSessions = false,
        Environment = CreateChildEnvironment(),
        Logger = NullLogger.Instance,
        LogLevel = CopilotLogLevel.None
    };

    internal static IReadOnlyDictionary<string, string> CreateChildEnvironment(
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] names =
        [
            "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP",
            "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA",
            "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "ALLUSERSPROFILE",
            "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE"
        ];
        foreach (var name in names)
        {
            var value = readEnvironment(name);
            if (!string.IsNullOrEmpty(value)) environment[name] = value;
        }
        return environment;
    }

    private static string PowerShellLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private ProviderStatus Status(
        CopilotOperation operation, string state, string message, string? setupCommand = null) =>
        new(Name, state, message, WorkspaceOptions.Model, WorkspaceOptions.ReasoningEffort,
            SdkVersion, operation.CliVersion, setupCommand);

    private static void ValidateRequest(AgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || request.WorkspaceId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.AgentId) || request.AgentId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Instructions) || string.IsNullOrWhiteSpace(request.Prompt) ||
            (request.SessionId is not null && !Guid.TryParseExact(request.SessionId, "D", out _)))
            throw new AgentProviderException("copilot_request_invalid",
                "The Copilot request must include workspace and agent identifiers, instructions, a prompt, and a valid saved session identifier.");
    }

    private sealed class CopilotEventState
    {
        private readonly TaskCompletionSource<AgentProviderException> _fault =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _characters;
        internal long Characters => Interlocked.Read(ref _characters);
        internal Task Fault => _fault.Task;

        internal void Observe(CopilotRuntimeEvent evt)
        {
            var error = evt.Kind switch
            {
                CopilotRuntimeEventKind.ToolStarted =>
                    new AgentProviderException("copilot_tool_policy_violation", "Copilot attempted tool execution despite this workspace's no-tools policy."),
                CopilotRuntimeEventKind.Error =>
                    new AgentProviderException("copilot_session_failed", "Copilot reported a session failure. No alternate provider was used."),
                _ => null
            };
            if (error is not null) _fault.TrySetResult(error);
            if (evt.Kind == CopilotRuntimeEventKind.Delta &&
                Interlocked.Add(ref _characters, Math.Max(0, evt.Characters)) > MaximumResponseCharacters)
                _fault.TrySetResult(new AgentProviderException("copilot_invalid_output",
                    "Copilot exceeded the supported response size and was stopped."));
        }

        internal void ThrowIfFaulted()
        {
            if (_fault.Task.IsCompletedSuccessfully) throw _fault.Task.Result;
        }
    }
}

internal sealed class CopilotOperation
{
    internal string? WorkspaceId;
    internal string? Executable;
    internal string? Home;
    internal string? WorkingDirectory;
    internal string? SetupCommand;
    internal string? CliVersion;
    internal ICopilotSession? Session;
    internal CopilotStage Stage = CopilotStage.Configuration;
}

internal enum CopilotStage { Configuration, Start, Preflight, Session, Policy, Turn }
internal enum CopilotCleanup { Graceful, Forced, Failed }

internal sealed record CopilotProviderTiming(
    TimeSpan StatusTimeout, TimeSpan AbortTimeout, TimeSpan StopTimeout,
    TimeSpan ForceStopTimeout, TimeSpan DisposeTimeout, TimeSpan PollInterval,
    TimeSpan ProgressInterval, TimeSpan HeartbeatInterval, TimeSpan HeartbeatTimeout)
{
    internal TimeSpan SessionSignalTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal static CopilotProviderTiming Default { get; } = new(
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
}
