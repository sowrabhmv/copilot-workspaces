import { Children, createContext, useContext, useEffect, useMemo, useState, type ReactNode, type MouseEvent } from 'react';
import { defineRegistry, JSONUIProvider, Renderer } from '@json-render/react';
import {
  blockText, catalog, defaultAnswer, formatQuestionAnswer, parseSpec, questionError,
  questionsOf, questionsUnder, selectedOptions, sliderIntervals, sliderValueAt,
  type QuestionElement, type SpecKind, type WireElement, type WireSpec,
} from '../ui-schema';
import { errorMessage } from '../api';
import { Icon } from './Icons';
import { InlineError } from './Common';

export interface SelectedBlock { element: WireElement; anchor: HTMLElement }
interface Interaction {
  spec: WireSpec;
  kind: SpecKind;
  primaryQuestions: ReadonlySet<string>;
  selectedId?: string;
  onSelect?: (block: SelectedBlock) => void;
  onFeedback?: (block: SelectedBlock) => void;
  onEdit?: (block: SelectedBlock) => void;
  answers?: Record<string, string>;
  errors?: Record<string, string>;
  onAnswer?: (id: string, value: string) => void;
  formId?: string;
}
const HostContext = createContext<Interaction | null>(null);
function useHost() {
  const value = useContext(HostContext);
  if (!value) throw new Error('Generated components require a host context.');
  return value;
}
function Block({ id, children }: { id: string; children: ReactNode }) {
  const context = useHost();
  const node = context.spec.elements[id];
  const selected = context.selectedId === id;
  if (!context.onSelect) return <div data-element-id={id}>{children}</div>;
  const select = (anchor: HTMLElement) => context.onSelect?.({ element: node, anchor });
  const act = (event: MouseEvent<HTMLButtonElement>, action: Interaction['onFeedback']) => {
    event.stopPropagation();
    const anchor = event.currentTarget.closest<HTMLElement>('[data-element-id]');
    if (anchor) action?.({ element: node, anchor });
  };
  return <div className={`artifact-block ${selected ? 'is-selected' : ''}`}
    data-element-id={id} tabIndex={0}
    aria-label={`${node.type} block: ${blockText(node).slice(0, 100)}`}
    onClick={(event) => { event.stopPropagation(); select(event.currentTarget); }}
    onKeyDown={(event) => {
      if (event.target === event.currentTarget && (event.key === 'Enter' || event.key === ' ')) {
        event.preventDefault(); event.stopPropagation(); select(event.currentTarget);
      }
    }}>
    {children}
    {selected && <div className="block-actions" data-canvas-interactive>
      <button onClick={(event) => act(event, context.onFeedback)}><Icon name="sparkle" />Give direction</button>
      {(node.type === 'Text' || node.type === 'Heading') && context.onEdit
        && <button onClick={(event) => act(event, context.onEdit)}><Icon name="document" />Edit text</button>}
    </div>}
  </div>;
}
function OptionalRefinements({ children, questionIds }: { children: ReactNode; questionIds: string[] }) {
  const context = useHost();
  const [open, setOpen] = useState(false);
  const hasErrors = questionIds.some((id) => !!context.errors?.[id]);
  useEffect(() => { if (hasErrors) setOpen(true); }, [hasErrors]);
  return <details className="optional-refinements" open={open}
    onToggle={(event) => setOpen(event.currentTarget.open)} data-canvas-interactive>
    <summary><span>Optional refinements</span><span className="optional-count">{questionIds.length}</span><Icon name="down" /></summary>
    <p className="field-help">Fine-tune these only if they matter for this task.</p>
    {children}
  </details>;
}
function SectionContent({ anchorId, title, description, children }: {
  anchorId: string; title: string; description: string | null; children?: ReactNode;
}) {
  const context = useHost();
  const rendered = Children.toArray(children);
  const primary: ReactNode[] = [];
  const optional: ReactNode[] = [];
  const optionalIds: string[] = [];
  context.spec.elements[anchorId].children.forEach((id, index) => {
    const questions = context.kind === 'clarification' ? questionsUnder(context.spec, id) : [];
    if (questions.length && questions.every((question) => !context.primaryQuestions.has(question.props.anchorId))) {
      optional.push(rendered[index]);
      optionalIds.push(...questions.map((question) => question.props.anchorId));
    } else primary.push(rendered[index]);
  });
  return <section className="generated-section" data-element-id={anchorId}>
    <h2>{title}</h2>{description && <p className="section-description">{description}</p>}
    {primary}
    {optional.length > 0 && <OptionalRefinements questionIds={optionalIds}>{optional}</OptionalRefinements>}
  </section>;
}

