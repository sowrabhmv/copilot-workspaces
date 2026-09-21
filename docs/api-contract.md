# MVP application contract

The authoritative server records are `src\Workspace.Server\Domain.cs` and
`AgentContracts.cs`. HTTP JSON uses camelCase. All timestamps are ISO 8601.
Only `copilot` and the explicitly labelled `demo` provider are allowed.
Live sessions always select Copilot's virtual `auto` model on create and resume.
`reasoningEffort` in bootstrap/provider status is nullable; null means the host
does not force an effort value. Runtime routing chooses the underlying model.
Every failed request returns `{ "code": "...", "message": "..." }` with an
appropriate non-2xx status; never an empty success-shaped fallback.

## Endpoints

Base: same-origin `/api`. Local service `http://127.0.0.1:5080`; optional Vite
development proxy `http://127.0.0.1:5174`. Do not use occupied prototype port 5173.

| Method / path | Body / response |
| --- | --- |
| GET `/bootstrap` | `{ csrfToken, model, reasoningEffort, providers: [{id,label}], version }`; token is app anti-forgery, never a GitHub credential |
| GET `/health` | `{ status: "ok" }` |
| GET `/provider` | Copilot `ProviderStatus` (`ready`, `setupRequired`, `unavailable`, `unsupported`); bounded preflight, no completion |
| GET `/workspaces` | `WorkspaceSummary[]`, most recently updated first |
| POST `/workspaces` | `{ objective, provider }` -> 201 `WorkspaceDocument`, initial job already durably queued |
| GET `/workspaces/{id}` | Canonical `WorkspaceDocument` |
| POST `/workspaces/{id}/answers` | `{ clarificationId, answers: { [questionId]: string } }` -> updated document, work queued |
| POST `/workspaces/{id}/clarification/refresh` | `{ clarificationId }` -> a `clarify` job; regenerate the current question layout with the visual catalog, preserving the old form on failure/cancel |
| POST `/workspaces/{id}/feedback` | `{ text, clientRequestId, artifactId?, elementId?, revision? }` -> updated document, immediately acknowledged queue; stable idempotency key required |
| POST `/workspaces/{id}/jobs/{jobId}/cancel` | No body -> updated document; queued cancel immediate, active abort explicit |
| POST `/workspaces/{id}/jobs/{jobId}/retry` | No body -> updated document; new job linked by `retryOf`, original context retained |
| POST `/workspaces/{id}/agents/{agentId}/reset-session` | No body -> updated document; explicit fresh Copilot conversation on next retry, only while no work is queued/active; old reference retained in event history |
| POST `/workspaces/{id}/artifacts/{artifactId}/review` | `{ revision, decision: "accept" \| "reject" }` -> updated document; stale/conflicted acceptance returns 409 |
| POST `/workspaces/{id}/artifacts/{artifactId}/edit` | `{ revision, elementId, text }` -> updated document; text-only Heading/Text edit, optimistic concurrency, human revision accepted explicitly |
| PUT `/workspaces/{id}/layout` | `{ positions: { [nodeId]: {x,y} }, viewport: {x,y,zoom} }` -> updated document |
| GET `/workspaces/{id}/events?after={sequence}` | SSE event `workspace`, data `WorkspaceEvent`; heartbeat `heartbeat`; browser refetches canonical document after changes and on reconnect |

Every POST/PUT must send `Content-Type: application/json` (including an empty
JSON object for no-body commands) and `X-Workspace-Token: <csrfToken>`.
HTTP request bodies are capped at 256 KiB, including escaped Unicode; individual
field and collection limits below remain stricter.
No permissive CORS. The browser retries bootstrap after a service restart; it
must not replay mutation requests implicitly.

### State vocabulary

- Workspace: `queued`, `working`, `needsInput`, `review`, `idle`, `error`.
- Agent: `idle`, `working`, `waiting`, `complete`, `error`, `cancelled`.
- Job: `queued`, `running`, `cancelling`, `completed`, `failed`, `cancelled`,
  `interrupted`. Kind: `initial`, `answers`, `feedback`, `clarify`.
- Feedback: follows its job, except successful generation is `review` until the
  corresponding proposal is accepted/rejected.
- Artifact revision: `proposed`, `accepted`, `rejected`, `superseded`;
  `conflict` is independent and must be prominent.

An agent's `assigned` boolean identifies the current task team. Legacy used
agents remain assigned, but old idle/waiting specialist placeholders with no
session are read as unassigned when the field is absent. Explicit modern
assignments are never inferred away. Historical unassigned agents retain their
sessions but are not displayed as active canvas participants.
New workspaces have only agent `planner`, displayed as
**Coordinator**. Specialist identities, names and purposes are task-dependent.

The current working revision can be a proposal. `acceptedRevision` identifies
the last human-approved version; never label the current proposal approved.
Feedback `clientRequestId` is an opaque, workspace-scoped idempotency key:
1-128 ASCII characters, starting with a letter or digit, followed by letters,
digits, `.`, `_`, `:`, or `-`. UUIDs and `feedback-<uuid>` are both valid.
Reusing a key with different content or an anchor returns 409; an identical
retry returns the saved result without another job.
Offer a previous/accepted revision view while background work is running.
Selection is `{artifactId, revision, elementId}`. The service resolves quoted
context from that immutable version; arbitrary client-provided quotes are not
trusted as artifact content.

