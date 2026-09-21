import { useEffect, useRef, useState } from 'react';
import { api, ApiError, errorMessage, type ActionResult } from '../api';
import { activeJob, type Bootstrap, type Feedback, type FeedbackRequest, type Selection, type Workspace } from '../domain';
import { useCompact, useStoredDraft, type Connection } from '../hooks';
import { SpatialCanvas, type CanvasHandle, type CanvasItem } from './SpatialCanvas';
import { ContextCard } from './ContextCard';
import { ArtifactCard } from './ArtifactCard';
import { ActivityPanel, AgentCard } from './ActivityPanel';
import { FeedbackComposer, DraftSchema, newFeedbackDraft, type ComposerTarget } from './FeedbackComposer';
import { Icon } from './Icons';
import { InlineError, Spinner, StatusBadge } from './Common';

function WorkspaceDirection({ workspaceId, submit }: {
  workspaceId: string; submit: (request: FeedbackRequest) => Promise<ActionResult>;
}) {
  const draft = useStoredDraft(`workspaces:v1:${workspaceId}:direction`, DraftSchema, newFeedbackDraft);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const latest = useRef(draft.value);
  latest.current = draft.value;
  return <div className="workspace-direction" data-canvas-interactive>
    {(error || draft.storageError) && <InlineError message={error ?? draft.storageError ?? ''} />}
    <form className="workspace-composer" onSubmit={async (event) => {
      event.preventDefault();
      if (pending || !draft.value.text.trim()) return;
      const submitted = { ...draft.value };
      setPending(true); setError(null);
      const result = await submit(submitted);
      setPending(false);
      if (!result.ok) { setError(result.message); return; }
      setAcknowledged(true);
      if (latest.current.clientRequestId === submitted.clientRequestId) draft.setValue(newFeedbackDraft());
    }}>
      <Icon name="sparkle" />
      <label className="sr-only" htmlFor="workspace-direction">Direction for the whole workspace</label>
      <input id="workspace-direction" maxLength={4000} placeholder="Steer the workspace, or select a block for focused feedback"
        value={draft.value.text} onChange={(event) => { draft.setValue(newFeedbackDraft(event.target.value)); setAcknowledged(false); }} />
      <button className="composer-send" aria-label="Send workspace direction" disabled={pending || !draft.value.text.trim()}>
        {pending ? <Spinner /> : <Icon name="send" />}
      </button>
    </form>
    {acknowledged && <span className="composer-acknowledgment" role="status">Direction saved to the activity queue.</span>}
  </div>;
}

