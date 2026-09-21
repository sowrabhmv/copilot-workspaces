# Workspaces browser

React 19.3, TypeScript, Vite, and genuine JSON Render 0.21.0 components for the
local workspace service. Run commands from this directory.

```powershell
npm ci --no-audit --no-fund
npm run dev
npm test
npm run build
```

Development binds **127.0.0.1:5174** with `strictPort` and proxies `/api` to
**127.0.0.1:5080**. It never uses the reference prototype's occupied port 5173.
The production output is `dist`. No model SDK, account credential, arbitrary
agent tool, or remote model request runs in the browser.

## Contract and trust boundary

`..\docs\api-contract.md` and the server domain records define the HTTP wire
contract. `src\api.ts` parses responses and sends the bootstrap anti-forgery
token on every mutation. A changed token is refreshed without replaying the
mutation. Only explicit user action retries a write.

`src\ui-schema.ts` owns strict per-component validation: literal props, stable
IDs, 96 nodes, depth 8, 192 KiB, single-parent reachability, collection identity,
and form/table shape. It rejects model-authored state, actions, expressions,
HTML components, arbitrary DOM props, and unknown fields. `catalog.validate`
is not used as an authorization boundary. The actual JSONUIProvider/Renderer
uses the bounded catalog in `GeneratedDocument.tsx`; question values live in
host React context and Continue is a trusted host control.

## Visual clarification, not a generic text form

The validated catalog renders seven question types through the genuine
JSON Render registry:

| Decision | Control | Stored answer |
| --- | --- | --- |
| One option | `ChoiceQuestion` visual radio choices | Offered option ID |
| A set of options | `MultiChoiceQuestion` checkbox chips | JSON-string array of distinct option IDs |
| A bounded scale | `SliderQuestion`, current label, step markers and endpoint captions | Invariant decimal string |
| A numeric quantity | `NumberQuestion`, native numeric input and unit | Invariant decimal string |
| A yes/no preference | `ToggleQuestion`, native switch semantics | `true` or `false` |
| A deadline | `DateQuestion`, native date picker and bounds | `YYYY-MM-DD` |
| Genuinely open information | `TextQuestion` | Plain text |

Only controls present in the generated specification appear. The browser does
not insert a showcase of every widget or choose controls from task keywords.
The reference's detail slider and stakeholder chips are supported directly by
the catalog, rather than a parallel hardcoded form.

Declared defaults seed new drafts; valid saved human answers take precedence.
Defaults are marked as suggestions, and **Your selections** provides a concise,
live summary before Continue confirms them. Optional refinements are disclosed
progressively; small all-optional forms still show their primary preferences.
Dates, numeric bounds, exact decimal steps, offered/unique choices, and required
answers are validated explicitly. Decimal serialization never depends on the
browser locale. Toggles are task preferences, never permissions or approval.

**Focus questions** switches to the existing full-size, unscaled focus view
without replacing the draft; mobile starts in that view. Controls use native
keyboard/date/numeric semantics and do not trigger canvas gestures. Sliders
also support Home, End, arrow keys, and Page Up/Down with meaningful value text.

**Regenerate questions** explicitly posts the current `clarificationId` to
`/api/workspaces/{id}/clarification/refresh`. It never runs automatically on
upgrade. It is disabled while any work is queued/active. A pending `clarify`
job retains the old form and draft but disables Continue and further refreshes.
Failure, cancellation, or interruption restores the old form's usability.
A successful refresh uses a new form identity; earlier browser draft keys are
not deleted. New-generation cognitive limits are enforced by the backend;
structurally valid older dense forms remain readable and answerable.

## Auto model policy and task-dependent teams

The UI displays **Auto** and **Copilot chooses the model**, with no manual
model picker and no raw `auto/null` or pinned model/effort labels. Bootstrap and
provider responses accept nullable `reasoningEffort`; routing stays server-side.

