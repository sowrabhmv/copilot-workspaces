# Workspaces: MVP architecture and compatibility plan

Research completed **20 September 2026**, before stack selection. This is a
single-user, local browser application, not a cloud deployment or a general
workflow builder.

## Decision and alternatives

Use **React + TypeScript + Vite** for the browser and **ASP.NET Core / .NET 10**
for the loopback service. Microsoft Agent Framework runs a bounded, real typed
executor graph: **Coordinator -> only the specialists needed by the task**.
The coordinator can return a direct artifact without spawning subagents.
Each executor calls an
application-owned provider interface implemented with the official Copilot SDK.
This small explicit integration is not presented as a first-party adapter.

| Candidate | Compatibility evidence | Decision |
| --- | --- | --- |
| React/Vite + .NET | JSON Render has a React renderer; MAF has native .NET workflows; SDK supports .NET 10; both runtimes are installed | Selected; no unnecessary intermediate model service |
| React/Vite + Python/ASGI | Native MAF and Copilot adapter exist, but the installed Python 3.9.5 is incompatible; Copilot requires 3.11+ | Viable after a runtime upgrade; no benefit for this machine |
| Node-only backend | Copilot has a TypeScript SDK; no native TypeScript MAF orchestration SDK was found in the official support surfaces | Would omit a required technology or need a .NET/Python sidecar and another RPC bridge |
| Next.js + .NET | Next 16.3.5 supports this Node runtime, but SSR/server components add no value to this private client-side canvas | Avoid a redundant server runtime |
| Blazor-only frontend | Strong .NET fit, but JSON Render's supported React renderer would require a JS interop island | Unnecessary rendering/integration complexity |

### Versions and maturity

| Technology | Researched / selected baseline | Limitations and compatibility assumptions |
| --- | --- | --- |
| .NET / ASP.NET Core | SDK **10.0.401**, runtime **10.0.12**, LTS through 14 November 2028 | Installed and compatible; project targets `net10.0` |
| Microsoft Agent Framework | `Microsoft.Agents.AI.Workflows` **1.22.0**, stable release 18 September | In-process graph, not a durable job service. Stream cancellation is not execution cancellation |
| Native MAF Copilot adapter | **1.22.0**, researched but not used | Upstream tested against SDK 1.0.11; resume drops some newer restriction fields. Direct SDK integration keeps the complete policy in one reviewed adapter |
| GitHub Copilot SDK | **1.0.14**, GA, 16 September | Protocol v3 only. Generated model/permission RPCs still carry experimental `GHCP001`; isolate them in the adapter |
| Copilot CLI | SDK bundle **1.0.85**; latest stable **1.0.86**; installed **1.0.87-0** preview | Use an explicit executable and preflight protocol/model compatibility. Builds do not silently download/upgrade CLI |
| Microsoft APM | **0.31.0**, 15 September, pre-1.0 | Build-time context/package tooling, not execution. `apm run` is experimental and unnecessary. Python installation requires 3.10+; Windows standalone exists |
| JSON Render | `@json-render/core` / `@json-render/react` **0.21.0**, 18 September, pre-1.0 Vercel Labs project | React peer `^19.2.3`; no experimental composition. Catalog validation alone is insufficient |
| React | **19.3.0** matching React DOM | Meets JSON Render peer requirements; exact installed pins are in the frontend lockfile |
| Node | Installed **22.23.2** | Meets Vite's 20.19+/22.12+ requirement; build-time only after the static frontend is built |
| Vite / Zod | **8.3.0** / **4.3.6** | Pinned browser build tool and JSON Render-compatible schema baseline |
| SQLite | `Microsoft.Data.Sqlite` **10.0.12** | Local durable store with WAL and transactions; synchronous driver operations are short and serialized |

The package lockfiles are the executable dependency record. Preview CLI
compatibility and account availability require a live preflight; public
documentation is not proof of entitlement. Per the updated product requirement,
all create/resume paths select **`auto`** and leave reasoning effort unset.
The old fixed-model restriction is cleared; provider routing and organization
policy choose the underlying model. Hidden provider or demo substitution is
not allowed.