interface Props {
  workspace: Workspace; bootstrap: Bootstrap | null; connection: Connection; error: string | null;
  onDocument: (workspace: Workspace) => void; onRefresh: () => void; onReconnect: () => void;
  onMenu: () => void; onHome: () => void;
}
export function WorkspaceScreen({ workspace, bootstrap, connection, error, onDocument, onRefresh, onReconnect, onMenu, onHome }: Props) {
  const compact = useCompact();
  const [focused, setFocused] = useState(compact);
  const [activityOpen, setActivityOpen] = useState(false);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [target, setTarget] = useState<ComposerTarget | null>(null);
  const [operationError, setOperationError] = useState<string | null>(null);
  const [revisionViews, setRevisionViews] = useState<Record<string, number | undefined>>({});
  const canvas = useRef<CanvasHandle>(null);
  const screen = useRef<HTMLElement>(null);
  useEffect(() => { if (compact) { setFocused(true); setActivityOpen(false); } }, [compact]);
  const running = workspace.jobs.filter((job) => job.status === 'running' || job.status === 'cancelling').length;
  const queued = workspace.jobs.filter((job) => job.status === 'queued').length;
  const active = workspace.jobs.filter(activeJob).length;
  const assignedAgents = workspace.agents.filter((agent) => agent.assigned);
  const coordinator = assignedAgents.find((agent) => agent.id === 'planner');
  const artifactStartY = 64 + Math.ceil(assignedAgents.length / 3) * 340;
  const latestJob = workspace.jobs.reduce<(typeof workspace.jobs)[number] | null>(
    (latest, job) => !latest || job.order > latest.order ? job : latest, null);
  const mutate = async (operation: () => Promise<Workspace>): Promise<ActionResult> => {
    try { onDocument(await operation()); return { ok: true }; }
    catch (failure) {
      const conflict = failure instanceof ApiError && failure.status === 409;
      setOperationError(errorMessage(failure));
      if (conflict) onRefresh();
      return { ok: false, message: errorMessage(failure), conflict };
    }
  };
  const feedback = (request: FeedbackRequest) => mutate(() => api.feedback(workspace.id, request));
  const focusArtifact = (id: string) => {
    if (focused) document.querySelector<HTMLElement>(`[data-node-id="${CSS.escape(`artifact-${id}`)}"]`)?.scrollIntoView({ block: 'start' });
    else canvas.current?.fit([`artifact-${id}`]);
  };
  const latestArtifact = (id: string) => {
    setRevisionViews((previous) => ({ ...previous, [id]: undefined }));
    setTarget(null); setSelection(null); onRefresh(); focusArtifact(id);
  };
  const goToAnchor = (item: Feedback) => {
    if (!item.artifactId || !item.elementId || item.revision === null) return;
    setRevisionViews((previous) => ({ ...previous, [item.artifactId!]: item.revision ?? undefined }));
    setSelection({ artifactId: item.artifactId, elementId: item.elementId, revision: item.revision });
    focusArtifact(item.artifactId);
    if (compact) setActivityOpen(false);
  };
  const focusQuestions = () => {
    setFocused(true);
    requestAnimationFrame(() => {
      const context = screen.current?.querySelector<HTMLElement>('[data-node-id="context"]');
      const questions = context?.querySelector<HTMLElement>('.clarification-form');
      (questions ?? context)?.scrollIntoView?.({ block: 'start' });
      context?.querySelector<HTMLInputElement>('.generated-question input, .generated-question textarea')?.focus({ preventScroll: true });
    });
  };
  const items: CanvasItem[] = [
    { id: 'context', label: 'Objective and context', kind: 'context', position: { x: 24, y: 24 }, width: 408,
      content: <ContextCard workspace={workspace} onAnswers={(id, answers) => mutate(() => api.answers(workspace.id, id, answers))}
        onRegenerate={(id) => mutate(() => api.regenerateClarification(workspace.id, id))}
        onFocusQuestions={focusQuestions} /> },
    ...assignedAgents.map((agent, index): CanvasItem => ({
      id: `agent-${agent.id}`, label: agent.name, kind: 'agent', position: { x: 504 + (index % 3) * 320, y: 24 + Math.floor(index / 3) * 340 }, width: 280,
      content: <AgentCard agent={agent} provider={workspace.provider} workPending={active > 0}
        sessionUnavailable={latestJob?.errorCode === 'copilot_session_unavailable'
          && (agent.status === 'error' || agent.status === 'cancelled' || agent.activeJobId === latestJob.id)}
        onResetSession={() => mutate(() => api.resetAgentSession(workspace.id, agent.id))}
        onOpenActivity={() => setActivityOpen(true)} />,
    })),
    ...workspace.artifacts.map((artifact, index): CanvasItem => ({
      id: `artifact-${artifact.id}`, label: artifact.title, kind: 'artifact',
      position: { x: 504 + (index % 2) * 872, y: artifactStartY + Math.floor(index / 2) * 1000 }, width: 824,
      content: <ArtifactCard artifact={artifact} selected={selection} selectedRevision={revisionViews[artifact.id]}
        onRevision={(revision) => {
          setRevisionViews((previous) => ({ ...previous, [artifact.id]: revision }));
          if (selection?.artifactId === artifact.id) setSelection(null);
        }}
        onSelect={setSelection} onCompose={(nextTarget) => {
          if (nextTarget.selection) {
            const selected = nextTarget.selection;
            setRevisionViews((previous) => ({ ...previous, [selected.artifactId]: selected.revision }));
            setSelection(selected);
          }
          setTarget(nextTarget);
        }}
        onReview={(id, revision, decision) => mutate(() => api.review(workspace.id, id, revision, decision))}
        onFocus={() => focusArtifact(artifact.id)} />,
    })),
  ];
  const connections: Array<[string, string]> = coordinator
    ? [['context', `agent-${coordinator.id}`], ...assignedAgents.filter((agent) => agent !== coordinator)
      .map((agent): [string, string] => [`agent-${coordinator.id}`, `agent-${agent.id}`])]
    : assignedAgents.map((agent): [string, string] => ['context', `agent-${agent.id}`]);

  return <main ref={screen} className="workspace-shell">
    <header className="workspace-header">
      <button className="mobile-menu icon-button" aria-label="Open task navigation" onClick={onMenu}><Icon name="tasks" /></button>
      <button className="icon-button back-home" aria-label="Back to new task" onClick={onHome}><Icon name="context" /></button>
      <div className="workspace-identity"><div><span>Workspace</span><span aria-hidden="true">/</span><strong>{workspace.title}</strong>
        {workspace.provider === 'demo' && <span className="demo-badge">Demo</span>}</div>
        <p>{workspace.provider === 'demo' ? 'Deterministic demonstration / no live model'
          : bootstrap ? 'Auto / Copilot chooses the model / human-led' : 'Auto / Copilot chooses the model'}</p>
      </div>
      <div className="global-activity" role="status" aria-live="polite">
        {running > 0 && <Spinner />}
        <span>{running ? `${running} working${queued ? ` / ${queued} queued` : ''}` : queued ? `${queued} queued`
          : workspace.status === 'needsInput' ? 'Your input is needed'
            : workspace.status === 'error' ? 'The work needs attention'
              : workspace.status === 'review' ? 'Proposals await your review' : 'Ready for your direction'}</span>
      </div>
      <button className="button-secondary activity-toggle" aria-label="Activity" aria-expanded={activityOpen} onClick={() => setActivityOpen(!activityOpen)}>
        <Icon name="tasks" /><span>Activity</span>{active > 0 && <span className="count-badge">{active}</span>}
      </button>
    </header>
    {(connection !== 'connected' || error) && <div className={`connection-banner ${connection === 'offline' || error ? 'is-offline' : ''}`} role="status">
      {connection === 'connecting' || connection === 'reconnecting' ? <Spinner /> : <Icon name="shield" />}
      <span>{error ?? (connection === 'connecting' ? 'Connecting to saved workspace...' : 'Progress connection interrupted. Your jobs continue in the background; reconnecting...')}</span>
      <button className="text-button" onClick={onReconnect}>Reconnect</button>
    </div>}
    {operationError && <div className="operation-error" role="alert">
      <span>{operationError}</span><button className="icon-button" aria-label="Dismiss operation error"
        onClick={() => setOperationError(null)}><Icon name="dismiss" /></button>
    </div>}
    <div className="workspace-body">
      <div className="canvas-region">
        <nav className="canvas-navigation" aria-label="Workspace areas">
          <button onClick={() => focused
            ? document.querySelector<HTMLElement>('[data-node-id="context"]')?.scrollIntoView({ block: 'start' })
            : canvas.current?.fit(['context'])}><Icon name="context" />Context</button>
          <button disabled={assignedAgents.length === 0} onClick={() => focused
            ? document.querySelector<HTMLElement>('.node-agent')?.scrollIntoView({ block: 'start' })
            : canvas.current?.fit(assignedAgents.map((agent) => `agent-${agent.id}`))}><Icon name="agent" />Agents <span>{assignedAgents.length}</span></button>
          {workspace.artifacts.length > 0 && <button onClick={() => focusArtifact(workspace.artifacts[0].id)}>
            <Icon name="document" />Artifacts <span>{workspace.artifacts.length}</span>
          </button>}
          <button className="focus-toggle" aria-pressed={focused} onClick={() => setFocused(!focused)}><Icon name={focused ? 'fit' : 'document'} />{focused ? 'Canvas view' : 'Focus view'}</button>
        </nav>
        <SpatialCanvas ref={canvas} items={items} connections={connections} focused={focused}
          selectedNode={selection ? `artifact-${selection.artifactId}` : undefined}
          initialLayout={workspace.layout}
          onSave={async (layout) => {
            const result = await mutate(() => api.layout(workspace.id, layout));
            if (!result.ok) throw new Error(result.message);
          }} />
        <WorkspaceDirection workspaceId={workspace.id} submit={feedback} />
        {!workspace.artifacts.length && workspace.status !== 'needsInput' && <div className="canvas-empty-hint" aria-live="polite">
          <Icon name="document" /><span>{workspace.status === 'error'
            ? 'The work needs attention. Open Activity for details and retry.'
            : 'Artifacts will appear here as the work takes shape.'}</span>
          <StatusBadge status={workspace.status} />
        </div>}
      </div>
      {activityOpen && <ActivityPanel workspace={workspace} onClose={() => setActivityOpen(false)}
        onAnchor={goToAnchor} onJob={(id, action) => mutate(() => api.job(workspace.id, id, action))} />}
    </div>
    {target && <FeedbackComposer key={`${workspace.id}:${target.instanceId}`} workspaceId={workspace.id} target={target}
      onClose={() => setTarget((current) => current?.instanceId === target.instanceId ? null : current)}
      onFeedback={feedback}
      onEdit={async (selected, text) => {
        const result = await mutate(() => api.edit(workspace.id, selected.artifactId, selected.revision, selected.elementId, text));
        if (result.ok) {
          setRevisionViews((previous) => previous[selected.artifactId] === selected.revision
            ? { ...previous, [selected.artifactId]: undefined } : previous);
          setSelection((current) => current?.artifactId === selected.artifactId
            && current.revision === selected.revision && current.elementId === selected.elementId ? null : current);
        }
        return result;
      }}
      onLatest={latestArtifact} />}
  </main>;
}
