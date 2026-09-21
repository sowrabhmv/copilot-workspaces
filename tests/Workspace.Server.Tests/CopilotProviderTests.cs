using System.Collections.Concurrent;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workspace.Server.Providers;
using Xunit.Abstractions;

namespace Workspace.Server.Tests;

public sealed class CopilotProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"copilot-provider-tests-{Guid.NewGuid():N}");
    private readonly string _executable;
    private readonly ITestOutputHelper _output;
    private static readonly CopilotProviderTiming Timing = new(
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(50));

    public CopilotProviderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
        _executable = Path.Combine(_directory, "copilot.exe");
        File.WriteAllBytes(_executable, []);
    }

    [Fact]
    public async Task StatusChecksAccountAndModelsWithoutOpeningOrSendingASession()
    {
        var runtime = new FakeRuntime();
        var provider = Create(runtime);
        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal("copilot", provider.Name);
        Assert.Equal("ready", status.State);
        Assert.Equal("1.0.14", status.SdkVersion);
        Assert.Equal("1.0.87-0", status.CliVersion);
        Assert.Equal(WorkspaceOptions.Model, status.Model);
        Assert.Equal(WorkspaceOptions.ReasoningEffort, status.ReasoningEffort);
        Assert.Equal(0, runtime.OpenCount);
        Assert.Equal(0, runtime.Session.SendCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task UnauthenticatedStatusGivesIsolatedSetupAndNeverLeaksAuthMetadata()
    {
        const string secret = "PRIVATE-AUTH-METADATA";
        var runtime = new FakeRuntime
        {
            Auth = new GetAuthStatusResponse
            {
                IsAuthenticated = false, Login = secret, StatusMessage = secret
            }
        };
        var options = Options();
        options.DataDirectory = Path.Combine(_directory, "owner's data");
        var status = await Create(runtime, options).GetStatusAsync(CancellationToken.None);

        Assert.Equal("setupRequired", status.State);
        Assert.Contains("COPILOT_HOME", status.SetupCommand);
        Assert.Contains("COPILOT_DISABLE_KEYTAR = '1'", status.SetupCommand);
        Assert.Contains("owner''s data", status.SetupCommand);
        Assert.Contains("login --device-code", status.SetupCommand);
        Assert.DoesNotContain(secret, status.Message);
        Assert.Equal(0, runtime.OpenCount);
        Assert.Equal(0, runtime.ModelsCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("unconfigured")]
    [InlineData("auto-disabled")]
    [InlineData("duplicate-auto")]
    public async Task StatusRejectsUnavailableOrDisabledAutoRouting(string variation)
    {
        var runtime = new FakeRuntime();
        var model = runtime.Models[0];
        switch (variation)
        {
            case "missing": runtime.Models.Clear(); break;
            case "auto-disabled":
                runtime.Models.Add(new() { Id = "auto", Name = "Auto", Policy = new() { State = "disabled" } });
                break;
            case "duplicate-auto":
                runtime.Models.Add(new() { Id = "auto", Name = "Auto" });
                runtime.Models.Add(new() { Id = "auto", Name = "Auto" });
                break;
            default: model.Policy = new ModelPolicy { State = variation }; break;
        }

        var status = await Create(runtime).GetStatusAsync(CancellationToken.None);
        Assert.Equal("unsupported", status.State);
        Assert.Equal(0, runtime.OpenCount);
        Assert.Equal(0, runtime.Session.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoDoesNotRequireAstraOrAMaxReasoningCapability(bool reasoning)
    {
        var runtime = new FakeRuntime();
        runtime.Models[0].Capabilities.Supports.ReasoningEffort = reasoning;
        runtime.Models[0].SupportedReasoningEfforts = reasoning ? ["medium"] : null;
        var status = await Create(runtime).GetStatusAsync(CancellationToken.None);
        Assert.Equal("ready", status.State);
        Assert.Equal("auto", status.Model);
        Assert.Null(status.ReasoningEffort);
        Assert.Equal(0, runtime.Session.SendCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task StatusRequiresProtocolThree(int version)
    {
        var runtime = new FakeRuntime { Status = new GetStatusResponse { ProtocolVersion = version, Version = "1.0.87-0" } };
        var status = await Create(runtime).GetStatusAsync(CancellationToken.None);
        Assert.Equal("unsupported", status.State);
        Assert.Equal(0, runtime.AuthCount);
    }

    [Fact]
    public async Task ProtocolFailureDuringSdkStartupIsExplicit()
    {
        var runtime = new FakeRuntime
        {
            Start = _ => throw new InvalidOperationException(
                "SDK protocol version mismatch: private runtime details")
        };
        var status = await Create(runtime).GetStatusAsync(CancellationToken.None);
        Assert.Equal("unsupported", status.State);
        Assert.DoesNotContain("private runtime details", status.Message);
    }

    [Fact]
    public async Task UntrustedVersionMetadataIsNotPublished()
    {
        var runtime = new FakeRuntime
        {
            Status = new GetStatusResponse { ProtocolVersion = 3, Version = "<script>PRIVATE</script>" }
        };
        var status = await Create(runtime).GetStatusAsync(CancellationToken.None);
        Assert.Equal("unsupported", status.State);
        Assert.Null(status.CliVersion);
        Assert.DoesNotContain("PRIVATE", status.Message);
    }

    [Fact]
    public async Task StatusIsBoundedAndEscalatesStuckShutdown()
    {
        var runtime = new FakeRuntime
        {
            Start = _ => Never(),
            Stop = () => Never()
        };
        var status = await Create(runtime).GetStatusAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("unavailable", status.State);
        Assert.Contains("deadline", status.Message);
        Assert.Equal(1, runtime.ForceStopCount);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task MissingConfiguredExecutableDoesNotFallBackToPath()
    {
        var options = Options();
        options.CopilotExecutable = Path.Combine(_directory, "not-installed.exe");
        var factory = new FakeFactory(new FakeRuntime());
        var provider = Provider(options, factory);

        var status = await provider.GetStatusAsync(CancellationToken.None);
        Assert.Equal("setupRequired", status.State);
        Assert.Empty(factory.Options);
        Assert.Throws<AgentProviderException>(() => CopilotExecutableResolver.Resolve(
            options.CopilotExecutable, name => name == "PATH" ? _directory : null));
    }

    [Theory]
    [InlineData("copilot.exe")]
    [InlineData("copilot --headless")]
    public void ResolverRejectsRelativePathsAndCommands(string input)
    {
        var error = Assert.Throws<AgentProviderException>(() =>
            CopilotExecutableResolver.Resolve(input, name => name == "PATH" ? _directory : null));
        Assert.Equal("copilot_executable_invalid", error.Code);
    }

    [Fact]
    public void ResolverUsesExplicitTrustedEnvironmentAndAbsolutePathEntriesOnly()
    {
        Assert.Equal(_executable, CopilotExecutableResolver.Resolve(null,
            name => name == "COPILOT_CLI_PATH" ? _executable : null));
        Assert.Equal(_executable, CopilotExecutableResolver.Resolve(null,
            name => name == "PATH" ? $".{Path.PathSeparator}{_directory}" : null));
        Assert.Throws<AgentProviderException>(() => CopilotExecutableResolver.Resolve(null,
            name => name == "PATH" ? "." : null));
        var shim = Path.Combine(_directory, "copilot.cmd");
        File.WriteAllText(shim, "not executed");
        Assert.Throws<AgentProviderException>(() => CopilotExecutableResolver.Resolve(shim));
    }

    [Fact]
    public void ChildEnvironmentDoesNotReadOrCopyCredentialAndInjectionVariables()
    {
        var read = new List<string>();
        var environment = CopilotAgentProvider.CreateChildEnvironment(name =>
        {
            read.Add(name);
            return name == "PATH" ? "trusted-path" : null;
        });
        Assert.Equal("trusted-path", environment["PATH"]);
        Assert.DoesNotContain("GH_TOKEN", read);
        Assert.DoesNotContain("GITHUB_TOKEN", read);
        Assert.DoesNotContain("COPILOT_GITHUB_TOKEN", read);
        Assert.DoesNotContain("COPILOT_ALLOW_ALL", read);
        Assert.DoesNotContain("COPILOT_CUSTOM_INSTRUCTIONS_DIRS", read);
        Assert.Single(environment);
    }

#pragma warning disable GHCP001
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealSdkConfigurationsApplyTheSameRestrictionsOnCreateAndResume(bool resume)
    {
        var id = Guid.NewGuid().ToString("D");
        var config = CopilotSdkRuntime.CreateConfiguration(
            new(id, resume, _directory, "Trusted role instructions.", _ => { }));

        Assert.Equal(WorkspaceOptions.Model, config.Model);
        Assert.Equal("auto", config.Model);
        Assert.Null(config.ReasoningEffort);
        Assert.True(config.Streaming);
        Assert.Empty(Assert.IsAssignableFrom<IList<string>>(config.AvailableTools));
        Assert.Equal(["builtin:*", "mcp:*", "custom:*"], config.ExcludedTools);
        Assert.Empty(config.Tools!);
        Assert.Empty(config.CustomAgents!);
        Assert.Empty(config.McpServers!);
        Assert.Equal(["github-mcp-server"], config.DisabledMcpServers);
        Assert.False(config.EnableConfigDiscovery);
        Assert.True(config.SkipCustomInstructions);
        Assert.False(config.EnableFileHooks);
        Assert.False(config.EnableOnDemandInstructionDiscovery);
        Assert.False(config.EnableHostGitOperations);
        Assert.False(config.EnableSkills);
        Assert.False(config.EnableSessionStore);
        Assert.False(config.EnableSessionTelemetry);
        Assert.False(config.EnableExperimentalMode);
        Assert.False(config.RequestExtensions);
        Assert.False(config.RequestCanvasRenderer);
        Assert.False(config.ManageScheduleEnabled);
        Assert.True(config.SkipEmbeddingRetrieval);
        Assert.False(config.Memory!.Enabled);
        Assert.Empty(config.PluginDirectories!);
        Assert.Empty(config.SkillDirectories!);
        Assert.Empty(config.InstructionDirectories!);
        Assert.Empty(config.AdditionalDirectories!);
        Assert.Null(config.Provider);
        Assert.Null(config.Agent);
        Assert.NotNull(config.OnPermissionRequest);
        Assert.NotNull(config.OnEvent);
        Assert.Null(config.OnUserInputRequest);
        if (resume)
            Assert.False(Assert.IsType<ResumeSessionConfig>(config).ContinuePendingWork);
        else
            Assert.Equal(id, Assert.IsType<SessionConfig>(config).SessionId);
    }

    [Fact]
    public async Task PermissionHandlerAlwaysRejects()
    {
        var config = CopilotSdkRuntime.CreateConfiguration(new(
            Guid.NewGuid().ToString("D"), false, _directory, "Instructions", _ => { }));
        var decision = await config.OnPermissionRequest!(
            new PermissionRequest(), new PermissionInvocation());
        Assert.Equal("reject", decision.Kind);
    }
#pragma warning restore GHCP001

    [Fact]
    public void RealSdkEventBridgePublishesOnlyCountsAndIgnoresReasoning()
    {
        var events = new List<CopilotRuntimeEvent>();
        var message = Assert.IsType<AssistantMessageDeltaEvent>(SessionEvent.FromJson("""
            {"id":"00000000-0000-4000-8000-000000000001","timestamp":"2026-09-20T00:00:00Z","parentId":null,"ephemeral":true,
             "type":"assistant.message_delta","data":{"messageId":"00000000-0000-4000-8000-000000000003","deltaContent":"<script>SECRET</script>"}}
            """));
        var reasoning = Assert.IsType<AssistantReasoningDeltaEvent>(SessionEvent.FromJson("""
            {"id":"00000000-0000-4000-8000-000000000002","timestamp":"2026-09-20T00:00:00Z","parentId":"00000000-0000-4000-8000-000000000001","ephemeral":true,
             "type":"assistant.reasoning_delta","data":{"reasoningId":"00000000-0000-4000-8000-000000000004","deltaContent":"PRIVATE REASONING"}}
            """));
        CopilotSdkRuntime.ObserveEvent(message, events.Add);
        CopilotSdkRuntime.ObserveEvent(reasoning, events.Add);

        var delta = Assert.Single(events);
        Assert.Equal(CopilotRuntimeEventKind.Delta, delta.Kind);
        Assert.Equal("<script>SECRET</script>".Length, delta.Characters);
    }

    [Theory]
    [InlineData("gpt-6-astra", "max")]
    [InlineData("gpt-6-astra", null)]
    [InlineData("claude-sonnet-4.6", "high")]
    [InlineData("auto", null)]
    public void ModelEventBridgeAllowsAutoToChooseDifferentUnderlyingModelsAndEfforts(
        string model, string? effort)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(), timestamp = DateTimeOffset.UtcNow, parentId = (Guid?)null,
            type = "session.model_change", data = new { newModel = model, reasoningEffort = effort }
        });
        var evt = Assert.IsType<SessionModelChangeEvent>(SessionEvent.FromJson(json));
        var events = new List<CopilotRuntimeEvent>();
        CopilotSdkRuntime.ObserveEvent(evt, events.Add);
        Assert.Empty(events);
    }

    [Fact]
    public void AutoUsageMetadataIsNotMistakenForAManualModelOverride()
    {
        var usage = Assert.IsType<AssistantUsageEvent>(SessionEvent.FromJson("""
            {"id":"00000000-0000-4000-8000-000000000001","timestamp":"2026-09-20T00:00:00Z","parentId":null,
             "type":"assistant.usage","data":{"model":"gpt-6-astra","inputTokens":12,"outputTokens":4}}
            """));
        var events = new List<CopilotRuntimeEvent>();
        CopilotSdkRuntime.ObserveEvent(usage, events.Add);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionIdentifierIsPersistedBeforeVerificationOrInference(bool resume)
    {
        var runtime = new FakeRuntime();
        var signals = new List<AgentSignal>();
        var persisted = false;
        runtime.Session.BeforePolicy = () => Assert.True(persisted);
        runtime.Session.Send = _ =>
        {
            Assert.True(persisted);
            return Task.FromResult<string?>("{\"summary\":\"result\"}");
        };
        var existing = resume ? Guid.NewGuid().ToString("D") : null;
        var reply = await Create(runtime).CompleteAsync(Request(existing), signal =>
        {
            signals.Add(signal);
            if (signal.Kind == "session") persisted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        var signal = Assert.Single(signals, item => item.Kind == "session");
        Assert.Equal(reply.SessionId, signal.SessionId);
        Assert.Equal(resume, runtime.LastStart!.Resume);
        if (resume) Assert.Equal(existing, reply.SessionId);
        Assert.Equal(2, runtime.Session.SnapshotCount);
        Assert.Equal(0, runtime.Session.AbortCount);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task FailedResumeNeverCreatesAReplacementSession()
    {
        var runtime = new FakeRuntime { OpenError = new IOException("PRIVATE PATH OR TOKEN") };
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(Guid.NewGuid().ToString("D")),
                IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_session_unavailable", error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.True(runtime.LastStart!.Resume);
        Assert.Equal(1, runtime.OpenCount);
        Assert.Equal(0, runtime.Session.SendCount);
    }

    [Fact]
    public async Task UnverifiedPolicyAbortsWithoutSendingAndKeepsSessionCallback()
    {
        var runtime = new FakeRuntime();
        runtime.Session.Policy = new(["gpt-6-astra"], ["gpt-6-astra"], null);
        var signals = new List<AgentSignal>();
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), signal =>
            {
                signals.Add(signal);
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal("copilot_policy_unverified", error.Code);
        Assert.Single(signals, signal => signal.Kind == "session");
        Assert.Equal(0, runtime.Session.SendCount);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("tools")]
    [InlineData("unknown-tools")]
    public async Task EffectiveSessionRestrictionsAreVerifiedBeforeAnyPrompt(string variation)
    {
        var runtime = new FakeRuntime();
        runtime.Session.Snapshot = variation switch
        {
            "model" => new("gpt-6-astra", "max", []),
            "tools" => new(WorkspaceOptions.Model, "max", ["powershell"]),
            _ => new(WorkspaceOptions.Model, "max", null)
        };
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_policy_unverified", error.Code);
        Assert.Equal(0, runtime.Session.SendCount);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task OutputIsRejectedIfFinalEffectivePolicyChanged()
    {
        var runtime = new FakeRuntime();
        runtime.Session.Send = _ =>
        {
            runtime.Session.Snapshot = new("gpt-6-astra", "max", []);
            return Task.FromResult<string?>("discard this response");
        };
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_policy_unverified", error.Code);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Theory]
    [InlineData("ToolStarted", "copilot_tool_policy_violation")]
    [InlineData("Error", "copilot_session_failed")]
    public async Task RuntimePolicyAndErrorEventsStopTheTurn(
        string eventKind, string code)
    {
        var kind = Enum.Parse<CopilotRuntimeEventKind>(eventKind);
        var runtime = new FakeRuntime();
        runtime.Session.Send = token =>
        {
            runtime.LastStart!.OnEvent(new(kind));
            return NeverReply(token);
        };
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task AutoPreservesProviderPolicyWithoutPinningAConcreteModelOrReasoningLevel()
    {
        var runtime = new FakeRuntime();
        runtime.Session.Policy = new(null, ["organization-approved-model"], "organization-approved-model");
        runtime.Session.Snapshot = new("auto", "medium", []);
        var reply = await Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None);
        Assert.NotEmpty(reply.Text);
        Assert.Equal(1, runtime.Session.SendCount);
        Assert.Equal(2, runtime.Session.SnapshotCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyResponsesAreNotSuccessShapedFallbacks(string? output)
    {
        var runtime = new FakeRuntime();
        runtime.Session.Send = _ => Task.FromResult(output);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_invalid_output", error.Code);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task ExcessiveStreamIsAbortedBeforeAccumulatingAnUnboundedResponse()
    {
        var runtime = new FakeRuntime();
        runtime.Session.Send = token =>
        {
            runtime.LastStart!.OnEvent(new(CopilotRuntimeEventKind.Delta,
                CopilotAgentProvider.MaximumResponseCharacters + 1));
            return NeverReply(token);
        };
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_invalid_output", error.Code);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task StreamPublishesSafeProgressInsteadOfRawProviderContent()
    {
        var runtime = new FakeRuntime();
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Session.Send = _ =>
        {
            runtime.LastStart!.OnEvent(new(CopilotRuntimeEventKind.Delta, 100));
            return finish.Task;
        };
        var signals = new List<AgentSignal>();
        var reply = await Create(runtime).CompleteAsync(Request(), signal =>
        {
            signals.Add(signal);
            if (signal.Kind == "delta") finish.TrySetResult("private raw structured output");
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal("private raw structured output", reply.Text);
        Assert.Contains(signals, signal => signal.Kind == "delta" && signal.Message.Contains("100"));
        Assert.DoesNotContain(signals, signal => signal.Message.Contains("private raw"));
    }

    [Fact]
    public async Task CallbackFailureAbortsInsteadOfRunningWithoutPersistence()
    {
        var runtime = new FakeRuntime();
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), signal =>
                signal.Kind == "session" ? throw new IOException("SECRET") : Task.CompletedTask,
                CancellationToken.None));
        Assert.Equal("copilot_signal_failed", error.Code);
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, runtime.Session.SendCount);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task CancellationImmediatelyAfterOpenStillSignalsTheActualSessionId()
    {
        using var cancel = new CancellationTokenSource();
        var runtime = new FakeRuntime { BeforeOpenReturns = cancel.Cancel };
        var signals = new List<AgentSignal>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(runtime).CompleteAsync(Request(), signal =>
            {
                signals.Add(signal);
                return Task.CompletedTask;
            }, cancel.Token));

        var sessionSignal = Assert.Single(signals, signal => signal.Kind == "session");
        Assert.Equal(runtime.Session.SessionId, sessionSignal.SessionId);
        Assert.Equal(0, runtime.Session.SendCount);
        Assert.Equal(0, runtime.Session.SnapshotCount);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task CancellationDoesNotCancelAnAlreadyStartedSessionPersistenceCallback()
    {
        using var cancel = new CancellationTokenSource();
        var runtime = new FakeRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Create(runtime).CompleteAsync(Request(), async signal =>
        {
            if (signal.Kind != "session") return;
            started.SetResult();
            await commit.Task;
            persisted.SetResult(signal.SessionId);
        }, cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        commit.SetResult();

        Assert.Equal(runtime.Session.SessionId,
            await persisted.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, runtime.Session.SendCount);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task WholeOperationDeadlineIncludesAcknowledgementAndExplicitlyAborts()
    {
        var runtime = new FakeRuntime();
        runtime.Session.Send = _ => NeverReply();
        var options = Options();
        options.ProviderTimeoutSeconds = 1;
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime, options).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("copilot_timeout", error.Code);
        Assert.Equal(1, runtime.Session.AbortCount);
        Assert.False(runtime.Session.AbortTokenAlreadyCancelled);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task ExplicitCancellationAbortsOnlyItsOwnedRuntime()
    {
        var first = new FakeRuntime();
        var second = new FakeRuntime();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Session.Send = token => { firstStarted.SetResult(); return NeverReply(token); };
        second.Session.Send = _ => { secondStarted.SetResult(); return secondResult.Task; };
        var provider = Provider(Options(), new FakeFactory(first, second));
        using var cancel = new CancellationTokenSource();
        var cancelled = provider.CompleteAsync(Request(workspace: "first"), IgnoreSignal, cancel.Token);
        await firstStarted.Task;
        var continued = provider.CompleteAsync(Request(workspace: "second"), IgnoreSignal, CancellationToken.None);
        await secondStarted.Task;
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, first.Session.AbortCount);
        Assert.False(first.Session.AbortTokenAlreadyCancelled);
        Assert.Equal(0, second.Session.AbortCount);
        Assert.Equal(0, second.StopCount);
        secondResult.SetResult("second workspace result");
        Assert.Equal("second workspace result", (await continued).Text);
    }

    [Fact]
    public async Task LostRuntimeIsDetectedByBoundedHeartbeat()
    {
        var runtime = new FakeRuntime { Ping = _ => throw new IOException("PRIVATE STDERR") };
        runtime.Session.Send = token => NeverReply(token);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_unavailable", error.Code);
        Assert.DoesNotContain("PRIVATE STDERR", error.Message);
        Assert.True(runtime.PingCount > 0);
        Assert.Equal(1, runtime.Session.AbortCount);
    }

    [Fact]
    public async Task FailedAbortEscalatesToOwnedRuntimeForceStop()
    {
        var runtime = new FakeRuntime();
        runtime.Session.Abort = _ => Never();
        runtime.Session.Send = _ => Task.FromResult<string?>("");
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_invalid_output", error.Code);
        Assert.Equal(1, runtime.ForceStopCount);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task UnconfirmedShutdownIsNeverReportedAsSuccess()
    {
        var runtime = new FakeRuntime
        {
            Stop = () => throw new IOException("PRIVATE STOP"),
            ForceStop = () => throw new IOException("PRIVATE FORCE")
        };
        var error = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Create(runtime).CompleteAsync(Request(), IgnoreSignal, CancellationToken.None));
        Assert.Equal("copilot_cleanup_failed", error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.Equal(1, runtime.ForceStopCount);
    }

    [Fact]
    public async Task RawRuntimeErrorsDoNotReachPublicErrorsOrApplicationLogs()
    {
        var runtime = new FakeRuntime { Start = _ => throw new IOException("SECRET TOKEN AND PRIVATE PATH") };
        var logger = new CapturingLogger();
        var provider = new CopilotAgentProvider(Options(), logger, new FakeFactory(runtime), Timing);
        var status = await provider.GetStatusAsync(CancellationToken.None);
        Assert.DoesNotContain("SECRET", status.Message);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("SECRET") || message.Contains("PRIVATE PATH"));
    }

    [Fact]
    public async Task RealClientOptionsUseEmptyModeAndOwnedIsolatedDirectories()
    {
        var factory = new FakeFactory(new FakeRuntime());
        await Provider(Options(), factory).GetStatusAsync(CancellationToken.None);
        var options = Assert.Single(factory.Options);
        Assert.Equal(CopilotClientMode.Empty, options.Mode);
        Assert.Equal(Path.Combine(_directory, "data", "copilot", "home"), options.BaseDirectory);
        Assert.False(options.EnableRemoteSessions);
        Assert.NotNull(options.Environment);
        Assert.Null(options.GitHubToken);
        Assert.IsType<StdioRuntimeConnection>(options.Connection);
        Assert.Same(NullLogger.Instance, options.Logger);
    }

    [CopilotPreflightFact]
    [Trait("Category", "CopilotPreflight")]
    public async Task InstalledCliReadOnlyPreflight()
    {
        var options = Options();
        options.CopilotExecutable = null;
        var provider = new CopilotAgentProvider(options, NullLogger<CopilotAgentProvider>.Instance);
        var status = await provider.GetStatusAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(40));
        _output.WriteLine("State={0}; SDK={1}; CLI={2}; Message={3}",
            status.State, status.SdkVersion, status.CliVersion, status.Message);
        Assert.NotNull(status.CliVersion);
        Assert.Contains(status.State, new[] { "ready", "setupRequired", "unsupported" });
    }

    [CopilotPreflightFact]
    [Trait("Category", "CopilotPreflight")]
    public async Task InstalledCliRestrictedCreateAndEmptySessionLifecyclePreflight()
    {
        var home = Path.Combine(_directory, "isolated-home");
        var workingDirectory = Path.Combine(_directory, "isolated-workspace");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workingDirectory);
        var operation = new CopilotOperation
        {
            Executable = CopilotExecutableResolver.Resolve(null),
            Home = home,
            WorkingDirectory = workingDirectory
        };
        var sessionId = Guid.NewGuid().ToString("D");
        foreach (var resume in new[] { false, true })
        {
            var runtime = new CopilotSdkRuntimeFactory().Create(CopilotAgentProvider.CreateClientOptions(operation));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var stage = "start";
            try
            {
                await runtime.StartAsync(deadline.Token).WaitAsync(deadline.Token);
                stage = resume ? "resume" : "create";
                if (resume)
                {
                    var missing = await Assert.ThrowsAsync<IOException>(() => runtime.OpenSessionAsync(new(
                        sessionId, true, workingDirectory,
                        "Metadata-only empty-session lifecycle verification.", _ => { }), deadline.Token)
                        .WaitAsync(deadline.Token));
                    Assert.True(missing.Message.Contains("session", StringComparison.OrdinalIgnoreCase) &&
                        missing.Message.Contains("not found", StringComparison.OrdinalIgnoreCase),
                        "The never-prompted session did not return the expected missing-session response.");
                    _output.WriteLine("CLI 1.0.87-0 does not cold-resume a never-prompted session. " +
                        "Conversation-history resume still requires a coordinated completion smoke.");
                    continue;
                }
                var session = await runtime.OpenSessionAsync(new(
                    sessionId, resume, workingDirectory,
                    "Metadata-only policy verification. No prompt is sent.", _ => { }),
                    deadline.Token).WaitAsync(deadline.Token);
                Assert.Equal(sessionId, session.SessionId);
                stage = "model allowlist";
                CopilotAgentProvider.RequireModelPolicy(
                    await session.ApplyModelPolicyAsync(deadline.Token).WaitAsync(deadline.Token));
                stage = "effective model and tools";
                CopilotAgentProvider.RequireSnapshot(
                    await session.GetSnapshotAsync(deadline.Token).WaitAsync(deadline.Token));
                _output.WriteLine("Create: Auto selection, cleared fixed-model restriction and initialized empty tool set confirmed; no prompt sent.");
            }
            catch (Exception error)
            {
                string[] markers = ["not found", "does not exist", "connection", "closed", "exited", "session", "timeout"];
                var categories = markers.Where(marker => error.Message.Contains(marker, StringComparison.OrdinalIgnoreCase));
                Assert.Fail($"Restricted metadata-only preflight failed at {stage} ({error.GetType().Name}; " +
                    $"HResult={error.HResult:X}; inner={error.InnerException?.GetType().Name ?? "none"}; " +
                    $"markers={string.Join(",", categories)}).");
            }
            finally
            {
                try { await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception)
                {
                    try { await runtime.ForceStopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (Exception) { Assert.Fail("Metadata-only preflight could not confirm owned-runtime shutdown."); }
                }
                try { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { Assert.Fail("Metadata-only preflight runtime disposal failed."); }
            }
        }
    }

    private WorkspaceOptions Options() => new()
    {
        DataDirectory = Path.Combine(_directory, "data"),
        CopilotExecutable = _executable,
        ProviderTimeoutSeconds = 10
    };

    private CopilotAgentProvider Create(FakeRuntime runtime, WorkspaceOptions? options = null) =>
        Provider(options ?? Options(), new FakeFactory(runtime));

    private static CopilotAgentProvider Provider(WorkspaceOptions options, FakeFactory factory) =>
        new(options, NullLogger<CopilotAgentProvider>.Instance, factory, Timing);

    private static AgentRequest Request(string? session = null, string workspace = "workspace") =>
        new(workspace, "planner", session, "Return the requested JSON.", "Prepare a bounded draft.", "planner");

    private static Task IgnoreSignal(AgentSignal _) => Task.CompletedTask;
    private static Task Never() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    private static Task<string?> NeverReply(CancellationToken token = default) =>
        new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);

    private static ModelInfo Model() => new()
    {
        Id = "gpt-4.1",
        Name = "An available Copilot model",
        Policy = new ModelPolicy { State = "enabled" },
        SupportedReasoningEfforts = null,
        Capabilities = new ModelCapabilities { Supports = new ModelSupports { ReasoningEffort = false } }
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FakeFactory(params FakeRuntime[] runtimes) : ICopilotRuntimeFactory
    {
        private readonly ConcurrentQueue<FakeRuntime> _runtimes = new(runtimes);
        internal ConcurrentQueue<CopilotClientOptions> Options { get; } = new();
        public ICopilotRuntime Create(CopilotClientOptions options)
        {
            Options.Enqueue(options);
            return _runtimes.TryDequeue(out var runtime) ? runtime :
                throw new InvalidOperationException("No fake runtime was supplied.");
        }

    }

    private sealed class FakeRuntime : ICopilotRuntime
    {
        internal Func<CancellationToken, Task> Start { get; init; } = _ => Task.CompletedTask;
        internal Func<CancellationToken, Task> Ping { get; init; } = _ => Task.CompletedTask;
        internal Func<Task> Stop { get; init; } = () => Task.CompletedTask;
        internal Func<Task> ForceStop { get; init; } = () => Task.CompletedTask;
        internal GetStatusResponse Status { get; init; } = new() { ProtocolVersion = 3, Version = "1.0.87-0" };
        internal GetAuthStatusResponse Auth { get; init; } = new() { IsAuthenticated = true, AuthType = "user" };
        internal IList<ModelInfo> Models { get; } = [Model()];
        internal FakeSession Session { get; } = new();
        internal Exception? OpenError { get; init; }
        internal Action? BeforeOpenReturns { get; init; }
        internal CopilotSessionStart? LastStart { get; private set; }
        internal int OpenCount;
        internal int StopCount;
        internal int ForceStopCount;
        internal int AuthCount;
        internal int ModelsCount;
        internal int PingCount;
        internal bool Disposed;
        public Task StartAsync(CancellationToken cancellationToken) => Start(cancellationToken);
        public Task<GetStatusResponse> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
        public Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken cancellationToken)
        {
            AuthCount++;
            return Task.FromResult(Auth);
        }
        public Task<IList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            ModelsCount++;
            return Task.FromResult(Models);
        }
        public Task<ICopilotSession> OpenSessionAsync(CopilotSessionStart start, CancellationToken cancellationToken)
        {
            OpenCount++;
            LastStart = start;
            if (OpenError is not null) throw OpenError;
            Session.SessionId = start.SessionId;
            BeforeOpenReturns?.Invoke();
            return Task.FromResult<ICopilotSession>(Session);
        }
        public Task PingAsync(CancellationToken cancellationToken)
        {
            PingCount++;
            return Ping(cancellationToken);
        }
        public Task StopAsync() { StopCount++; return Stop(); }
        public Task ForceStopAsync() { ForceStopCount++; return ForceStop(); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeSession : ICopilotSession
    {
        public string SessionId { get; set; } = "";
        internal CopilotModelPolicy Policy { get; set; } = new(null, null, null);
        internal CopilotSessionSnapshot Snapshot { get; set; } = new(WorkspaceOptions.Model, WorkspaceOptions.ReasoningEffort, []);
        internal Func<CancellationToken, Task<string?>> Send { get; set; } = _ => Task.FromResult<string?>("{\"summary\":\"ok\"}");
        internal Func<CancellationToken, Task> Abort { get; set; } = _ => Task.CompletedTask;
        internal Action? BeforePolicy { get; set; }
        internal int SendCount;
        internal int AbortCount;
        internal int SnapshotCount;
        internal bool AbortTokenAlreadyCancelled;
        public Task<CopilotModelPolicy> ApplyModelPolicyAsync(CancellationToken cancellationToken)
        {
            BeforePolicy?.Invoke();
            return Task.FromResult(Policy);
        }
        public Task<CopilotSessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotCount++;
            return Task.FromResult(Snapshot);
        }
        public Task<string?> SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
        {
            SendCount++;
            return Send(cancellationToken);
        }
        public Task AbortAsync(CancellationToken cancellationToken)
        {
            AbortCount++;
            AbortTokenAlreadyCancelled = cancellationToken.IsCancellationRequested;
            return Abort(cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<CopilotAgentProvider>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

public sealed class CopilotPreflightFactAttribute : FactAttribute
{
    public CopilotPreflightFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKSPACES_COPILOT_PREFLIGHT") != "1")
            Skip = "Opt in with WORKSPACES_COPILOT_PREFLIGHT=1; checks metadata only, never requests a completion.";
    }
}
