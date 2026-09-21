import { useMemo, useRef, useState } from 'react';
import { z } from 'zod';
import { errorMessage, type ActionResult } from '../api';
import { activeJob, type Workspace } from '../domain';
import { useStoredDraft } from '../hooks';
import { encodeAnswers, formatQuestionAnswer, parseSpec, questionsOf, seedAnswers, validateAnswers, type WireSpec } from '../ui-schema';
import { GeneratedDocument } from './GeneratedDocument';
import { Icon } from './Icons';
import { InlineError, Spinner, StatusBadge } from './Common';

interface Props {
  workspace: Workspace;
  onAnswers: (clarificationId: string, answers: Record<string, string>) => Promise<ActionResult>;
  onRegenerate: (clarificationId: string) => Promise<ActionResult>;
  onFocusQuestions?: () => void;
}
const Answers = z.record(z.string(), z.string().max(4000));

function ClarificationForm({ workspace, spec, onAnswers, pending, blocked }: {
  workspace: Workspace; spec: WireSpec; pending: boolean; blocked: boolean;
  onAnswers: Props['onAnswers'];
}) {
  const form = useRef<HTMLFormElement>(null);
  const formId = `clarify-${workspace.clarificationId}`;
  const seed = useMemo(() => seedAnswers(spec, workspace.answers), [spec, workspace.answers]);
  const draft = useStoredDraft(`workspaces:v1:${workspace.id}:${formId}`, Answers, () => seed);
  const answers = { ...seed, ...draft.value };
  const questions = questionsOf(spec);
  const primaryCount = questions.some((question) => question.props.required)
    ? questions.filter((question) => question.props.required).length : Math.min(3, questions.length);
  const optionalCount = questions.length - primaryCount;
  const [errors, setErrors] = useState<Record<string, string>>({});
  return <form ref={form} className="clarification-form" data-canvas-interactive noValidate
    aria-label="Clarification questions"
    onSubmit={async (event) => {
      event.preventDefault();
      if (pending || blocked || !workspace.clarificationId) return;
      const issues = validateAnswers(spec, answers);
      setErrors(issues);
      if (Object.keys(issues).length) {
        requestAnimationFrame(() => {
          const invalid = form.current?.querySelector('.field-error');
          const disclosure = invalid?.closest('details');
          if (disclosure) disclosure.open = true;
          invalid?.closest('fieldset')?.querySelector<HTMLInputElement>('input,textarea')?.focus();
        });
        return;
      }
      await onAnswers(workspace.clarificationId, encodeAnswers(spec, answers));
    }}>
    <p className="clarification-intro">{primaryCount} {primaryCount === 1 ? 'decision' : 'decisions'} to shape the next step.
      {optionalCount > 0 && ` ${optionalCount} optional refinement${optionalCount === 1 ? '' : 's'}.`}</p>
    <GeneratedDocument candidate={spec} kind="clarification" formId={formId}
      answers={answers} errors={errors}
      onAnswer={(id, value) => {
        draft.setValue((previous) => ({ ...previous, [id]: value }));
        setErrors((previous) => { const next = { ...previous }; delete next[id]; return next; });
      }} />
    <section className="selection-summary" aria-labelledby={`${formId}-summary`}>
      <h3 id={`${formId}-summary`}>Your selections</h3>
      <dl aria-live="polite" aria-atomic="false">
        {questions.map((question) => <div key={question.props.anchorId}>
          <dt>{question.props.label}</dt>
          <dd>{formatQuestionAnswer(question, answers[question.props.anchorId] ?? '')}</dd>
        </div>)}
      </dl>
      <p>Suggested defaults are confirmed only when you continue.</p>
    </section>
    {draft.storageError && <InlineError message={draft.storageError} />}
    <div className="clarification-footer">
      <span className="field-help">{blocked ? 'Wait for the new questions. Your current draft is kept.' : 'You can keep steering as the work takes shape.'}</span>
      <button className="button-primary" disabled={pending || blocked} type="submit">
        {pending ? <Spinner /> : <Icon name="arrow" />}{pending ? 'Saving answers...' : 'Continue'}
      </button>
    </div>
  </form>;
}

