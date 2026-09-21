import { defineCatalog, validateSpec } from '@json-render/core';
import { schema } from '@json-render/react/schema';
import { z } from 'zod';

export const ElementId = z.string().regex(/^(?!constructor$|prototype$)[A-Za-z][A-Za-z0-9_-]{0,63}$/);
const Short = z.string().min(1).max(180);
const Option = z.strictObject({ id: ElementId, label: Short });
const Question = {
  anchorId: ElementId, label: Short, help: z.string().max(500).nullable(), required: z.boolean(),
};
const Caption = z.string().max(80).nullable();
const Quantity = z.number().finite().min(-1_000_000_000_000).max(1_000_000_000_000);
export function isIsoDate(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const [year, month, day] = value.split('-').map(Number);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= days[month - 1];
}
const IsoDate = z.string().refine(isIsoDate, 'Use a valid YYYY-MM-DD date.');
export const componentProps = {
  Section: z.strictObject({ anchorId: ElementId, title: Short, description: z.string().max(500).nullable() }),
  Heading: z.strictObject({ anchorId: ElementId, level: z.enum(['h2', 'h3', 'h4']), text: Short }),
  Text: z.strictObject({ anchorId: ElementId, text: z.string().min(1).max(8000) }),
  BulletList: z.strictObject({
    anchorId: ElementId, ordered: z.boolean(),
    items: z.array(z.strictObject({ id: ElementId, text: z.string().min(1).max(2000) })).min(1).max(40),
  }),
  DataTable: z.strictObject({
    anchorId: ElementId, caption: z.string().max(180).nullable(), columns: z.array(Option).min(1).max(8),
    rows: z.array(z.strictObject({
      id: ElementId, cells: z.array(z.string().max(2000)).min(1).max(8),
    })).max(30),
  }),
  Decision: z.strictObject({
    anchorId: ElementId, question: Short,
    options: z.array(Option.extend({ reason: z.string().min(1).max(2000) })).min(2).max(6),
    recommendedId: ElementId.nullable(),
  }),
  ChoiceQuestion: z.strictObject({
    ...Question, options: z.array(Option).min(2).max(8), value: ElementId.nullable(),
  }),
  TextQuestion: z.strictObject({
    ...Question, multiline: z.boolean(), value: z.string().max(4000).nullable(),
  }),
  SliderQuestion: z.strictObject({
    ...Question,
    min: z.number().finite().min(-1_000_000).max(1_000_000),
    max: z.number().finite().min(-1_000_000).max(1_000_000),
    step: z.number().finite().positive(), value: z.number().finite().nullable(),
    minLabel: Caption, maxLabel: Caption, unit: Caption,
    labels: z.array(z.string().min(1).max(80).refine((label) => label.trim().length > 0))
      .min(2).max(11).nullable(),
  }),
  MultiChoiceQuestion: z.strictObject({
    ...Question, options: z.array(Option).min(2).max(8),
    value: z.array(ElementId).max(8).nullable(),
  }),
  NumberQuestion: z.strictObject({
    ...Question, min: Quantity.nullable(), max: Quantity.nullable(),
    step: z.number().finite().positive(), value: Quantity.nullable(), unit: Caption,
  }),
  ToggleQuestion: z.strictObject({ ...Question, value: z.boolean() }),
  DateQuestion: z.strictObject({
    ...Question, min: IsoDate.nullable(), max: IsoDate.nullable(), value: IsoDate.nullable(),
  }),
};