### Browser behavior

Create a stable workspace route (hash or client route); retain real task history.
Do not block feedback because another job is running. Disable only the actual
submission while its enqueue HTTP request is in flight. Keep answer/feedback
drafts keyed to workspace/form/anchor. Do not let a task-switch race replace the
new task's state with an old response. EventSource disposal cancels only the
subscriber, not the job.

Use original prototype neutral Segoe styling, spatial cards, pan/zoom/fit,
readable unscaled feedback, activity ledger, and explicit Accept/Reject/Edit.
The home page must explain real Copilot versus deterministic demo; no fake
history, fake source integrations or implicit switching between modes.

## Strict declarative UI subset

Spec: `{ root: string, elements: { [id]: { type, props, children: string[] } } }`.
No other keys. IDs match `[A-Za-z][A-Za-z0-9_-]{0,63}` excluding
`constructor`, `prototype`, `__proto__`. Every `props.anchorId` equals its map
key. Maximum 96 nodes, depth 8, 24 children per Section, one parent per non-root
node, no cycles/orphans/shared children. Root is Section. Only Section has
children; leaves use `[]`. Maximum serialized spec 192 KiB.

All props below are required; nullable fields must be explicit `null`.
No arbitrary additional props, `on`, `state`, `watch`, expressions, HTML, CSS,
URLs, scripts or navigation. Text is rendered literally and escaped.

| Type | Props in addition to `anchorId` |
| --- | --- |
| Section | `title` (1-180 chars), `description` (null or <=500 chars) |
| Heading | `level` (`h2`, `h3`, `h4`), `text` (1-180 chars) |
| Text | `text` (1-8000 chars) |
| BulletList | `ordered` (boolean), `items` (1-40 `{id,text}`; text 1-2000 chars) |
| DataTable | `caption` (null or <=180 chars), `columns` (1-8 `{id,label}`), `rows` (0-30 `{id,cells:string[]}`); cell count must equal columns, cell <=2000 chars |
| Decision | `question` (1-180 chars), `options` (2-6 `{id,label,reason}`; reason 1-2000 chars), `recommendedId` (null or matching option ID); recommendation is not approval |
| ChoiceQuestion | `label` (1-180 chars), `help` (null or <=500 chars), `required` (boolean), `options` (2-8 `{id,label}`), `value` (null or matching option ID) |
| TextQuestion | `label`, `help`, `required`, `multiline` (boolean), `value` (null or <=4000 chars) |
| SliderQuestion | `label`, `help`, `required`; finite `min`, `max`, `step`; `value` (null or an aligned in-range number); `minLabel`, `maxLabel`, `unit` (null or <=80 chars); `labels` (null or 2-11 nonempty strings, each <=80 chars, one per step including endpoints) |
| MultiChoiceQuestion | `label`, `help`, `required`; `options` (2-8 `{id,label}`); `value` (null or an array of distinct offered option IDs) |
| NumberQuestion | `label`, `help`, `required`; finite `min`/`max` (nullable), positive finite `step`; `value` (nullable finite number), `unit` (null or <=80 chars). Numbers/bounds have magnitude <=1000000000000; defaults/answers must respect bounds and step alignment from `min` or zero |
| ToggleQuestion | `label`, `help`, `required`; `value` (boolean, a visible suggested default). Use for yes/no decisions with a sensible default; not for permissions or approval |
| DateQuestion | `label`, `help`, `required`; `min`, `max`, `value` (each null or a valid ISO `YYYY-MM-DD` date); min <= max and values within supplied bounds |

Slider bounds satisfy `-1000000 <= min < max <= 1000000`, positive `step`,
and 1-10000 evenly spaced steps. Optional `labels` must match the step count
plus one. A mock-style detail slider can use 1-5 with labels from Executive
Brief to Full Playbook. A numeric slider can instead show a unit and endpoint
captions. Values and defaults must be step-aligned.

All nested IDs must be unique within their collection. Option/column labels
are 1-180 chars. Artifacts cannot contain question components. Clarification
documents allow Section/Heading/Text plus 1-8 question components. Submit is
host-owned, not a model-authored action. Form values can live in a controlled
React context; JSON Render still renders the declarative questions. Neither
the SDK nor JSON Render's single active stream hook is the browser job queue.

Answers remain strings for storage/API compatibility: slider answers are
invariant decimal strings; multi-choice answers are JSON-encoded arrays of
option IDs, such as `["product","marketing"]`. Required multi-choice questions
need at least one selection. Unknown/duplicate options, invalid numbers, and
out-of-range/off-step answers fail explicitly. The host seeds visible declared
defaults into the draft; pressing Continue confirms them. Text/choice behavior
is preserved.
Number answers use invariant decimal strings, switches use `true`/`false`,
and dates use ISO date strings. All of these remain task data, never application
permissions or artifact approval.