export function ContextCard({ workspace, onAnswers, onRegenerate, onFocusQuestions }: Props) {
  const [request, setRequest] = useState<'answers' | 'refresh' | null>(null);
  const [requestError, setRequestError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const refreshing = workspace.jobs.some((job) => job.kind === 'clarify' && activeJob(job));
  const workPending = workspace.jobs.some(activeJob);
  const hasForm = workspace.clarification != null && workspace.clarificationId !== null;
  const latestJob = workspace.jobs.reduce<(typeof workspace.jobs)[number] | null>(
    (latest, job) => !latest || job.order > latest.order ? job : latest, null);
  const run = async (kind: 'answers' | 'refresh', action: () => Promise<ActionResult>): Promise<ActionResult> => {
    if (inFlight.current || refreshing || (kind === 'refresh' && workPending)) {
      const message = 'Wait for pending work before updating these questions.';
      setRequestError(message);
      return { ok: false, message, conflict: false };
    }
    inFlight.current = true;
    setRequest(kind); setRequestError(null);
    try {
      const result = await action();
      if (!result.ok) setRequestError(result.message);
      return result;
    } catch (error) {
      const message = errorMessage(error);
      setRequestError(message);
      return { ok: false, message, conflict: false };
    } finally {
      inFlight.current = false;
      setRequest(null);
    }
  };
  const clarification = useMemo(() => {
    if (workspace.clarification == null || !workspace.clarificationId) return { spec: null, error: null };
    try { return { spec: parseSpec(workspace.clarification, 'clarification'), error: null }; }
    catch (error) { return { spec: null, error: errorMessage(error) }; }
  }, [workspace.clarification, workspace.clarificationId]);
  return <article className={`surface-card context-card ${workspace.status === 'needsInput' ? 'needs-input' : ''}`}>
    <header className="card-header" data-drag-handle>
      <span className="card-emblem"><Icon name="context" /></span>
      <div><span className="eyebrow">Start here</span><h2>Objective &amp; context</h2></div>
      <Icon name="drag" className="drag-grip" />
    </header>
    <div className="context-body" data-scroll-region>
      {hasForm ? <details className="objective-recap" data-canvas-interactive>
        <summary><span>Your objective</span><Icon name="down" /></summary>
        <p className="objective-text">{workspace.objective}</p>
      </details> : <><p className="eyebrow">Your objective</p><p className="objective-text">{workspace.objective}</p></>}
      {workspace.provider === 'demo' && <div className="demo-note"><Icon name="shield" />
        <span>Demo workspace. These agents use deterministic sample output, not a live model.</span>
      </div>}
      {workspace.status === 'needsInput' && <div className="attention-heading"><StatusBadge status="needsInput" /></div>}
      {hasForm && <div className="question-tools" data-canvas-interactive>
        {onFocusQuestions && clarification.spec && <button type="button" className="text-button" onClick={onFocusQuestions}>
          <Icon name="fit" />Focus questions
        </button>}
        <button type="button" className="text-button" disabled={workPending || request !== null}
          title={workPending ? 'Wait for queued and active work to finish.' : 'Generate a fresh question layout without deleting this draft.'}
          onClick={() => {
            const id = workspace.clarificationId;
            if (id) void run('refresh', () => onRegenerate(id));
          }}><Icon name="retry" />{request === 'refresh' ? 'Queueing refresh...' : 'Regenerate questions'}</button>
      </div>}
      {hasForm && (refreshing || request === 'refresh') && <p className="clarification-progress" role="status">
        Updating questions in the background. Your previous form and draft stay here until a new version is ready.
      </p>}
      {hasForm && latestJob?.kind === 'clarify' && ['failed', 'cancelled', 'interrupted'].includes(latestJob.status)
        && !refreshing && <p className="clarification-recovered" role="status">
          Question refresh {latestJob.status}. Your previous form and draft are still available.
        </p>}
      {requestError && <InlineError message={requestError} />}
      {clarification.spec && <ClarificationForm key={workspace.clarificationId} workspace={workspace}
        spec={clarification.spec} pending={request === 'answers'} blocked={refreshing || request === 'refresh'}
        onAnswers={(id, answers) => run('answers', () => onAnswers(id, answers))} />}
      {clarification.error && <InlineError message={`The clarification could not be rendered safely. ${clarification.error}`} />}
      {!clarification.spec && !clarification.error && workspace.summary && <div className="context-summary">
        <p className="eyebrow">Coordinator summary</p><p>{workspace.summary}</p>
      </div>}
      {!clarification.spec && Object.keys(workspace.answers).length > 0 && <p className="context-confirmed"><Icon name="check" />Your clarification answers are saved.</p>}
      <div className="human-control"><Icon name="shield" /><span>You stay in control. Agents propose; you review and decide.</span></div>
    </div>
  </article>;
}