function element<const T extends string, P extends z.ZodType>(type: T, props: P, container = false) {
  return z.strictObject({ type: z.literal(type), props, children: z.array(ElementId).max(container ? 24 : 0) });
}
export const WireElementSchema = z.discriminatedUnion('type', [
  element('Section', componentProps.Section, true),
  element('Heading', componentProps.Heading),
  element('Text', componentProps.Text),
  element('BulletList', componentProps.BulletList),
  element('DataTable', componentProps.DataTable),
  element('Decision', componentProps.Decision),
  element('ChoiceQuestion', componentProps.ChoiceQuestion),
  element('TextQuestion', componentProps.TextQuestion),
  element('SliderQuestion', componentProps.SliderQuestion),
  element('MultiChoiceQuestion', componentProps.MultiChoiceQuestion),
  element('NumberQuestion', componentProps.NumberQuestion),
  element('ToggleQuestion', componentProps.ToggleQuestion),
  element('DateQuestion', componentProps.DateQuestion),
]);
export const WireSpecSchema = z.strictObject({
  root: ElementId, elements: z.record(ElementId, WireElementSchema),
});
export type WireSpec = z.infer<typeof WireSpecSchema>;
export type WireElement = z.infer<typeof WireElementSchema>;
export const QUESTION_TYPES = [
  'ChoiceQuestion', 'TextQuestion', 'SliderQuestion', 'MultiChoiceQuestion',
  'NumberQuestion', 'ToggleQuestion', 'DateQuestion',
] as const;
export type QuestionElement = Extract<WireElement, { type: (typeof QUESTION_TYPES)[number] }>;
export type SpecKind = 'artifact' | 'clarification';
export function isQuestion(node: WireElement): node is QuestionElement {
  return QUESTION_TYPES.some((type) => type === node.type);
}

interface Decimal { coefficient: bigint; scale: number }
function decimal(value: string | number): Decimal | null {
  const text = String(value);
  if (text.length > 4000 || !Number.isFinite(Number(text))) return null;
  const match = /^([+-]?)(\d+(?:\.\d*)?|\.\d+)(?:[eE]([+-]?\d+))?$/.exec(text);
  if (!match) return null;
  const exponent = Number(match[3] ?? 0);
  if (!Number.isInteger(exponent) || Math.abs(exponent) > 324) return null;
  const [whole, fraction = ''] = match[2].split('.');
  let coefficient = BigInt(`${whole || '0'}${fraction}`) * (match[1] === '-' ? -1n : 1n);
  if (coefficient === 0n) return { coefficient: 0n, scale: 0 };
  let scale = fraction.length - exponent;
  if (scale < 0) { coefficient *= 10n ** BigInt(-scale); scale = 0; }
  while (scale > 0 && coefficient % 10n === 0n) { coefficient /= 10n; scale--; }
  return { coefficient, scale };
}
function decimalText(value: Decimal): string {
  const negative = value.coefficient < 0n;
  const digits = (negative ? -value.coefficient : value.coefficient).toString().padStart(value.scale + 1, '0');
  return `${negative ? '-' : ''}${value.scale
    ? `${digits.slice(0, -value.scale)}.${digits.slice(-value.scale)}` : digits}`;
}
export function decimalAnswer(value: string | number): string | null {
  const parsed = decimal(value);
  return parsed ? decimalText(parsed) : null;
}
function scaled(values: Array<string | number>): bigint[] | null {
  const parts: Decimal[] = [];
  for (const value of values) {
    const part = decimal(value);
    if (!part) return null;
    parts.push(part);
  }
  const scale = Math.max(...parts.map((part) => part.scale));
  return parts.map((part) => part.coefficient * 10n ** BigInt(scale - part.scale));
}
function aligned(value: string | number, origin: number, step: number): boolean {
  const values = scaled([value, origin, step]);
  return !!values && values[2] > 0n && (values[0] - values[1]) % values[2] === 0n;
}
function numericCompare(left: string | number, right: string | number): number {
  const values = scaled([left, right]);
  if (!values) throw new InvalidSpecError('Invalid numeric comparison.');
  return values[0] < values[1] ? -1 : values[0] > values[1] ? 1 : 0;
}
export function sliderIntervals(props: z.infer<typeof componentProps.SliderQuestion>): number | null {
  const values = scaled([props.max, props.min, props.step]);
  if (!values || values[2] <= 0n || (values[0] - values[1]) % values[2] !== 0n) return null;
  return Number((values[0] - values[1]) / values[2]);
}
export function sliderValueAt(props: z.infer<typeof componentProps.SliderQuestion>, index: number): string {
  const min = decimal(props.min);
  const step = decimal(props.step);
  if (!min || !step || !Number.isInteger(index)) throw new InvalidSpecError('Invalid slider step.');
  const scale = Math.max(min.scale, step.scale);
  return decimalText({
    coefficient: min.coefficient * 10n ** BigInt(scale - min.scale)
      + BigInt(index) * step.coefficient * 10n ** BigInt(scale - step.scale),
    scale,
  });
}

