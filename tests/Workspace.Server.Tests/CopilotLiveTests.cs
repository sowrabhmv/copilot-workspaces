using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Workspace.Server.Providers;
using Xunit.Abstractions;

namespace Workspace.Server.Tests;

public sealed class CopilotLiveTests(ITestOutputHelper output)
{
    private const string Instructions = """
        This is a synthetic, nonsecret integration check, not a real user task.
        Do not use tools, external services, files, or agents.
        Return only the single JSON object requested by the prompt, with no
        markdown fence, commentary, or additional properties. Keep the reply
        below 200 characters. Preserve synthetic nonce values exactly.
        """;

    [CopilotLiveSmokeFact]
    [Trait("Category", "CopilotLiveSmoke")]
    public async Task RealCompletionAndColdResumePreserveConversationHistory()
    {
        Assert.Equal("auto", WorkspaceOptions.Model);
        Assert.Null(WorkspaceOptions.ReasoningEffort);

        var directory = Path.Combine(Path.GetTempPath(), $"copilot-workspaces-live-smoke-{Guid.NewGuid():N}");
        var nonce = $"smoke_nonce_{Guid.NewGuid():N}";
        var workspaceId = $"smoke_workspace_{Guid.NewGuid():N}";
        var shutdownConfirmed = true;
        var stage = "initial completion";
        Directory.CreateDirectory(directory);
        using var totalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(330));
        try
        {
            var options = new WorkspaceOptions
            {
                DataDirectory = directory,
                ProviderTimeoutSeconds = 150
            };
            var firstSignals = new List<AgentSignal>();
            var firstProvider = new CopilotAgentProvider(
                options, NullLogger<CopilotAgentProvider>.Instance);
            var firstPrompt =
                $"Remember this nonsecret synthetic verification nonce for our next turn: {nonce}. " +
                $"Reply only with this JSON object: {{\"phase\":\"stored\",\"nonce\":\"{nonce}\"}}";
            var elapsed = Stopwatch.StartNew();
            var initial = await firstProvider.CompleteAsync(
                new AgentRequest(workspaceId, "planner", null, Instructions, firstPrompt, "smoke"),
                signal => { firstSignals.Add(signal); return Task.CompletedTask; },
                totalDeadline.Token);
            var initialPayload = Parse(initial.Text);
            Assert.True(initialPayload.Phase == "stored" &&
                string.Equals(initialPayload.Nonce, nonce, StringComparison.Ordinal),
                "The initial real completion did not acknowledge the exact synthetic nonce in the requested JSON shape.");
            Assert.True(Guid.TryParseExact(initial.SessionId, "D", out _),
                "The provider did not return an actual UUID session identifier.");
            var saved = Assert.Single(firstSignals, signal => signal.Kind == "session");
            Assert.Equal(initial.SessionId, saved.SessionId);
            Assert.All(firstSignals, RequireSafeSignalKind);
            output.WriteLine("Initial completion: structured acknowledgement and session callback verified; owned runtime stopped; elapsed={0:F1}s.",
                elapsed.Elapsed.TotalSeconds);

            stage = "history-bearing cold resume";
            const string recallPrompt = """
                Recall the exact nonsecret synthetic verification nonce I asked
                you to remember in our earlier turn. It is deliberately not
                repeated in this prompt. Reply only with a JSON object whose
                phase is "recalled" and whose nonce is that exact earlier value.
                If the earlier value is unavailable, use phase "missing" and
                nonce null instead of inventing a value.
                """;
            Assert.False(recallPrompt.Contains(nonce, StringComparison.Ordinal));
            Assert.False(Instructions.Contains(nonce, StringComparison.Ordinal));
            var resumedSignals = new List<AgentSignal>();
            var resumedProvider = new CopilotAgentProvider(
                options, NullLogger<CopilotAgentProvider>.Instance);
            elapsed.Restart();
            var resumed = await resumedProvider.CompleteAsync(
                new AgentRequest(workspaceId, "planner", saved.SessionId, Instructions, recallPrompt, "smoke"),
                signal => { resumedSignals.Add(signal); return Task.CompletedTask; },
                totalDeadline.Token);
            var recalled = Parse(resumed.Text);
            Assert.True(recalled.Phase == "recalled" &&
                string.Equals(recalled.Nonce, nonce, StringComparison.Ordinal),
                "The new owned runtime did not recall the original nonce from real prior-turn history.");
            Assert.Equal(initial.SessionId, resumed.SessionId);
            Assert.Equal(initial.SessionId,
                Assert.Single(resumedSignals, signal => signal.Kind == "session").SessionId);
            Assert.All(resumedSignals, RequireSafeSignalKind);
            output.WriteLine("Cold resume: same session ID and exact nonce recall verified without repeating it; second owned runtime stopped; elapsed={0:F1}s.",
                elapsed.Elapsed.TotalSeconds);
            output.WriteLine("Exactly two provider completions; model selection=auto, no forced reasoning effort. Real provider verified Auto selection and no-tools policy before and after both turns.");
        }
        catch (AgentProviderException error)
        {
            shutdownConfirmed = error.Code != "copilot_cleanup_failed";
            Assert.Fail($"Live smoke stopped at {stage}: {error.Code}. {error.Message}");
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Live smoke reached its finite deadline at {stage}; no automatic retry or provider substitution was attempted.");
        }
        finally
        {
            if (!shutdownConfirmed)
            {
                output.WriteLine("Scoped smoke data retained because owned-runtime shutdown was not confirmed: {0}", directory);
            }
            else
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Assert.Fail("The owned runtimes returned, but scoped smoke-data cleanup failed. No unrelated directory was touched.");
                }
            }
        }
    }

    private static SmokeReply Parse(string text)
    {
        Assert.True(text.Length <= 200, "The real completion exceeded the requested bounded reply length.");
        try
        {
            return JsonSerializer.Deserialize<SmokeReply>(text, JsonDefaults.Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            Assert.Fail("The real completion did not match the strict JSON smoke-response schema.");
            throw;
        }
    }

    private static void RequireSafeSignalKind(AgentSignal signal) =>
        Assert.True(signal.Kind is "session" or "status" or "delta",
            "The provider emitted a signal kind outside the coordinator contract.");

    private sealed record SmokeReply(string Phase, string? Nonce);
}

public sealed class CopilotLiveSmokeFactAttribute : FactAttribute
{
    public CopilotLiveSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKSPACES_COPILOT_LIVE_SMOKE") != "1")
            Skip = "Explicit opt-in required: WORKSPACES_COPILOT_LIVE_SMOKE=1 consumes two Copilot Auto completions.";
    }
}
