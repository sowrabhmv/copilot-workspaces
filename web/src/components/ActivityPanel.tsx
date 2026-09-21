import { useEffect, useRef, useState } from 'react';
import type { ActionResult } from '../api';
import { activeJob, type Agent, type Feedback, type Provider, type Workspace } from '../domain';
import { Icon } from './Icons';
import { InlineError, Spinner, StatusBadge, timeLabel } from './Common';
import { SessionRecoveryDialog } from './SessionRecoveryDialog';

interface AgentCardProps {
  agent: Agent; provider: Provider; workPending: boolean; sessionUnavailable: boolean;
  onResetSession: () => Promise<ActionResult>; onOpenActivity: () => void;
}
export function AgentCard({ agent, provider, workPending, sessionUnavailable, onResetSession, onOpenActivity }: AgentCardProps) {
  const savedSession = provider === 'copilot' && agent.sessionId !== null;
  const recoverySuggested = sessionUnavailable || agent.status === 'error' || agent.status === 'cancelled';
  const [expanded, setExpanded] = useState(recoverySuggested);
  const [confirmSessionId, setConfirmSessionId] = useState<string | null>(null);
  const [resetComplete, setResetComplete] = useState(false);
  const card = useRef<HTMLElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (recoverySuggested) setExpanded(true); }, [recoverySuggested]);
  return <article ref={card} tabIndex={-1} aria-label={`${agent.name} agent`}
    className={`surface-card agent-card agent-${agent.status} ${savedSession ? 'has-saved-session' : ''}`}>
    <header className="card-header" data-drag-handle>
      <span className="card-emblem"><Icon name="agent" /></span>
      <div><span className="eyebrow">{agent.id === 'planner' ? 'Task coordinator' : 'Specialist'}</span><h2>{agent.name}</h2></div>
      <Icon name="drag" className="drag-grip" />
    </header>
    <div className="agent-body">
      <details className="agent-purpose" data-canvas-interactive>
        <summary>Role in this task<Icon name="down" /></summary><p>{agent.role}</p>
      </details>
      <StatusBadge status={agent.status} />
      <p data-scroll-region>{agent.message}</p>
      <span className="agent-policy"><Icon name="shield" />Proposes, never approves</span>
      {savedSession && <details className={`agent-session-options ${recoverySuggested ? 'needs-recovery' : ''}`}
        open={expanded} onToggle={(event) => setExpanded(event.currentTarget.open)} data-canvas-interactive>
        <summary aria-label={`Session options for ${agent.name}`}><Icon name="history" />Session options
          {recoverySuggested && <span>Recovery</span>}<Icon name="down" />
        </summary>
        <p>{sessionUnavailable
          ? 'A saved Copilot conversation could not be resumed. For the affected agent, start a new session, then choose Retry in Activity.'
          : recoverySuggested
            ? 'If the run failed or was cancelled before its first prompt, this saved conversation may not resume. If Retry cannot continue, start a new session.'
            : 'This agent has a saved Copilot conversation reference. You can choose a fresh conversation without removing workspace content.'}</p>
        <button ref={trigger} className="button-secondary agent-session-reset" aria-label={`Start a new ${agent.name} session`}
          disabled={workPending} onClick={() => {
            if (!workPending && agent.sessionId !== null) setConfirmSessionId(agent.sessionId);
          }}><Icon name="retry" />Start new session</button>
        {workPending && <p className="session-wait">Available after all queued, running, and cancelling work finishes.</p>}
      </details>}
      {provider === 'copilot' && resetComplete && agent.sessionId === null && <div className="session-reset-result">
        <p role="status">Saved session reference cleared. Choose Retry in Activity to start a fresh conversation.</p>
        <button className="text-button" onClick={onOpenActivity}><Icon name="tasks" />Open Activity to retry</button>
      </div>}
    </div>
    {provider === 'copilot' && confirmSessionId !== null && <SessionRecoveryDialog
      agentName={agent.name} workPending={workPending} sessionChanged={agent.sessionId !== confirmSessionId}
      onReset={onResetSession} onComplete={() => { setResetComplete(true); setConfirmSessionId(null); }}
      onClose={() => setConfirmSessionId(null)}
      returnFocus={() => trigger.current && !trigger.current.disabled ? trigger.current : card.current} />}
  </article>;
}

