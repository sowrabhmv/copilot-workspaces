# Trusted local agent pack

The workflow consumes **APM-prepared instructions**, not unused package metadata.
`AgentWorkflow` loads the prepared shared contract plus a coordinator,
producing-specialist, or reviewing-specialist template before invoking the selected
`IAgentProvider`. The demo and Copilot paths use the same workflow and pack.
Provider authentication, model policy, tools, and SDK session cleanup belong to
the provider, not this package. The three templates do **not** mean three fixed
agents: the coordinator can draft directly or choose zero to four named,
task-specific specialists. Live routing remains the host's fixed Auto policy.

## Source and generated files

| Path | Purpose |
| --- | --- |
| `apm.yml` | Consumer manifest; its only dependency is the real local package at `agent-pack\source`. No remote packs, MCP servers, or lifecycle scripts are declared. |
| `apm.lock.yaml` | Genuine APM 0.31.0 generated lock, including the local package identity and deployed-file hashes. Commit it; do not author it by hand. |
| `agent-pack\source\apm.yml` | `workspace-task-prompts` version `0.2.0`: task-driven teams and semantic visual forms. Local source packages are development dependencies, not immutable remote commit pins. |
| `agent-pack\source\.apm\instructions\workspace-contract.instructions.md` | Shared literal-UI schema, untrusted-input boundary, and human-review rules. |
| `agent-pack\source\.apm\prompts\*.prompt.md` | The three trusted role prompts. |
| `agent-pack\.build` | Ignored isolated APM consumer/cache. This avoids recursively compiling templates from `.tools` or unrelated application folders. |
| `agent-pack\prepared` | Committed runtime input: actual APM-deployed `.github` files, compiled context, manifests, and a preparation receipt. |
| `.tools\apm\0.31.0\apm-windows-x86_64` | Scoped standalone APM distribution. Keep `apm.exe` with its `_internal` directory. Do not commit this tooling. |

Preparation uses an explicit local dependency because an empty-dependency
consumer did not produce the needed lock/deployment with the pinned Windows
standalone CLI. The isolated consumer has the same manifest and relative package
path as the repository. Only generated outputs are copied back; package
resolution and prompt deployment are performed by APM itself.

## Prepare

From the repository root in PowerShell:

```powershell
pwsh -NoProfile -File .\scripts\install-apm.ps1
pwsh -NoProfile -File .\scripts\prepare-agent-pack.ps1
```

The installer downloads only the official APM **v0.31.0** Windows x86_64 archive
and its published SHA-256 sidecar, compares their hashes before extraction, and
checks the executable version. It does not use global pip or change PATH.

An existing trusted installation can be selected explicitly:

```powershell
pwsh -NoProfile -File .\scripts\prepare-agent-pack.ps1 -ApmExecutable 'C:\Tools\apm\apm.exe'
```

The wrapper requires exactly 0.31.0 and runs these supported APM operations:

1. `apm compile --validate --target copilot` within the local source package.
2. `apm install --target copilot --no-trust-bin` within the isolated consumer.
   When the root lock already exists, the wrapper adds `--frozen`.
   An explicit update is followed by a frozen replay: APM 0.31 otherwise retains
   the previous local package's display version until the next replay. The
   wrapper lets APM refresh that metadata before publishing the lock/receipt.
3. `apm audit --ci --format json` against the installed files and lock.
4. `apm compile --root <prepared-directory> --target copilot --force-instructions`.
   The explicit flag refreshes compiled context even when native instruction
   deployment would otherwise make the compiler skip that output.

For a deliberate source/package update, review the source changes first, then:

```powershell
pwsh -NoProfile -File .\scripts\prepare-agent-pack.ps1 -UpdateLock
```

This explicitly runs `apm install --update` instead of frozen installation; it
does not introduce remote dependencies or run agents. Review and commit the source, root lock, and prepared output
together. Do not edit deployed prompt files manually.

The wrapper scopes `APM_HOME` under `.tools\apm\home` and sets the documented
`APM_NO_SCRIPTS=1` kill switch for all lifecycle-script tiers. Both environment
values are restored afterward. It never calls `apm run`, enables executable
extensions, installs unknown packs, or invokes a model. Organization policy
discovery may access GitHub and can print a missing-policy-repository warning;
the wrapper does not bypass policy or suppress audit failures.

