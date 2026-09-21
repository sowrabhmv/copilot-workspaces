# Copilot provider

`Workspace.Server.Providers.CopilotAgentProvider` implements `IAgentProvider` with the
official `GitHub.Copilot.SDK` **1.0.14**. It does not use the native MAF adapter:
the application's typed MAF executors call this provider so that create and
resume share the complete restriction policy.

## Registration and configuration

Register the provider as a singleton. Its public constructor is
`CopilotAgentProvider(WorkspaceOptions, ILogger<CopilotAgentProvider>)`.
Register the already-configured `WorkspaceOptions` instance and add the provider
to the application's `IEnumerable<IAgentProvider>`:

```csharp
using Workspace.Server.Providers;

builder.Services.AddSingleton(workspaceOptions);
builder.Services.AddSingleton<IAgentProvider, CopilotAgentProvider>();
```

`WorkspaceOptions.DataDirectory` must be nonempty and writable by the local
application user. `CopilotExecutable` may contain a trusted absolute `.exe`
path. Otherwise resolution checks `COPILOT_CLI_PATH`, absolute entries in
`PATH`, and the user's WinGet link. Explicit invalid configuration fails rather
than falling back. Shell command strings, relative executable paths, and npm
`.cmd` shims are not accepted. Never bind these settings to browser input.

`ProviderTimeoutSeconds` is the entire completion budget, including startup,
auth/model checks, session creation, callbacks, inference, and final policy
verification. The adapter accepts 1-900 seconds; the application host deliberately
narrows configuration to 10-600 seconds. The shared default is 180.
The project sets `CopilotSkipCliDownload=true`; this adapter does not install or
upgrade the executable. The researched machine has CLI **1.0.87-0 preview**,
whereas the SDK's default bundle is **1.0.85**. Runtime protocol **3** is checked,
not assumed from a version string.

## Isolated authentication and preflight

Each invocation owns a separate managed stdio runtime, including status
requests. Runtime state lives in `DataDirectory\copilot\home`; working
directories are application-owned and named with a hash of the workspace ID.
Empty mode, disabled keychain access, and an explicit safe environment whitelist
prevent inheriting ambient tokens, permission switches, extension variables,
or custom-instruction paths. The whitelist reads only named OS/runtime path
variables, never credential variables or credential files. The CLI itself
performs supported authentication; no credential is returned to the browser.

`GetStatusAsync` starts the runtime, reads status/auth/model metadata, and stops
it. It never creates a session or requests a completion. The preflight budget
is 20 seconds, plus bounded cleanup. States are `ready`, `setupRequired`,
`unsupported`, or `unavailable`. A ready status means the account advertises the
required model; each completion separately verifies its effective session
policy.

An unauthenticated isolated home returns a `SetupCommand` similar to:

```powershell
$env:COPILOT_HOME = 'APP_DATA\copilot\home'; $env:COPILOT_DISABLE_KEYTAR = '1'; & 'ABSOLUTE_PATH\copilot.exe' login --device-code
```

Use the actual command supplied by status, in a separate PowerShell window.
The application never executes login, reads credential files, copies tokens,
or weakens the isolation profile to reuse another home. With keychain disabled,
CLI authentication may store credentials in its isolated home; protect the
application data directory and do not serve, commit, or share it. The app home
must survive application restarts for sessions and authentication to survive.

## Policy and persistence

All roles select `auto` on both create and resume. No reasoning effort is
forced. Preflight requires an enabled account model and respects an explicitly
disabled Auto entry; it does not require Astra, `max`, or an Auto alias to be
listed as a concrete model. Custom API-key authentication is not accepted.
Underlying models may legitimately vary under Auto. There is no alternate
provider or hidden demo fallback.

Both create and resume apply an explicit empty tool allowlist; exclusions for
all builtin, MCP and custom tools; an unconditional rejecting permission
handler; and disabled config discovery, instructions, skills, plugins, file
hooks, host git, embeddings, memory, cross-session retrieval, extensions,
remote sessions and scheduling. MCP startup is disabled. The runtime is given
no paths/attachments or custom-agent registrations. These are capability
restrictions, not a general OS sandbox or a claim that inference needs no
network access.