interface Props {
  workspace: Workspace; onClose: () => void; onAnchor: (feedback: Feedback) => void;
  onJob: (jobId: string, action: 'cancel' | 'retry') => Promise<ActionResult>;
}
export function ActivityPanel({ workspace, onClose, onAnchor, onJob }: Props) {
  const [pending, setPending] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const jobs = [...workspace.jobs].sort((a, b) => b.order - a.order);
  const active = jobs.filter(activeJob).length;
  const execute = async (jobId: string, action: 'cancel' | 'retry') => {
    setPending((previous) => new Set(previous).add(jobId)); setError(null);
    const result = await onJob(jobId, action);
    setPending((previous) => { const next = new Set(previous); next.delete(jobId); return next; });
    if (!result.ok) setError(result.message);
  };
  return <aside className="activity-panel" aria-label="Workspace activity">
    <header><div><span className="eyebrow">In the background</span><h2>Activity <span>{active}</span></h2></div>
      <button className="icon-button" aria-label="Close activity" onClick={onClose}><Icon name="dismiss" /></button>
    </header>
    <p className="activity-intro">Each direction has its own place in the queue. You can keep reviewing and giving feedback.</p>
    {error && <InlineError message={error} />}
    <ol className="activity-list">
      {jobs.map((job) => {
        const feedback = workspace.feedback.find((item) => item.id === job.feedbackId);
        const feedbackStatus = feedback?.jobId === job.id ? feedback.status : job.status;
        const retryActive = jobs.some((candidate) => candidate.retryOf === job.id && activeJob(candidate));
        return <li className={`activity-item activity-${job.status}`} key={job.id}>
          <div className="activity-row"><StatusBadge status={feedbackStatus} /><time dateTime={job.createdAt}>{timeLabel(job.createdAt)}</time></div>
          <h3>{feedback ? feedback.targetLabel : job.kind === 'initial' ? 'Understand the objective'
            : job.kind === 'clarify' ? 'Regenerate clarifying questions' : 'Continue with your answers'}</h3>
          {feedback && <p className="feedback-instruction">{feedback.text}</p>}
          <p className="job-message">{job.message}</p>
          {feedback?.quotedText && <details><summary>Original selected context</summary><blockquote>{feedback.quotedText}</blockquote></details>}
          {job.errorCode && <p className="job-error-code">{job.errorCode}</p>}
          {workspace.provider === 'copilot' && job.errorCode === 'copilot_session_unavailable' && <p className="session-recovery-guidance">
            Copilot could not resume a saved conversation for this attempt. If you have not already cleared that reference,
            open the affected agent&apos;s <strong>Session options</strong> and choose <strong>Start new session</strong>.
            Then Retry here. Your workspace content stays saved.
          </p>}
          {job.retryOf && <p className="retry-note">Retry of an earlier run. Original context retained.</p>}
          <div className="job-actions">
            {feedback?.artifactId && <button className="text-button" onClick={() => onAnchor(feedback)}><Icon name="document" />View anchor</button>}
            {['queued', 'running'].includes(job.status) && <button className="text-button" disabled={pending.has(job.id)}
              onClick={() => void execute(job.id, 'cancel')}>{pending.has(job.id) ? <Spinner /> : <Icon name="stop" />}Cancel</button>}
            {['failed', 'cancelled', 'interrupted'].includes(job.status) && <button className="text-button" disabled={pending.has(job.id) || retryActive}
              onClick={() => void execute(job.id, 'retry')}>{pending.has(job.id) ? <Spinner /> : <Icon name="retry" />}{retryActive ? 'Retry in progress' : 'Retry'}</button>}
          </div>
        </li>;
      })}
      {!jobs.length && <li className="empty-activity">No work has been queued yet.</li>}
    </ol>
    {workspace.decisions.length > 0 && <details className="decision-history"><summary><Icon name="history" />Your review history ({workspace.decisions.length})</summary>
      <ol>{[...workspace.decisions].reverse().map((decision) => <li key={decision.id}>
        <strong>{decision.kind === 'accept' ? 'Accepted' : decision.kind === 'reject' ? 'Rejected' : decision.kind}</strong>
        <span>{workspace.artifacts.find((artifact) => artifact.id === decision.artifactId)?.title ?? 'Artifact'} / v{decision.revision}</span>
        <time>{timeLabel(decision.createdAt)}</time>
      </li>)}</ol>
    </details>}
  </aside>;
}