The observed baseline audit passed all ten lock/deployment/content checks.
Organization-policy lookup also reported an unavailable private repository and
one timeout; APM's default warning policy skipped that remote enforcement. A
baseline pass is **not** a claim that organization policy was verified. A
deployment requiring that policy must configure a blocking fetch-failure policy
and resolve the policy access/availability separately.

## Runtime integrity and host integration

`manifest.json` is an application preparation receipt containing the exact
package/APM versions, the prepared lockfile's SHA-256, and SHA-256 values for four
allowlisted deployed text files. `TrustedAgentPack` verifies these before each
run, loads a single instruction snapshot for the entire chosen workflow, rejects unknown
paths or oversized/unreadable files, and never falls back to source prompts.
Failures are explicit `agent_pack_unavailable` or `agent_pack_invalid` errors.
Hashes detect drift; this is not a signature or protection against a malicious
local user who can replace both the files and receipt.

Register:

```csharp
services.AddSingleton(workspaceOptions);
services.AddSingleton<IAgentProvider, CopilotAgentProvider>();
services.AddSingleton<IAgentProvider, DemoAgentProvider>();
services.AddSingleton<IWorkspaceWorkflow, AgentWorkflow>();
```

Constructor dependencies:

- `AgentWorkflow(IEnumerable<IAgentProvider>, WorkspaceOptions)`.
- `DemoAgentProvider(WorkspaceOptions)`.

The default runtime directory is
`Path.Combine(AppContext.BaseDirectory, "agent-pack")`. The host project must copy
`agent-pack\prepared` into that output/publish directory, including hidden
`.github` directories. Alternatively configure `WorkspaceOptions.AgentPackDirectory`
with the trusted prepared directory. Do not serve the pack, source, `.tools`, or
provider homes as browser static content.

For the server project at `src\Workspace.Server`, the parent-owned content rule is:

```xml
<Content Include="..\..\agent-pack\prepared\**\*"
         Link="agent-pack\%(RecursiveDir)%(Filename)%(Extension)"
         CopyToOutputDirectory="PreserveNewest"
         CopyToPublishDirectory="PreserveNewest" />
```

`agent-pack\.gitattributes` preserves prepared bytes so Git line-ending
conversion cannot invalidate the receipt. The source package forces LF text.
Prepare before building/publishing, and do not update a directory actively used
by a running service.

## Workflow and demo behavior

The first real MAF graph runs only the coordinator (`AgentId: planner`, displayed
as Coordinator). Its validated response selects a clarification, direct artifacts,
or zero to four specialist assignments. A non-clarifying empty plan is an error,
not permission to restore a default team. Clarification cannot also draft or
recruit. The coordinator can finish a simple complete task without specialists;
its result has no invented AI review.

The workflow then emits exactly one coordinator `roster` signal with the
validated team, awaiting the host's transactional registration before any worker.
The second MAF graph contains **exactly those declared specialists**, sequentially
connected by typed candidate-state edges. Each producer receives and returns the
whole candidate set, preserving existing identifiers. A reviewer is optional and
requires an existing/direct/upstream draft. Review-only plans work on existing
drafts without a forced producer. Review text is attributed to the actual name
and candidate version, including when a later producer revises an earlier review's
draft. Per-review budgets keep the combined text within 8000 characters.

It does not use the native Copilot adapter, a no-op framework wrapper, chat
`TurnToken`s, or generated executable agent definitions. Specialist `AgentId`
values come from the validated task plan; `OutputKind` remains the generic
`planner` / `producer` / `reviewer` response contract. Assignment names/purposes
are JSON task data, never privileged system instructions or provider/tool/model
configuration. Provider callbacks cannot declare another roster.