The adapter clears the old host-supplied fixed-model allowlist with the pinned
SDK's documented null request. It does not clear provider/organization policy.
The authoritative `model.getCurrent` snapshot must show the selected virtual
model `auto` before and after inference, together with an initialized empty
tool set. Concrete usage-model/effort events are normal Auto routing metadata,
not manual-selection violations; they are not exposed as reasoning content.
Tool-start and session-error events still stop the turn.
These generated RPCs are experimental in the GA SDK;
`GHCP001` is suppressed only in the small SDK wrapper. Missing/incompatible
verification is a hard failure, never permission to continue.

Session IDs are explicit UUIDs. Resume does not replace missing sessions and
uses `ContinuePendingWork=false`. The `AgentSignal` with kind `session` is
awaited immediately after opening the requested session, before policy checks
or any prompt, so the coordinator can persist it even if a later step fails.
The session signal has an independent bounded persistence wait, not the job's
cancelled token; cancellation immediately after opening a session does not
suppress its identifier. No policy operation or prompt follows a cancelled
session notification. Only `session`, `status` and `delta` signals are emitted.
The provider never writes the application store or starts application jobs.
Session history survives owned runtime shutdown; application artifacts,
feedback, approval records and replay cursors belong to the coordinator.

**Empty-session limitation:** the installed CLI 1.0.87-0 creates a session
directory but does not cold-resume a session that has never received a prompt.
Upstream SDK persistence tests explicitly send a message before checking
durability. If a run fails or is cancelled between the session callback and its
first accepted prompt, retry can therefore return `copilot_session_unavailable`.
The provider does not silently discard that saved context or create a
replacement. Recovery requires a host/user decision to start a new session;
cold resume after a real completed turn has been verified by the separately
gated live integration test below.

## Streaming, errors, and cancellation

The SDK uses streaming, but public progress consists of fixed messages and
character counts. It never forwards raw JSON fragments, reasoning, CLI stderr,
exception text, paths from provider errors, or authentication metadata.
Structured final text is returned only to the workflow for strict validation.
Requests carry system provenance because they are host-orchestrated work;
model text cannot authorize human approval.

The whole-operation deadline includes prompt acknowledgement, unlike the
SDK's idle-wait timeout. A heartbeat detects an unresponsive runtime while a
turn is pending. Output is capped at 1,048,576 characters. Blank output,
unverified configuration, callback failure or runtime failure is an explicit
`AgentProviderException` with a safe code/message.

Explicit job cancellation throws `OperationCanceledException` only after
cleanup. The adapter calls `AbortAsync` with a fresh cleanup token, then stops
the owned client. Abort, graceful stop and forced stop each have a 3-second
budget; disposal has a 2-second budget. A stuck abort/stop escalates to the
SDK's `ForceStopAsync`. Failed cleanup is `copilot_cleanup_failed`, never a
confirmed success/cancellation. No process-name-wide termination is used.
The coordinator preserves cleanup failures as failures even after a user asks
to cancel, and interrupts that workspace's remaining queue until explicit
human-directed recovery.
Cancelling pending local waits has a separate 2-second cleanup budget.
Each invocation owns its process, so cancelling one workspace cannot stop a
different workspace's process. The job coordinator must pass its job lifetime
token, not an HTTP/SSE subscriber token, and await active jobs on shutdown.

The provider is stateless and does not require `IAsyncDisposable` registration.
SDK/client/session operations are behind a small internal seam so focused
tests can exercise failure, persistence, cancellation and concurrent ownership
without credentials, model calls, or real CLI processes.

Run the focused tests with:

```powershell
dotnet test tests\Workspace.Server.Tests\Workspace.Server.Tests.csproj --filter FullyQualifiedName~CopilotProviderTests
```

An optional `InstalledCliReadOnlyPreflight` test is skipped unless
`WORKSPACES_COPILOT_PREFLIGHT=1` is set. It uses a fresh temporary isolated home
and only calls `GetStatusAsync`; it cannot generate a model completion.
It reports safe status/version metadata and removes that temporary home after
the runtime stops. It does not prove that the application's persistent home
has been authenticated. A second opt-in
`InstalledCliRestrictedCreateAndEmptySessionLifecyclePreflight` verifies a
new session's effective model/tool policy and the observed missing-session
result when cold-resuming that never-prompted session. It never sends a prompt
and does not claim to validate conversation-history resume.