Only agents with `assigned: true` are rendered, counted, and connected. Missing
legacy `assigned` fields default to true. A new task's single `planner` identity
can be displayed as **Coordinator** with zero to four task-specific specialists.
There are no writer/reviewer placeholders or forced three-agent layout. The
canvas derives participant rows and artifact spacing from the current roster,
and connections show coordinator recruitment rather than an invented fixed
pipeline. Historical unassigned agents, sessions, and saved node positions
remain in canonical data. A direct artifact with null/empty AI review shows no
review section, but always retains the separate human approval controls.

Task routes use `#/workspaces/<id>`. Canonical state and layouts are persisted
by the service. Answer, feedback, and edit drafts are locally stored by task,
form, artifact revision, and element anchor. A direction's idempotency key is
retained across uncertain network failures. SSE only prompts canonical
refreshes; disconnecting a subscriber never cancels a job. Old task responses
cannot replace a newly selected task.

Selection/feedback does not reset canvas pan/zoom. Popovers are portalled
outside the transformed canvas and clamped to the viewport. History and accepted
versions remain available during agent work. Acceptance, rejection, and direct
text edits target an explicit saved revision; a 409 preserves the human draft.
Mobile defaults to a readable focused view. Reduced motion disables animation.

Saved Copilot agent cards also offer **Session options > Start new session**.
The confirmed action posts `{}` to
`/api/workspaces/{id}/agents/{agentId}/reset-session` with the normal token.
It is unavailable for demo agents, missing session references, or any queued,
running, or cancelling workspace job. The dialog explains that artifacts,
answers, feedback, saved context, and the audit reference remain, but the next
run uses a fresh conversation. It clears only the selected saved reference;
the user must choose **Retry** in Activity separately. No session is deleted
or executed by this action. A changed session or newly pending job blocks an
already-open confirmation, and server failures remain visible.

## Dependency source note

The configured machine npm mirror only exposed JSON Render 0.20.0 on
2026-09-20, although official release 0.21.0 and its published UNPKG artifacts
were available. Direct registry connections were unavailable. To retain the
requested version without modifying the renderer, `vendor` contains exact
published 0.21.0 files repacked with `npm pack --ignore-scripts`.

Each file was checked against UNPKG's SHA-256 integrity metadata. Package
identity, file integrity, source URLs, archive hashes, and Apache-2.0 license
are recorded in `vendor\provenance.json`; licenses are included in the archives.
The lockfile resolves these local versioned tarballs, so normal `npm ci` does
not depend on that stale mirror entry. Zod 4.3.6 is the compatible exact
upstream-tested baseline. `scripts\vendor-json-render.ps1` reproduces sourcing;
it does not execute downloaded package scripts.

## Design and assets

The supplied `..\..\Prototype` and its linked Relay/One Copilot Figma screens
were reviewed read-only. This implementation adapts their neutral Segoe
surfaces, objective/agent/artifact canvas, and anchored direction patterns; it
does not copy the prototype's timers, fake task history, or sample integrations.
The demo starter is always explicitly labelled and invokes the service's
`demo` provider, never a hidden frontend simulation.

Fluent 16px/20px Regular icons are actual package exports. Copilot artwork is
the exact supplied rainbow SVG at its native optical dimensions, with hashes
and Figma source nodes in `public\assets\provenance.json`. Product marks are not
recolored. No proprietary fonts are redistributed or fetched.

Focused tests cover hostile/oversized/cyclic specs, provider/CSRF/error
contracts, stable anchors, controlled forms, concurrent feedback, retry
idempotency, newer draft/focus preservation, accepted/conflicted versions,
canvas geometry, and SSE/task-switch lifetime behavior. Visual-control tests
also cover suggested defaults, live summaries, optional disclosure, all seven
answer encodings, slider keyboard behavior, regeneration recovery, legacy
forms, dynamic roster sizes, historical sessions, nullable reviews, and Auto
labels. Backend integration
and complete browser E2E are coordinated from the repository root.