Every role receives JSON-serialized task data, original feedback
target/revision/quote, latest working artifacts, and upstream stage output.
The version-2 prompt envelope includes saved identities without session IDs,
the current assignment/team, candidate artifacts, and earlier named reviews.
It never serializes future queued feedback. Appropriate historical identities
reuse their saved sessions; unused members are deactivated by the host, not
deleted. Plans exceeding twelve historical specialists fail explicitly.
User text never becomes trusted pack instructions. Later answer/feedback jobs
cannot ask blocking questions again. Targeted refinements must retain the exact
artifact ID, root, and existing element IDs. All JSON and specs are validated
before downstream routing; no repair or success-shaped fallback is used.
Coordinator summaries are limited to 4000 characters; combined attributed
reviews are limited to 8000. With no review specialist, Review is null.
The host still owns artifact revisions, conflict handling, persistence, and
human acceptance/rejection.

### Visual questions and explicit refresh

Clarifications choose controls from their meaning, rather than defaulting to
text boxes: choice cards for categories, checkbox chips for sets, labelled sliders
for scales, numeric inputs for quantities, switches for preferences, and date
inputs for deadlines. Only genuinely open information uses text. The trusted
contract aims for 2-4 primary decisions and at most one ordinary open field.
`ClarificationQualityPolicy` rejects newly generated forms with more than four
required questions or two TextQuestions. This is separate from structural
`SpecValidator` checks, so old forms remain readable and answerable.

A `clarify` job requires the current saved form and must return another valid
form with no artifacts or recruited agents. The workflow does not alter the
old form; the host publishes a new identity only after success. Failed or
cancelled refresh leaves the prior data intact.

### Deterministic demo

The explicit `demo` provider makes no model calls. A fully specified one-sentence
thank-you can be drafted directly. A complete short email can select only a
communications writer. Broader launch, research, event, and planning tasks use
different identities and purposes. Confirmed detail/stakeholder scope may add
a second producing specialist; a review preference can add or omit a reviewer.

The default rich launch form uses `detail`, a 1-5 slider labelled **Executive
Brief / Concise Overview / Working Plan / Detailed Guide / Full Playbook**,
`stakeholders`, multi-select team chips, and an optional `review` switch.
Date (`deadline`) and numeric (`budget` or `quantity`) controls are added only
when relevant and not already specified; controls are not shown merely to
demonstrate the catalog. Defaults are visible suggestions confirmed by Continue.
Answers stay API-compatible strings: invariant decimals, JSON-encoded option
arrays, lowercase true/false, and ISO dates. Malformed typed values fail rather
than being silently converted into decisions.

Artifacts include useful Text/BulletList/DataTable/Decision examples, human-readable
saved decisions, and the selected specialist's contribution. Later producers
refine actual upstream candidates; feedback uses the latest working text, never
replaces it with an older quote, and is not applied twice by successive producers.
Sessions are stable per workspace/agent and cannot be reused across either
boundary. `DemoDelayMilliseconds` accepts **0-5000** and is cancellation-aware.
The demo does not claim realistic inference quality or execute real-world work.

Cancellation links the caller and executor tokens, explicitly cancels a MAF run
when observation is interrupted, and disposes the run while waiting for provider
cleanup. A live Copilot provider must implement confirmed abort/owned-runtime
cleanup; MAF event-stream cancellation alone is not proof inference stopped.
If a provider reports that abort/cleanup could not be confirmed, the workflow
preserves that error even after its event reader has been cancelled instead of
misreporting successful cancellation.

Focused verification:

```powershell
dotnet test .\tests\Workspace.Server.Tests\Workspace.Server.Tests.csproj --no-restore --filter 'FullyQualifiedName~WorkflowTests|FullyQualifiedName~DemoProviderTests'
```

One test loads the actual prepared APM pack and runs a task-selected demo team.
Other tests cover direct drafts, optional/review-only teams, exact four-specialist
routing, candidate/review dependencies, roster ordering, saved-session reuse,
invalid capability/identity plans, visual form quality, typed answers, explicit
question refresh, schema/identity rejection, future-feedback isolation, provider
failures, pack tampering, and cancellation cleanup. Live account/model compatibility is
the separately verified Copilot provider's responsibility.

Sources: [APM 0.31.0](https://github.com/microsoft/apm/releases/tag/v0.31.0),
[install](https://microsoft.github.io/apm/reference/cli/install/),
[compile](https://microsoft.github.io/apm/reference/cli/compile/),
[lockfile](https://microsoft.github.io/apm/reference/lockfile-spec/),
[lifecycle kill switch](https://microsoft.github.io/apm/enterprise/lifecycle-scripts/).