The separately gated `CopilotLiveTests` integration test **does consume model
credits**, only when `WORKSPACES_COPILOT_LIVE_SMOKE=1` is explicitly set:

```powershell
$env:WORKSPACES_COPILOT_LIVE_SMOKE = '1'
dotnet test tests\Workspace.Server.Tests\Workspace.Server.Tests.csproj --no-restore --filter FullyQualifiedName~CopilotLiveTests --logger 'console;verbosity=detailed'
Remove-Item Env:WORKSPACES_COPILOT_LIVE_SMOKE
```

It makes exactly two sequential real provider calls, each bounded to 150
seconds: remember a nonsecret synthetic nonce as strict JSON, then recall it
using the actual saved session ID in a new owned runtime without repeating
the nonce. It does not change the model, relax tools, log in, read credential
files, manually seed transcripts, or automatically retry. The temporary app
home is deleted only after provider cleanup has completed; an unconfirmed
shutdown retains that scoped directory instead. Normal tests skip this case.
This verifies the provider, not the application's full HTTP/browser workflow.

Initial fixed-model baseline, verified on **20 September 2026** against **CLI 1.0.87-0** and
restored **SDK 1.0.14**: the normal server build succeeds; 55 automatic provider
cases pass; both opt-in metadata-only checks pass. Live status reports `ready`,
and the real new-session policy probe confirms `gpt-6-astra`, `max`, a singleton
effective model allowlist, and zero initialized tools. Those metadata-only
checks did not send a prompt.

**Authorized completion evidence, 20 September 2026:** the gated live test
passed on its first execution, with exactly two provider completion calls and
no retry or model substitution. The initial real completion acknowledged the
synthetic nonce as strict JSON in **9.1 seconds**. After that owned runtime had
stopped, a new provider instance and owned runtime cold-resumed the same actual
session ID and returned the exact earlier nonce as strict JSON in **8.5
seconds**, without the nonce appearing in the second prompt or instructions.
Both calls traversed the real provider's pre/post model, effort and tool
checks. Session callbacks matched returned IDs, both owned runtimes stopped,
and the scoped temporary data directory was successfully removed. Total test
runner time was **18.6 seconds**. No credential files were read, no login or
global configuration change was performed, and no third completion was made.
This establishes completion and history-bearing cold resume for the provider;
it is not evidence of full application HTTP/browser E2E behavior.

**Auto selection update:** the same real CLI passed metadata probes with model
`auto`, no host fixed-model allowlist, and zero initialized tools. The gated
two-completion test then passed using Auto for both calls: **8.1 seconds** for
the initial acknowledgement and **6.8 seconds** for history-bearing cold resume.
The nonce was not repeated in the recall prompt. Pre/post selected-mode and
tool checks passed, both owned runtimes stopped, and temporary state was
removed. This supersedes the baseline's Astra/max requirement without removing
authentication, session isolation, cancellation, or no-tools controls.

## Official API sources

- [SDK 1.0.14 release](https://github.com/github/copilot-sdk/releases/tag/v1.0.14)
- [.NET SDK configuration](https://github.com/github/copilot-sdk/blob/v1.0.14/dotnet/src/Types.cs)
- [SDK lifecycle](https://github.com/github/copilot-sdk/blob/v1.0.14/dotnet/src/Client.cs)
- [SDK send/abort behavior](https://github.com/github/copilot-sdk/blob/v1.0.14/dotnet/src/Session.cs)
- [Persistence tests requiring a first message](https://github.com/github/copilot-sdk/blob/v1.0.14/dotnet/test/E2E/SessionE2ETests.cs)
- [Generated policy RPCs](https://github.com/github/copilot-sdk/blob/v1.0.14/dotnet/src/Generated/Rpc.cs)
- [Permission decisions](https://github.com/github/copilot-sdk/blob/v1.0.14/dotnet/src/PermissionDecision.cs)