### Clarification experience is the product differentiator

The model chooses the question's control from the answer's semantics, not a
generic text form: one-of choices use visual cards/chips, sets use checkbox
chips, bounded scales use labelled sliders, open numeric quantities use numeric
inputs, yes/no preferences use switches, deadlines use date pickers, and only
genuinely open-ended information uses text. Do not turn dates, budgets, scope
levels, or known audience categories into text questions.

Ask only decisions needed to produce a useful next artifact. Aim for 2-4
primary decisions, at most one open text field in ordinary clarifications,
short plain-language labels, and a brief reason only when useful. Do not repeat
the full objective inside the form. Avoid presenting every available widget
merely to demonstrate the catalog.
Newly generated forms are quality-checked: at most four required questions
and two open-text questions. This generation gate is separate from structural
validation so previously saved forms remain readable and answerable.

The host presents visible defaults and current slider labels, contextual help,
an optional-refinements disclosure, inline validation, and a concise live
selection summary. Defaults are suggestions confirmed by Continue, not silent
agent decisions. Keyboard operation, readable unscaled inputs, preserved drafts,
and mobile behavior are required. All question controls must still be rendered
through the validated JSON Render registry; no parallel hardcoded form pathway.

Question refresh is an explicit action, never automatic inference on upgrade.
It requires the current form and no queued/active work. While a `clarify` job
is pending, the old form is kept but cannot be submitted/refreshed concurrently.
A cancelled/failed refresh restores its usability; a successful refresh
replaces it with a new form identity. Unsubmitted browser drafts are not deleted.

### Explicit session recovery

A CLI session cancelled or failed before its first accepted prompt can have a
saved ID without resumable conversation history. `copilot_session_unavailable`
is a real failure, not permission to silently replace it. The user can confirm
**Start new session** for that Copilot agent after all queued/active work has
stopped, then explicitly retry the failed job. The command clears only the
current role's session reference; workspace data and the previous reference in
the durable event audit are preserved. It does not delete the old CLI session,
run a model, switch provider, or change the requested model/effort.

## Agent implementation boundary

`IAgentProvider` implements `GetStatusAsync` and `CompleteAsync` for a single
role session. `Name` is `copilot` or `demo`. A session-created/resumed callback
uses `AgentSignal(kind: "session", sessionId: actualId)` so it can be persisted
before later failures. Other signals: `status` or safe `delta`. Never emit
hidden reasoning, credentials, raw CLI logs or unsafe HTML.

The MAF graph implements `IWorkspaceWorkflow.ExecuteAsync`. Its coordinator
returns `{summary, clarification, agents, artifacts}`. `agents` and `artifacts`
may be omitted/null to represent empty lists, but a non-clarifying response
must have direct artifacts or a valid specialist plan; there is no fixed-team
fallback. A clarification result cannot also recruit agents or contain artifacts.
Once answers exist or feedback targets an artifact, do not repeat blocking
questions. An explicit `clarify` job must return a fresh clarification form.

Each `AgentAssignment` has `{id,name,role,kind}`: safe component-style ID,
name 1-80 characters, purpose/role 1-1000 characters, and kind `produce` or
`review`. IDs must be unique and cannot be `planner` (case-insensitive).
Select **zero to four** specialists according to the task; reuse appropriate
existing identities. The coordinator can create artifacts directly with no
subagents. A reviewer is optional, never forced. A review step requires an
existing/direct/upstream draft. Each producer returns the complete candidate
artifact set; later producers see and refine prior candidates. Reviews are
combined with their authors' names. No generated plan can select models, tools,
executable code, remote agents, or another provider.

After validating the plan, the workflow emits exactly one
`AgentSignal("planner", "roster", summary, Assignments: assignments)` before
executing specialists. Only the coordinator may declare a roster. The host
registers that bounded team transactionally, deactivates unused historical
members, and rejects signals from unplanned agents. Saved role sessions are
reused, not silently deleted. At most twelve historical specialist identities
are retained per workspace; exceeding this is an explicit planning error.

Each producer returns `{artifacts:[{artifactId:null-or-existing-id,title,spec}]}`
(1-3 artifacts). Initial output uses null IDs, assigned by the host. Refinement
of a selected artifact returns exactly that existing artifact ID. The reviewer
returns `{summary}`. No stage may accept or overwrite an artifact. With no
review specialist, `WorkflowResult.Review` and the stored revision review are
null; the UI must not invent an AI review.

Validate structured output with `JsonDefaults.Options` and
`SpecValidator.Validate(spec, "artifact" | "clarification")`. This validator
throws `WorkspaceException` with code `invalid_spec`; do not silently repair,
prune or substitute data. Validate all intermediate output before next routing.
Workspace snapshot and original feedback context are in `WorkflowRequest`;
user input is data, not trusted prompt/pack instructions.

The coordinator owns queue state, layout, revisions, decision history, conflict
detection and persistence. Workflow/provider code does not write the store or
start independent application jobs. `AgentProviderException` carries a safe
public code/message. The MAF run must abort on explicit cancellation, not merely
stop its event reader. Browser request cancellation is never the run lifetime.