The configured npm mirror did not expose JSON Render 0.21.0. Exact published
files were SHA-256 checked against UNPKG metadata and packaged unchanged into
versioned local tarballs with licenses and provenance in `web\vendor`. The
lockfile uses those archives; this is not a downgraded or reimplemented renderer.

APM preparation and its baseline audit were exercised against the actual local
pack. Organization-policy lookup produced availability warnings; that remote
policy was not verified. Runtime receipt hashes detect prepared-file drift,
not a malicious local user who can replace both the files and receipt.

## Design discovery

The supplied `..\Prototype` was inspected read-only, including its canvas,
context panel, steering popover, document editor, reducer, styling, provenance,
and concurrency/viewport test patterns.

Figma screens actually reviewed: Relay `198:6` (task entry), `198:446` and
`216:692` (configured canvas), `198:671` (anchored changes), and `198:859`
(agents working). The One Copilot Desktop UI Kit composer instances
`455:50387`, `455:50388`, and `455:50389` were also inspected. A real
click-to-agents-working transition was verified; not every visual frame has
wired interactions.

Preserve the updated local prototype's neutral Segoe/system-font treatment:
white cards, `#fafafa` canvas, `#dedede` borders, `#242424` primary text/actions,
`#616161` secondary text, blue selection, 248px entry sidebar, a rounded outcome
composer, and independently positioned objective, agent, and artifact cards.
Keep pan/zoom/fit and an unscaled, viewport-clamped contextual feedback surface.
Use genuine Fluent icons, not drawn approximations or emoji product logos.

**Design limitation:** the Figma variable-definition request was unavailable
without a selection. Effective node properties, bindings, screenshots and local
CSS were reviewed; a complete named variable/mode export was not obtained.
The prototype's timers, sample integrations, fake history, and reset-on-refresh
behavior are not carried into the live engine.

## Responsibility boundaries

| Layer | Responsibility |
| --- | --- |
| Browser | Task navigation, spatial canvas, answer drafts, selection/anchors, JSON Render registry, feedback submission, review controls, accessible progress |
| Backend application | Durable jobs and feedback, workspace isolation, revisions and human decisions, validation, bounded scheduling, errors and restart recovery |
| MAF | The coordinator and dynamically selected specialist executor graph, typed routing and bounded stage execution |
| Copilot SDK / CLI | Authenticated provider integration, separate role sessions, model policy, streamed messages, explicit abort and owned-process cleanup |
| APM | Authored trusted text-pack discovery, metadata, version/lock lifecycle and pre-runtime preparation; never automatic execution of unknown packs |
| HTTP + SSE | Commands and safe application events with monotonic replay IDs, heartbeat, reconnect and canonical-state refresh; not SDK JSON-RPC |
| Declarative schema generation | Role prompts request a bounded literal UI document; the backend validates it before it becomes a proposal |
| JSON Render | Renders the already-validated component tree. It does not authorize actions, manage jobs, resolve conflicts or grant human approval |

## MVP interaction and implementation sequence

1. Define the shared API/domain/UI contract after discovery.
2. In parallel, implement the restricted Copilot adapter, real MAF workflow and
   trusted APM pack, browser experience, and durable local HTTP service.
3. A task creates its own saved workspace with one coordinator. It asks only
   necessary decisions through semantically appropriate JSON-rendered controls,
   or drafts directly. Sliders, multi-choice chips, numbers, dates, switches and
   choice cards replace text entry where that reduces effort.
4. Answers enqueue work. A validated coordinator plan declares zero to four
   task-specific specialists. Only those roles receive sessions and run.
   Artifact proposals remain human-reviewed; an AI reviewer is not forced and
   the UI does not fabricate a review when none was requested.
5. A user selects a block and submits direction. Each submission is persisted
   with its original artifact revision, stable block ID and quoted context
   before acknowledgment. More feedback remains available during execution.
6. Jobs run FIFO within a workspace, with bounded parallelism across workspaces.
   Each job uses the latest working version plus the original feedback anchor.
   Changes are individually traceable in the activity/feedback ledger.
7. Accept/reject and direct text edits are trusted application actions. An agent
   result based on a version changed by a human is a conflicted proposal, never
   an overwrite. Accepted revisions and decision history remain available.
