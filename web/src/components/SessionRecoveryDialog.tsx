import { useId, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { errorMessage, type ActionResult } from '../api';
import { InlineError, Spinner } from './Common';
import { Icon } from './Icons';

interface Props {
  agentName: string;
  workPending: boolean;
  sessionChanged: boolean;
  onReset: () => Promise<ActionResult>;
  onComplete: () => void;
  onClose: () => void;
  returnFocus: () => HTMLElement | null;
}

export function SessionRecoveryDialog({
  agentName, workPending, sessionChanged, onReset, onComplete, onClose, returnFocus,
}: Props) {
  const titleId = useId();
  const descriptionId = useId();
  const dialog = useRef<HTMLDivElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  const returnFocusRef = useRef(returnFocus);
  returnFocusRef.current = returnFocus;
  const inFlight = useRef(false);
  const [pending, setPending] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const guard = sessionChanged
    ? 'The saved session changed while this dialog was open. Close this dialog and review the current session before continuing.'
    : workPending
      ? 'Work is queued, running, or cancelling. Wait for all pending work in this workspace to finish before changing a session.'
      : null;

  useLayoutEffect(() => {
    const root = document.getElementById('root');
    const previousInert = root?.inert ?? false;
    const previousHidden = root?.getAttribute('aria-hidden') ?? null;
    cancel.current?.focus({ preventScroll: true });
    if (root) { root.inert = true; root.setAttribute('aria-hidden', 'true'); }
    return () => {
      if (root) {
        root.inert = previousInert;
        if (previousHidden === null) root.removeAttribute('aria-hidden');
        else root.setAttribute('aria-hidden', previousHidden);
      }
      const element = returnFocusRef.current();
      if (element?.isConnected) element.focus({ preventScroll: true });
    };
  }, []);

  const confirm = async () => {
    if (inFlight.current || workPending || sessionChanged) return;
    inFlight.current = true;
    setPending(true);
    setFailure(null);
    try {
      const result = await onReset();
      if (result.ok) onComplete();
      else setFailure(result.message);
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      inFlight.current = false;
      setPending(false);
    }
  };

  return createPortal(<div className="session-dialog-backdrop" data-canvas-interactive>
    <div ref={dialog} className="session-dialog" role="dialog" aria-modal="true"
      aria-labelledby={titleId} aria-describedby={descriptionId} aria-busy={pending || undefined} tabIndex={-1}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          event.preventDefault(); event.stopPropagation();
          if (!inFlight.current) onClose();
        }
        if (event.key === 'Tab') {
          event.stopPropagation();
          const controls = dialog.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)');
          const first = controls?.[0];
          const last = controls?.[controls.length - 1];
          if (!first || !last) {
            event.preventDefault(); dialog.current?.focus();
          } else if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog.current)) {
            event.preventDefault(); last.focus();
          } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === dialog.current)) {
            event.preventDefault(); first.focus();
          }
        }
      }}>
      <header><span className="session-dialog-emblem"><Icon name="history" /></span>
        <div><span className="eyebrow">Copilot conversation recovery</span><h2 id={titleId}>Start a new {agentName} session?</h2></div>
      </header>
      <div className="session-dialog-content" id={descriptionId}>
        <p>This clears only {agentName}&apos;s saved Copilot conversation reference. The old session is not deleted.</p>
        <div className="session-preserved"><Icon name="shield" /><p>
          <strong>Your workspace stays intact.</strong> Your task, artifacts, accepted versions, answers, feedback, and saved context are preserved.
          The old session reference remains in the event audit.
        </p></div>
        <p>The next run will start a fresh conversation for this agent instead of resuming its earlier conversation history.</p>
        <p><strong>Then choose Retry in Activity.</strong> Clearing the reference does not run the agent or start any work automatically.</p>
      </div>
      {guard && <div className="session-guard" role="status">{guard}</div>}
      {failure && <InlineError message={failure} />}
      <footer>
        <button ref={cancel} className="button-secondary" onClick={onClose} disabled={pending}>Cancel</button>
        <button className="button-primary" onClick={() => void confirm()} disabled={pending || !!guard}>
          {pending ? <Spinner /> : <Icon name="retry" />}{pending ? 'Clearing reference...' : 'Start new session'}
        </button>
      </footer>
    </div>
  </div>, document.body);
}