export class InvalidSpecError extends Error {
  constructor(message: string) { super(message); this.name = 'InvalidSpecError'; }
}
function fail(message: string): never { throw new InvalidSpecError(message); }
function unique(ids: string[], label: string) {
  if (new Set(ids).size !== ids.length) fail(`${label} contains duplicate IDs.`);
}

export function parseSpec(candidate: unknown, kind: SpecKind): WireSpec {
  let serialized: string | undefined;
  try { serialized = JSON.stringify(candidate); }
  catch { fail('The generated document is not a serializable JSON tree.'); }
  if (!serialized || new TextEncoder().encode(serialized).byteLength > 192 * 1024) {
    fail('The generated document exceeds the 192 KiB limit.');
  }
  const parsed = WireSpecSchema.safeParse(candidate);
  if (!parsed.success) fail(`Invalid generated document: ${parsed.error.issues[0]?.message ?? 'unknown shape'}`);
  const spec = parsed.data;
  const entries = Object.entries(spec.elements);
  if (entries.length < 1 || entries.length > 96) fail('A document must have between 1 and 96 blocks.');
  if (spec.elements[spec.root]?.type !== 'Section') fail('The document root must be a Section.');
  let questions = 0;
  for (const [id, node] of entries) {
    if (node.props.anchorId !== id) fail(`Block ${id} has a mismatched anchor.`);
    unique(node.children, `Children of ${id}`);
    if (node.type === 'BulletList') unique(node.props.items.map((item) => item.id), 'List');
    if (node.type === 'DataTable') {
      unique(node.props.columns.map((item) => item.id), 'Table columns');
      unique(node.props.rows.map((item) => item.id), 'Table rows');
      if (node.props.rows.some((row) => row.cells.length !== node.props.columns.length)) fail('Table rows must match the column count.');
    }
    if (node.type === 'Decision' || node.type === 'ChoiceQuestion' || node.type === 'MultiChoiceQuestion') {
      const ids = node.props.options.map((option) => option.id);
      unique(ids, 'Options');
      if (node.type !== 'MultiChoiceQuestion') {
        const selected = node.type === 'Decision' ? node.props.recommendedId : node.props.value;
        if (selected !== null && !ids.includes(selected)) fail('A selection must identify an offered option.');
      }
    }
    if (node.type === 'SliderQuestion') {
      const intervals = sliderIntervals(node.props);
      if (node.props.min >= node.props.max || intervals === null || intervals < 1 || intervals > 10000) {
        fail('Sliders require 1 to 10,000 evenly spaced steps between increasing bounds.');
      }
      if (node.props.labels !== null && node.props.labels.length !== intervals + 1) {
        fail('Slider labels must match every step including both endpoints.');
      }
    }
    if ((node.type === 'NumberQuestion' || node.type === 'DateQuestion')
      && node.props.min !== null && node.props.max !== null && node.props.min > node.props.max) {
      fail('Question minimum cannot exceed its maximum.');
    }
    if (isQuestion(node)) {
      questions++;
      if (kind === 'artifact') fail('Artifact documents cannot contain clarification questions.');
      const value = defaultAnswer(node);
      if (value !== null) {
        const issue = questionError(node, value, false);
        if (issue) fail(`Invalid default for "${node.props.label}": ${issue}`);
      }
    }
    if (kind === 'clarification' && !['Section', 'Heading', 'Text'].includes(node.type) && !isQuestion(node)) {
      fail('Clarification documents can contain only text, sections, and questions.');
    }
  }
  if (kind === 'clarification' && (questions < 1 || questions > 8)) fail('A clarification must contain 1 to 8 questions.');
  const visiting = new Set<string>();
  const visited = new Set<string>();
  function walk(id: string, depth: number) {
    if (depth > 8) fail('The document exceeds the maximum depth of 8.');
    if (visiting.has(id)) fail('The document contains a cycle.');
    if (visited.has(id)) fail('A document block cannot have multiple parents.');
    if (!Object.hasOwn(spec.elements, id)) fail(`The document references missing block ${id}.`);
    const node = spec.elements[id];
    visiting.add(id);
    visited.add(id);
    for (const child of node.children) walk(child, depth + 1);
    visiting.delete(id);
  }
  walk(spec.root, 1);
  if (visited.size !== entries.length) fail('The document contains unreachable blocks.');
  const structure = validateSpec(spec, { checkOrphans: true });
  if (!structure.valid || structure.issues.length) fail('The generated document has invalid structural references.');
  return spec;
}