8. Test the complete local interaction loop, rejection/error paths, cancellation,
   revision races, restart persistence and browser reconnection before considering
   any deployment.

### Persistence and background work

SQLite stores versioned workspace documents and append-only public events in the
same transaction. The queue is durable, not just an in-memory channel. One
writer per workspace prevents feedback races. The UI never waits for inference
to acknowledge feedback. Browser/SSE disconnection detaches that subscriber;
only an explicit job cancellation (or shutdown/deadline) cancels agent work.

On restart, unfinished work is visibly interrupted and requires explicit retry;
it is never silently marked successful or replayed into potential duplicate
charges. Workspace history, artifacts, anchors, answers, and layout remain.
The application owns this persistence; MAF checkpoints are not a substitute
and Copilot session IDs are not portable transcript snapshots.

Local MVP limits are 100 workspaces, 300 run records per workspace, 20
queued/active requests per workspace, and 12 artifact surfaces per workspace.
Limits produce explicit errors; history is not silently pruned. Each provider
invocation defaults to a 180-second whole-operation budget. A run has one
coordinator and at most four specialists, with a corresponding aggregate
deadline. Historical role sessions are preserved, with at most twelve saved
specialist identities. These are bounded local execution
choices, not distributed-scale guarantees.

### Provider policy and errors

Only the local service launches a managed stdio child process. Credentials stay
with CLI-managed authentication and never enter the browser or event stream.
No arbitrary executable paths, model overrides, SDK RPC, plugins or tools are
accepted from browser requests. Every role selects Auto; reasoning is managed
by the runtime rather than forced to a model-specific value.

Create and resume must reapply empty tool allowlists, reject permissions, and
disable instructions/config discovery, skills, plugins, file hooks, host git,
MCP startup, extensions and remote sessions. These controls are **not an OS
sandbox**. An isolated empty-mode home can require separate CLI login; report
that explicitly rather than reverting to an unrestricted home.

Whole-operation deadlines must explicitly abort sessions; `SendAndWait` timeout
alone does not stop inference. Stop an unresponsive **owned** runtime, never
unrelated user processes. Distinguish missing executable, auth, protocol, model,
timeout, cancellation, invalid output and process errors. Do not reveal raw
provider exceptions, tokens, private reasoning, or local credential paths.
If owned-runtime cleanup cannot be confirmed, the job is failed rather than
labelled cancelled, and its workspace's remaining queue is interrupted. A
human must verify cleanup before explicitly retrying; uncertainty is not
treated as permission to continue spending model usage.

### Declarative UI trust boundary

The authoritative schema is a strict literal tree, not `catalog.validate`.
Allow only Section, Heading, Text, BulletList, DataTable, Decision, and the
seven question controls: ChoiceQuestion, MultiChoiceQuestion, SliderQuestion,
NumberQuestion, ToggleQuestion, DateQuestion and TextQuestion.
Validate props by component type, stable IDs,
depth, node count, string lengths, table shape and single-parent reachability.
Reject cycles, unknown components/props, orphans and executable/dynamic fields.

Model output cannot supply `on`, `watch`, `state`, navigation, arbitrary styles,
URLs, HTML, expressions or code. Host-controlled inputs/actions are wired
outside that untrusted document. In particular, JSON Render's built-in actions
can bypass a custom handler map, and a single `useUIStream.send` aborts the prior
request; neither mechanism is used as an authorization or feedback queue.

### Local HTTP boundary

Bind loopback only. Validate Host/Origin, reject cross-site browser requests,
use a process-local anti-forgery header on mutations, do not enable permissive
CORS, and serve production assets from the same origin. Cap request sizes,
queued work and schema complexity. Never serve `.data`, provider homes or source
trees as static content. No cloud exposure or production security claim.

## Explicitly excluded

Native mobile, multi-user features, tenant/billing/marketplace systems, general
no-code workflows, arbitrary executable code, unknown remote agents, distributed
execution, production deployment, live SharePoint/M365 integrations, and
pixel-perfect redesign of screens outside this vertical slice.

## Verification record, 20 September 2026

