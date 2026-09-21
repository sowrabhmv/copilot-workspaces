import { useMemo, useState } from 'react';
import type { ActionResult } from '../api';
import type { Artifact, Selection } from '../domain';
import { blockText, parseSpec } from '../ui-schema';
import { GeneratedDocument, type SelectedBlock } from './GeneratedDocument';
import { Icon } from './Icons';
import { InlineError, Spinner, StatusBadge, timeLabel } from './Common';
import type { ComposerTarget } from './FeedbackComposer';

interface Props {
  artifact: Artifact; selected: Selection | null; selectedRevision: number | undefined;
  onRevision: (revision: number | undefined) => void; onSelect: (selection: Selection) => void;
  onCompose: (target: ComposerTarget) => void;
  onReview: (artifactId: string, revision: number, decision: 'accept' | 'reject') => Promise<ActionResult>;
  onFocus: () => void;
}
export function ArtifactCard({ artifact, selected, selectedRevision, onRevision, onSelect, onCompose, onReview, onFocus }: Props) {
  const revision = artifact.revisions.find((item) => item.revision === (selectedRevision ?? artifact.currentRevision))
    ?? artifact.revisions.at(-1);
  const [pending, setPending] = useState<'accept' | 'reject' | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const readable = useMemo(() => {
    try { parseSpec(revision?.spec, 'artifact'); return true; }
    catch { return false; }
  }, [revision?.spec]);
  if (!revision) return <article className="surface-card"><InlineError message="This artifact has no saved versions." /></article>;
  const selectionOf = (elementId: string): Selection => ({ artifactId: artifact.id, revision: revision.revision, elementId });
  const openFeedback = ({ element, anchor }: SelectedBlock) => onCompose({
    instanceId: crypto.randomUUID(), mode: 'feedback', selection: selectionOf(element.props.anchorId),
    label: `${artifact.title} / v${revision.revision}`, quote: blockText(element), anchor,
  });
  const openEdit = ({ element, anchor }: SelectedBlock) => {
    if (element.type !== 'Heading' && element.type !== 'Text') return;
    onCompose({
      instanceId: crypto.randomUUID(), mode: 'edit', selection: selectionOf(element.props.anchorId),
      label: `${artifact.title} / v${revision.revision}`, quote: '', anchor, text: element.props.text,
      limit: element.type === 'Heading' ? 180 : 8000,
    });
  };
  const review = async (decision: 'accept' | 'reject') => {
    setPending(decision); setFailure(null);
    const result = await onReview(artifact.id, revision.revision, decision);
    setPending(null);
    if (!result.ok) setFailure(result.conflict
      ? `${result.message} No approval was applied. Your saved versions are unchanged.`
      : result.message);
  };
  return <article className={`surface-card artifact-card ${revision.conflict ? 'has-conflict' : ''}`}>
    <header className="card-header" data-drag-handle>
      <span className="card-emblem"><Icon name="document" /></span>
      <div><span className="eyebrow">Shared artifact</span><h2>{artifact.title}</h2></div>
      <StatusBadge status={revision.status} />
      <button className="icon-button" title="Focus this artifact" aria-label={`Focus ${artifact.title}`} onClick={onFocus}><Icon name="fit" /></button>
      <Icon name="drag" className="drag-grip" />
    </header>
    <div className="artifact-version-bar" data-canvas-interactive>
      <div className="version-tabs" role="group" aria-label={`View ${artifact.title}`}>
        <button aria-pressed={revision.revision === artifact.currentRevision} onClick={() => onRevision(undefined)}>Latest</button>
        {artifact.acceptedRevision !== null && <button aria-pressed={revision.revision === artifact.acceptedRevision}
          onClick={() => onRevision(artifact.acceptedRevision ?? undefined)}>Accepted</button>}
      </div>
      <label className="version-select"><Icon name="history" /><span className="sr-only">Version of {artifact.title}</span>
        <select value={revision.revision} onChange={(event) => onRevision(Number(event.target.value))}>
          {[...artifact.revisions].reverse().map((item) => <option key={item.revision} value={item.revision}>
            v{item.revision} - {item.status}{item.source === 'human' ? ' - your edit' : ''}
          </option>)}
        </select>
      </label>
    </div>
    {revision.conflict && <div className="conflict-banner" role="status"><Icon name="shield" /><span>
      <strong>Changes need reconciliation.</strong> This proposal was based on an older version. Review it against your latest edits; it cannot overwrite them.
    </span></div>}
    <div className="artifact-document" data-scroll-region>
      <GeneratedDocument candidate={revision.spec} kind="artifact"
        selectedId={selected?.artifactId === artifact.id && selected.revision === revision.revision ? selected.elementId : undefined}
        onSelect={({ element }) => onSelect(selectionOf(element.props.anchorId))}
        onFeedback={openFeedback} onEdit={openEdit} />
    </div>
    {revision.review?.trim() && <details className="ai-review" data-canvas-interactive>
      <summary><Icon name="review" />AI review<span>Not human approval</span><Icon name="down" /></summary>
      <p>{revision.review}</p>
    </details>}
    <footer className="artifact-footer" data-canvas-interactive>
      <span className="version-meta">v{revision.revision} <span aria-hidden="true">/</span> {revision.source === 'human' ? 'Your edit' : 'Agent proposal'} <span aria-hidden="true">/</span> {timeLabel(revision.createdAt)}</span>
      {revision.status === 'proposed' ? <div className="review-actions">
        <button className="button-secondary" onClick={() => void review('reject')} disabled={pending !== null}>
          {pending === 'reject' ? <Spinner /> : <Icon name="dismiss" />}Reject
        </button>
        <button className="button-primary" onClick={() => void review('accept')}
          disabled={pending !== null || revision.conflict || !readable}
          title={!readable ? 'This document must pass validation before it can be reviewed.' : revision.conflict ? 'Reconcile this conflict before accepting.' : 'Accept this exact saved version'}>
          {pending === 'accept' ? <Spinner /> : <Icon name="check" />}Accept version
        </button>
      </div> : <span className="review-disposition"><Icon name={revision.status === 'accepted' ? 'approved' : 'history'} />
        {revision.status === 'accepted' ? 'Accepted by you' : 'Saved in history'}</span>}
    </footer>
    {failure && <InlineError message={failure} />}
    <p className="artifact-hint">Select a block to give direction or edit its text. Other work keeps moving.</p>
  </article>;
}
