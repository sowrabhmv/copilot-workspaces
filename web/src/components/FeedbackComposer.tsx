import { useEffect, useLayoutEffect, useRef, useState, type FormEvent } from 'react';
import { createPortal } from 'react-dom';
import { z } from 'zod';
import type { ActionResult } from '../api';
import type { FeedbackRequest, Selection } from '../domain';
import { useStoredDraft } from '../hooks';
import { Icon } from './Icons';
import { InlineError, Spinner } from './Common';

interface TargetBase { instanceId: string; label: string; quote: string; anchor: HTMLElement }
export type ComposerTarget = TargetBase & (
  | { mode: 'feedback'; selection?: Selection }
  | { mode: 'edit'; selection: Selection; text: string; limit: number }
);
export const DraftSchema = z.strictObject({ text: z.string(), clientRequestId: z.string().min(1) });
export function newFeedbackDraft(text = '') { return { text, clientRequestId: `feedback-${crypto.randomUUID()}` }; }
export function composerKey(workspaceId: string, target: ComposerTarget) {
  const selection = target.selection;
  return `workspaces:v1:${workspaceId}:${target.mode}:${selection
    ? `${selection.artifactId}:${selection.revision}:${selection.elementId}` : 'workspace'}`;
}
interface Props {
  workspaceId: string; target: ComposerTarget; onClose: () => void;
  onFeedback: (request: FeedbackRequest) => Promise<ActionResult>;
  onEdit: (selection: Selection, text: string) => Promise<ActionResult>;
  onLatest: (artifactId: string) => void;
}

export function FeedbackComposer({ workspaceId, target, onClose, onFeedback, onEdit, onLatest }: Props) {
  const panel = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  const form = useRef<HTMLFormElement>(null);
  const [position, setPosition] = useState({ left: 16, top: 96 });
  const [pending, setPending] = useState(false);
  const [failure, setFailure] = useState<{ message: string; conflict: boolean } | null>(null);
  const draft = useStoredDraft(composerKey(workspaceId, target), DraftSchema,
    () => newFeedbackDraft(target.mode === 'edit' ? target.text : ''));
  const latest = useRef(draft.value);
  latest.current = draft.value;
  const mounted = useRef(true);
  const limit = target.mode === 'edit' ? target.limit : 4000;
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  useLayoutEffect(() => {
    const reposition = () => {
      const rect = target.anchor.getBoundingClientRect();
      const width = Math.min(408, window.innerWidth - 24);
      const height = panel.current?.offsetHeight ?? 400;
      let left = rect.right + 12;
      if (left + width > window.innerWidth - 12) left = rect.left - width - 12;
      left = Math.max(12, Math.min(left, window.innerWidth - width - 12));
      const top = Math.max(12, Math.min(rect.top, window.innerHeight - height - 12));
      setPosition((previous) => previous.left === left && previous.top === top ? previous : { left, top });
    };
    reposition();
    const observer = new ResizeObserver(reposition);
    if (panel.current) observer.observe(panel.current);
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, true);
    window.addEventListener('pointermove', reposition);
    input.current?.focus({ preventScroll: true });
    return () => {
      observer.disconnect();
      window.removeEventListener('resize', reposition);
      window.removeEventListener('scroll', reposition, true);
      window.removeEventListener('pointermove', reposition);
    };
  }, [target.instanceId, target.anchor]);

  const close = () => {
    onClose();
    if (target.anchor.isConnected) target.anchor.focus({ preventScroll: true });
  };
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (pending || !draft.value.text.trim()) return;
    const submitted = { ...draft.value };
    setPending(true);
    setFailure(null);
    const result = target.mode === 'edit'
      ? await onEdit(target.selection, submitted.text)
      : await onFeedback({
        text: submitted.text, clientRequestId: submitted.clientRequestId, ...target.selection,
      });
    if (!mounted.current) {
      if (result.ok) {
        try {
          const key = composerKey(workspaceId, target);
          if (localStorage.getItem(key) === JSON.stringify(submitted)) localStorage.removeItem(key);
        } catch (error) {
          console.warn('An acknowledged draft could not be cleared from browser storage. Its idempotency key is retained.', error);
        }
      }
      return;
    }
    setPending(false);
    if (!result.ok) { setFailure(result); return; }
    if (latest.current.clientRequestId === submitted.clientRequestId && latest.current.text === submitted.text) {
      draft.setValue(newFeedbackDraft());
      close();
    }
  };

  return createPortal(<div ref={panel} className="feedback-popover" style={position}
    role="dialog" aria-modal="false" aria-label={target.mode === 'edit' ? `Edit ${target.label}` : `Give direction for ${target.label}`}
    data-canvas-interactive
    onKeyDown={(event) => {
      if (event.key === 'Escape') { event.stopPropagation(); event.preventDefault(); close(); }
      if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
        event.preventDefault(); form.current?.requestSubmit();
      }
    }}>
    <header><Icon name={target.mode === 'edit' ? 'document' : 'sparkle'} />
      <div><strong>{target.mode === 'edit' ? 'Edit this text' : 'Give direction'}</strong><span>{target.label}</span></div>
      <button className="icon-button" aria-label="Close feedback" onClick={close}><Icon name="dismiss" /></button>
    </header>
    <form ref={form} onSubmit={(event) => void submit(event)}>
      {target.mode === 'feedback' && target.quote && <blockquote>{target.quote.slice(0, 500)}</blockquote>}
      <label className="sr-only" htmlFor="anchor-direction">{target.mode === 'edit' ? 'Your revised text' : 'Your direction'}</label>
      <textarea ref={input} id="anchor-direction" maxLength={limit} rows={target.mode === 'edit' ? 8 : 4}
        placeholder={target.mode === 'edit' ? 'Write your version...' : 'What would you like to change or explore?'}
        value={draft.value.text}
        onChange={(event) => {
          draft.setValue(newFeedbackDraft(event.target.value));
          setFailure(null);
        }} />
      {target.mode === 'feedback' && !draft.value.text && <div className="feedback-suggestions">
        {['Make this more concise', 'Explore a different approach', 'Make the trade-offs explicit'].map((suggestion) =>
          <button type="button" key={suggestion} onClick={() => {
            draft.setValue(newFeedbackDraft(suggestion)); input.current?.focus();
          }}>{suggestion}<Icon name="right" /></button>)}
      </div>}
      {draft.storageError && <InlineError message={draft.storageError} />}
      {failure && <InlineError message={failure.conflict
        ? `${failure.message} Your draft is kept. Open the latest version to reconcile it.`
        : failure.message} />}
      {failure?.conflict && target.selection && <button type="button" className="text-button"
        onClick={() => onLatest(target.selection!.artifactId)}>Open latest version</button>}
      <footer>
        <span>{target.mode === 'edit' ? 'Creates a new human-authored version.' : 'Queued independently. Keep working elsewhere.'}<small>Ctrl+Enter to {target.mode === 'edit' ? 'save' : 'send'}</small></span>
        <button className="button-primary" type="submit" disabled={pending || !draft.value.text.trim()}>
          {pending ? <Spinner /> : <Icon name={target.mode === 'edit' ? 'check' : 'send'} />}
          {pending ? 'Saving...' : target.mode === 'edit' ? 'Save version' : 'Send direction'}
        </button>
      </footer>
    </form>
  </div>, document.body);
}