export const catalog = defineCatalog(schema, {
  components: {
    Section: { props: componentProps.Section, slots: ['default'], description: 'A bounded section.' },
    Heading: { props: componentProps.Heading, description: 'A literal heading.' },
    Text: { props: componentProps.Text, description: 'Escaped plain text.' },
    BulletList: { props: componentProps.BulletList, description: 'Identified text list items.' },
    DataTable: { props: componentProps.DataTable, description: 'Identified rows of literal text.' },
    Decision: { props: componentProps.Decision, description: 'Alternatives; recommendation is not approval.' },
    ChoiceQuestion: { props: componentProps.ChoiceQuestion, description: 'A host-controlled choice.' },
    TextQuestion: { props: componentProps.TextQuestion, description: 'A host-controlled text answer.' },
    SliderQuestion: { props: componentProps.SliderQuestion, description: 'A labelled bounded scale, with visible suggested value.' },
    MultiChoiceQuestion: { props: componentProps.MultiChoiceQuestion, description: 'A set of choices as checkbox chips.' },
    NumberQuestion: { props: componentProps.NumberQuestion, description: 'A bounded numeric quantity with a meaningful unit.' },
    ToggleQuestion: { props: componentProps.ToggleQuestion, description: 'A yes/no task preference, never permission or approval.' },
    DateQuestion: { props: componentProps.DateQuestion, description: 'A date decision with native date semantics.' },
  },
  actions: {},
});