The counts and fixed-Astra run below record the initial delivered baseline.
The subsequent Auto/adaptive-UI update retains the same safety boundaries.
Auto metadata probes and a real two-completion cold-resume check passed with
the selected session model remaining `auto`, no forced effort, and zero tools.

- Locked NuGet restore and a fresh `npm ci` reproduce the application; the
  frontend build and TypeScript checks pass.
- 147 normal backend tests and 54 frontend tests cover validation, isolation,
  queue ordering, anchored context, revision conflicts, cancellation, uncertain
  cleanup, restart recovery, provider policy, rendering and browser-state races.
  Three real-provider tests are opt-in and skipped by ordinary test runs.
- Real Microsoft Edge browser flows exercise task creation, clarification,
  multiple feedback submissions before agents finish, accepted/history views,
  human edits, reload, task isolation, rejection, cancellation/retry, and a
  readable 390px layout with an in-viewport anchored composer.
- The installed CLI completed a real three-role MAF workflow using exact
  GPT-6 Astra/max, producing a validated agenda table and checklist. Its three
  durations were 5, 15 and 10 minutes; the result remained an unaccepted proposal.
  That live artifact was rendered in the browser, not replaced with demo data.
- A separate opt-in SDK test made two real completions across distinct owned
  runtimes and recovered a synthetic nonce from persisted conversation history
  without restating it in the second prompt. Pre/post model and empty-tool
  policy verification passed. Metadata-only real-runtime probes also passed.
- The live workspace's explicit session-recovery UI was exercised: Cancel
  preserved the session; confirmation cleared only the selected role reference,
  preserving artifacts and jobs and creating no new inference job.

These are local MVP checks, not production certification. The documented
Figma-variable export and remote APM organization-policy limitations remain.

### Adaptive GenUI update

The update is verified with **250 backend tests**, **108 frontend tests**, and
**six real Edge browser flows**. These cover the seven JSON-rendered question
types, typed answer persistence, visible defaults, optional refinements, live
selection summaries, unscaled question focus, explicit regeneration, and
task-dependent teams in addition to the original collaboration safeguards.
Unused legacy specialist placeholders are hidden on read without deleting
their records or any previously used sessions.

Real Auto-provider application checks also passed:

- A fully specified one-sentence task produced a direct coordinator artifact
  with no subagents and no fabricated AI review.
- An ambiguous launch task produced Choice, Slider, Number and Date questions.
  After explicit regeneration with the cognitive-load guidance, audience/detail
  were primary decisions, budget/date were optional, and no currency was
  assumed. The live controls were rendered and manipulated in the browser.
- A task explicitly needing independent perspectives recruited a named
  Prototype Approach Analyst and Independent Risk Reviewer and completed the
  corresponding real MAF specialist workflow.

Existing stored forms are intentionally not rewritten or regenerated during an
upgrade. **Regenerate questions** asks the coordinator to modernize their
interaction design explicitly. Historical local drafts remain stored.

## Official sources

- [MAF overview](https://learn.microsoft.com/en-us/agent-framework/overview/)
- [MAF .NET 1.22.0](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.22.0)
- [Copilot SDK 1.0.14](https://github.com/github/copilot-sdk/releases/tag/v1.0.14)
- [Copilot SDK source at the researched release](https://github.com/github/copilot-sdk/tree/v1.0.14)
- [APM quickstart](https://microsoft.github.io/apm/quickstart/)
- [APM 0.31.0](https://github.com/microsoft/apm/releases/tag/v0.31.0)
- [JSON Render](https://json-render.dev/)
- [JSON Render 0.21.0](https://github.com/vercel-labs/json-render/releases/tag/v0.21.0)
- [Vite runtime requirements](https://vite.dev/guide/)
- [Next.js comparison baseline](https://nextjs.org/docs/app/getting-started/installation)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [ASP.NET Core SSE](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0)
- [SQLite concurrency and async limitations](https://learn.microsoft.com/dotnet/standard/data/sqlite/async)
- [Relay canvas](https://www.figma.com/design/wVQ8aGLhVB2Y7WAUFc9LZS/Relay?node-id=216-692)
- [One Copilot UI kit](https://www.figma.com/design/WcFgnWHcyDDy4GMsqnvYhp/One-Copilot-Desktop-UI-Kit?node-id=425-1685109)