function QuestionControl({ element }: { element: QuestionElement }) {
  const context = useHost();
  const props = element.props;
  const id = `${context.formId ?? 'questions'}-${props.anchorId}`;
  const suggested = defaultAnswer(element);
  const hasSuggestion = suggested !== null && suggested !== '';
  const value = context.answers?.[props.anchorId] ?? suggested ?? '';
  const error = context.errors?.[props.anchorId] ?? (value ? questionError(element, value, false) : null);
  const description = [props.help ? `${id}-help` : null, hasSuggestion ? `${id}-suggestion` : null, error ? `${id}-error` : null]
    .filter(Boolean).join(' ') || undefined;
  const field = {
    id, 'aria-labelledby': `${id}-label`, 'aria-describedby': description,
    'aria-invalid': error ? true : undefined,
  };
  const answer = (next: string) => context.onAnswer?.(props.anchorId, next);
  let control: ReactNode;
  switch (element.type) {
    case 'ChoiceQuestion':
      control = <div className="choice-options">{element.props.options.map((option) =>
        <label key={option.id} className={value === option.id ? 'is-checked' : ''}>
          <input type="radio" name={id} value={option.id} checked={value === option.id}
            required={props.required} aria-describedby={description} onChange={() => answer(option.id)} />
          <span>{option.label}</span>
        </label>)}</div>;
      break;
    case 'MultiChoiceQuestion': {
      const selected = selectedOptions(value) ?? [];
      control = <div className="choice-options checkbox-chips">{element.props.options.map((option) =>
        <label key={option.id} className={selected.includes(option.id) ? 'is-checked' : ''}>
          <input type="checkbox" value={option.id} checked={selected.includes(option.id)}
            aria-describedby={description} onChange={(event) => {
              const next = new Set(selected);
              if (event.target.checked) next.add(option.id); else next.delete(option.id);
              answer(JSON.stringify(element.props.options.filter((item) => next.has(item.id)).map((item) => item.id)));
            }} />
          <span>{option.label}</span>
        </label>)}</div>;
      break;
    }
    case 'SliderQuestion': {
      const slider = element.props;
      const intervals = sliderIntervals(slider);
      if (intervals === null) throw new Error('A slider must be validated before rendering.');
      const selected = value !== '' && !questionError(element, value, false);
      const current = selected ? Number(value) : slider.min;
      const percent = Math.max(0, Math.min(100, (current - slider.min) / (slider.max - slider.min) * 100));
      const label = selected ? formatQuestionAnswer(element, value) : 'Choose a value';
      control = <div className="slider-question">
        <output className="slider-current" htmlFor={id}>{label}</output>
        <input {...field} type="range" min={slider.min} max={slider.max} step={slider.step}
          value={current} aria-valuetext={selected ? label : 'Not selected. Use the arrow keys or move the slider.'}
          style={{ backgroundImage: `linear-gradient(to right, var(--accent) 0%, var(--accent) ${percent}%, var(--border) ${percent}%, var(--border) 100%)` }}
          onChange={(event) => answer(event.target.value)}
          onKeyDown={(event) => {
            if (event.ctrlKey || event.altKey || event.metaKey) return;
            const index = Math.round((current - slider.min) / slider.step);
            const page = Math.max(1, Math.round(intervals / 10));
            const positions: Record<string, number> = {
              ArrowRight: index + 1, ArrowUp: index + 1, ArrowLeft: index - 1, ArrowDown: index - 1,
              Home: 0, End: intervals, PageUp: index + page, PageDown: index - page,
            };
            if (!Object.hasOwn(positions, event.key)) return;
            const next = positions[event.key];
            event.preventDefault();
            answer(sliderValueAt(slider, Math.max(0, Math.min(intervals, next))));
          }} />
        {slider.labels && <div className="slider-stops" aria-hidden="true">
          {slider.labels.map((label, index) => <span key={index} title={label}
            className={selected && index === Math.round((current - slider.min) / slider.step) ? 'is-current' : ''} />)}
        </div>}
        <div className="range-captions" aria-hidden="true">
          <span>{slider.minLabel ?? slider.labels?.[0] ?? `${slider.min}${slider.unit ? ` ${slider.unit}` : ''}`}</span>
          <span>{slider.maxLabel ?? slider.labels?.at(-1) ?? `${slider.max}${slider.unit ? ` ${slider.unit}` : ''}`}</span>
        </div>
      </div>;
      break;
    }
    case 'NumberQuestion':
      control = <div className="quantity-input">
        <input {...field} type="number" inputMode="decimal" required={props.required} min={element.props.min ?? undefined}
          max={element.props.max ?? undefined} step={element.props.step} value={value}
          onChange={(event) => answer(event.target.value)} />
        {element.props.unit && <span>{element.props.unit}</span>}
      </div>;
      break;
    case 'ToggleQuestion':
      control = <label className="toggle-preference">
        <input {...field} type="checkbox" role="switch" checked={value === 'true'}
          onChange={(event) => answer(String(event.target.checked))} />
        <span className="toggle-track" aria-hidden="true"><span /></span>
        <span className="toggle-value">{value === 'true' ? 'Yes' : value === 'false' ? 'No' : 'Choose Yes or No'}</span>
      </label>;
      break;
    case 'DateQuestion':
      control = <input {...field} className="question-input date-input" type="date" required={props.required}
        min={element.props.min ?? '0001-01-01'} max={element.props.max ?? '9999-12-31'} value={value}
        onChange={(event) => answer(event.target.value)} />;
      break;
    case 'TextQuestion':
      control = element.props.multiline
        ? <textarea {...field} className="question-input" rows={3} maxLength={4000} required={props.required} value={value} onChange={(event) => answer(event.target.value)} />
        : <input {...field} className="question-input" type="text" maxLength={4000} required={props.required} value={value} onChange={(event) => answer(event.target.value)} />;
      break;
  }
  return <fieldset className={`generated-question question-${element.type}`} data-canvas-interactive
    data-question-id={props.anchorId} aria-describedby={description}>
    <legend><span id={`${id}-label`}>{props.label}</span>
      {props.required && <span className="required-label">Required</span>}
    </legend>
    {props.help && <p className="field-help" id={`${id}-help`}>{props.help}</p>}
    {control}
    {hasSuggestion && <p className="question-default" id={`${id}-suggestion`}>
      {suggested === value ? 'Suggested default' : `Suggested: ${formatQuestionAnswer(element, suggested)}`}
    </p>}
    {error && <p className="field-error" id={`${id}-error`}>{error}</p>}
  </fieldset>;
}
const { registry } = defineRegistry(catalog, {
  components: {
    Section: ({ props, children }) => <SectionContent {...props}>{children}</SectionContent>,
    Heading: ({ props }) => {
      const Heading = props.level;
      return <Block id={props.anchorId}><Heading>{props.text}</Heading></Block>;
    },
    Text: ({ props }) => <Block id={props.anchorId}><p className="literal-text">{props.text}</p></Block>,
    BulletList: ({ props }) => {
      const List = props.ordered ? 'ol' : 'ul';
      return <Block id={props.anchorId}><List>{props.items.map((item) => <li key={item.id}>{item.text}</li>)}</List></Block>;
    },
    DataTable: ({ props }) => <Block id={props.anchorId}><div className="table-scroll" data-scroll-region>
      <table>{props.caption && <caption>{props.caption}</caption>}
        <thead><tr>{props.columns.map((column) => <th scope="col" key={column.id}>{column.label}</th>)}</tr></thead>
        <tbody>{props.rows.map((row) => <tr key={row.id}>
          {props.columns.map((column, index) => <td key={column.id}>{row.cells[index]}</td>)}
        </tr>)}</tbody>
      </table>
    </div></Block>,
    Decision: ({ props }) => <Block id={props.anchorId}>
      <div className="decision-block"><h3>{props.question}</h3>
        <p className="eyebrow">Compare the alternatives</p>
        {props.options.map((option) => <div className="decision-option" key={option.id}>
          <strong>{option.label}</strong>
          {option.id === props.recommendedId && <span className="recommendation">AI recommendation</span>}
          <p>{option.reason}</p>
        </div>)}
        <p className="field-help">A recommendation is not approval. You decide which version to accept.</p>
      </div>
    </Block>,
    ChoiceQuestion: ({ props }) => <QuestionControl element={{ type: 'ChoiceQuestion', props, children: [] }} />,
    TextQuestion: ({ props }) => <QuestionControl element={{ type: 'TextQuestion', props, children: [] }} />,
    SliderQuestion: ({ props }) => <QuestionControl element={{ type: 'SliderQuestion', props, children: [] }} />,
    MultiChoiceQuestion: ({ props }) => <QuestionControl element={{ type: 'MultiChoiceQuestion', props, children: [] }} />,
    NumberQuestion: ({ props }) => <QuestionControl element={{ type: 'NumberQuestion', props, children: [] }} />,
    ToggleQuestion: ({ props }) => <QuestionControl element={{ type: 'ToggleQuestion', props, children: [] }} />,
    DateQuestion: ({ props }) => <QuestionControl element={{ type: 'DateQuestion', props, children: [] }} />,
  },
});

interface GeneratedDocumentProps extends Omit<Interaction, 'spec' | 'kind' | 'primaryQuestions'> {
  candidate: unknown;
  kind: SpecKind;
}
export function GeneratedDocument({ candidate, kind, ...interaction }: GeneratedDocumentProps) {
  const checked = useMemo(() => {
    try { return { spec: parseSpec(candidate, kind), error: null }; }
    catch (error) { return { spec: null, error: errorMessage(error) }; }
  }, [candidate, kind]);
  if (!checked.spec) return <InlineError message={`This document cannot be displayed safely. ${checked.error}`} />;
  const questions = questionsOf(checked.spec);
  const required = questions.filter((question) => question.props.required);
  const primaryQuestions = new Set((required.length ? required : questions.slice(0, 3)).map((question) => question.props.anchorId));
  return <HostContext.Provider value={{ ...interaction, spec: checked.spec, kind, primaryQuestions }}>
    <JSONUIProvider registry={registry}>
      <Renderer spec={checked.spec} registry={registry} />
    </JSONUIProvider>
  </HostContext.Provider>;
}