export function questionsOf(spec: WireSpec): QuestionElement[] {
  return questionsUnder(spec, spec.root);
}
export function questionsUnder(spec: WireSpec, root: string): QuestionElement[] {
  const result: QuestionElement[] = [];
  const visit = (id: string) => {
    const node = spec.elements[id];
    if (isQuestion(node)) result.push(node);
    for (const child of node.children) visit(child);
  };
  visit(root);
  return result;
}
export function defaultAnswer(node: QuestionElement): string | null {
  const value = node.props.value;
  if (value === null) return null;
  if (node.type === 'SliderQuestion' || node.type === 'NumberQuestion') return decimalAnswer(node.props.value!);
  if (node.type === 'MultiChoiceQuestion') return JSON.stringify(node.props.value);
  if (node.type === 'ToggleQuestion') return String(node.props.value);
  return String(value);
}
export function selectedOptions(value: string): string[] | null {
  if (!value) return [];
  try {
    const parsed = z.array(ElementId).max(8).safeParse(JSON.parse(value));
    return parsed.success ? parsed.data : null;
  } catch { return null; }
}
export function questionError(node: QuestionElement, value: string, required = node.props.required): string | null {
  if (value.length > 4000) return 'Keep the answer under 4,000 characters.';
  if (!value.trim()) {
    if (required) return 'Please answer this question.';
    return value !== '' && node.type !== 'TextQuestion' ? 'Clear this value or enter a valid answer.' : null;
  }
  switch (node.type) {
    case 'TextQuestion': return null;
    case 'ChoiceQuestion': return node.props.options.some((option) => option.id === value) ? null : 'Choose one of the available options.';
    case 'MultiChoiceQuestion': {
      const selected = selectedOptions(value);
      if (!selected) return 'Choose valid options from this question.';
      if (new Set(selected).size !== selected.length) return 'Each option can be selected only once.';
      if (selected.some((id) => !node.props.options.some((option) => option.id === id))) return 'One of these options is no longer offered.';
      return required && selected.length === 0 ? 'Choose at least one option.' : null;
    }
    case 'SliderQuestion':
    case 'NumberQuestion': {
      if (!decimal(value)) return 'Enter a valid number.';
      if (node.type === 'NumberQuestion'
        && (numericCompare(value, -1_000_000_000_000) < 0 || numericCompare(value, 1_000_000_000_000) > 0)) {
        return 'Use a number between -1000000000000 and 1000000000000.';
      }
      if (node.props.min !== null && numericCompare(value, node.props.min) < 0) return `Use a value of ${node.props.min} or more.`;
      if (node.props.max !== null && numericCompare(value, node.props.max) > 0) return `Use a value of ${node.props.max} or less.`;
      return aligned(value, node.props.min ?? 0, node.props.step)
        ? null : `Use increments of ${node.props.step}, starting at ${node.props.min ?? 0}.`;
    }
    case 'ToggleQuestion': return value === 'true' || value === 'false' ? null : 'Choose Yes or No.';
    case 'DateQuestion':
      if (!isIsoDate(value)) return 'Use a valid date in YYYY-MM-DD format.';
      if (node.props.min !== null && value < node.props.min) return `Choose ${node.props.min} or later.`;
      if (node.props.max !== null && value > node.props.max) return `Choose ${node.props.max} or earlier.`;
      return null;
  }
}
export function validateAnswers(spec: WireSpec, answers: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const node of questionsOf(spec)) {
    const error = questionError(node, answers[node.props.anchorId] ?? '');
    if (error) errors[node.props.anchorId] = error;
  }
  return errors;
}
export function seedAnswers(spec: WireSpec, saved: Record<string, string> = {}): Record<string, string> {
  return Object.fromEntries(questionsOf(spec).map((node) => {
    const previous = saved[node.props.anchorId];
    return [node.props.anchorId, previous !== undefined && !questionError(node, previous, false)
      ? previous : defaultAnswer(node) ?? ''];
  }));
}
export function encodeAnswers(spec: WireSpec, answers: Record<string, string>): Record<string, string> {
  const issues = validateAnswers(spec, answers);
  if (Object.keys(issues).length) throw new InvalidSpecError('Correct the highlighted answers before continuing.');
  return Object.fromEntries(questionsOf(spec).map((node) => {
    const value = answers[node.props.anchorId] ?? '';
    if (value && (node.type === 'NumberQuestion' || node.type === 'SliderQuestion')) {
      const encoded = decimalAnswer(value);
      if (encoded === null) throw new InvalidSpecError('Invalid numeric answer.');
      return [node.props.anchorId, encoded];
    }
    if (node.type === 'MultiChoiceQuestion') {
      const selected = selectedOptions(value);
      if (!selected) throw new InvalidSpecError('Invalid multi-choice answer.');
      return [node.props.anchorId, JSON.stringify(node.props.options.filter((option) => selected.includes(option.id)).map((option) => option.id))];
    }
    return [node.props.anchorId, value];
  }));
}
export function formatQuestionAnswer(node: QuestionElement, value: string): string {
  if (!value) return 'Not set';
  if (questionError(node, value, false)) return 'Check value';
  switch (node.type) {
    case 'ChoiceQuestion': return node.props.options.find((option) => option.id === value)?.label ?? 'Not set';
    case 'MultiChoiceQuestion': {
      const selected = selectedOptions(value);
      return node.props.options.filter((option) => selected?.includes(option.id)).map((option) => option.label).join(', ') || 'None selected';
    }
    case 'SliderQuestion': {
      const label = node.props.labels?.[Math.round((Number(value) - node.props.min) / node.props.step)];
      return label ?? `${decimalAnswer(value)}${node.props.unit ? ` ${node.props.unit}` : ''}`;
    }
    case 'NumberQuestion': return `${decimalAnswer(value)}${node.props.unit ? ` ${node.props.unit}` : ''}`;
    case 'ToggleQuestion': return value === 'true' ? 'Yes' : 'No';
    case 'DateQuestion': return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(`${value}T12:00:00Z`));
    case 'TextQuestion': return value.length > 90 ? `${value.slice(0, 87)}...` : value;
  }
}
export function blockText(node: WireElement): string {
  switch (node.type) {
    case 'Section': return node.props.title;
    case 'Heading': case 'Text': return node.props.text;
    case 'BulletList': return node.props.items.map((item) => item.text).join('\n');
    case 'DataTable': return [node.props.columns.map((column) => column.label).join(' | '),
      ...node.props.rows.map((row) => row.cells.join(' | '))].join('\n');
    case 'Decision': return `${node.props.question}\n${node.props.options.map((option) => `${option.label}: ${option.reason}`).join('\n')}`;
    case 'ChoiceQuestion': case 'TextQuestion': case 'SliderQuestion': case 'MultiChoiceQuestion':
    case 'NumberQuestion': case 'ToggleQuestion': case 'DateQuestion': return node.props.label;
  }
}
