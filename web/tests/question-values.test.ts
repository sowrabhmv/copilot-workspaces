import { describe, expect, it } from 'vitest';
import {
  decimalAnswer, encodeAnswers, parseSpec, questionsOf, seedAnswers, sliderValueAt, validateAnswers,
  type QuestionElement, type WireSpec,
} from '../src/ui-schema';
import { richClarificationSpec, visualClarificationSpec } from './fixtures';

function oneQuestion(question: QuestionElement): WireSpec {
  return { root: 'root', elements: {
    root: { type: 'Section', props: { anchorId: 'root', title: 'One decision', description: null }, children: [question.props.anchorId] },
    [question.props.anchorId]: question,
  } };
}
function slider() {
  const element = visualClarificationSpec().elements.detail;
  if (element.type !== 'SliderQuestion') throw new Error('Fixture');
  return element;
}
function number() {
  const element = richClarificationSpec().elements.budget;
  if (element.type !== 'NumberQuestion') throw new Error('Fixture');
  return element;
}
function date() {
  const element = richClarificationSpec().elements.deadline;
  if (element.type !== 'DateQuestion') throw new Error('Fixture');
  return element;
}

describe('visual question wire contract', () => {
  it('accepts all seven question types without allowing them inside artifacts', () => {
    const spec = parseSpec(richClarificationSpec(), 'clarification');
    expect(questionsOf(spec)).toHaveLength(7);
    expect(() => parseSpec(spec, 'artifact')).toThrow(/cannot contain/);
  });
  it('seeds declared defaults into API-compatible strings, including false and zero', () => {
    const spec = richClarificationSpec();
    const toggle = spec.elements.risks;
    const quantity = spec.elements.budget;
    if (toggle.type !== 'ToggleQuestion' || quantity.type !== 'NumberQuestion') throw new Error('Fixture');
    toggle.props.value = false;
    quantity.props.value = 0;
    expect(seedAnswers(parseSpec(spec, 'clarification'))).toEqual({
      detail: '3', stakeholders: '["product","marketing"]', tone: 'direct', risks: 'false',
      budget: '0', deadline: '2026-10-14', notes: '',
    });
  });
  it('preserves valid saved human values and replaces incompatible old option values with the visible default', () => {
    const spec = parseSpec(visualClarificationSpec(), 'clarification');
    expect(seedAnswers(spec, { detail: '5', stakeholders: '["removed-option"]' })).toEqual({
      detail: '5', stakeholders: '["product","marketing"]',
    });
  });
  it.each([
    { min: 5, max: 1 },
    { min: 1, max: 1 },
    { min: -1000001 },
    { max: 1000001 },
    { step: 0 },
    { step: -1 },
    { min: 0, max: 1, step: 0.3, labels: null, value: null },
    { min: 0, max: 10001, step: 1, labels: null, value: 0 },
    { value: 0 },
    { value: 2.5 },
    { labels: ['Brief', 'Full'] },
    { labels: ['Brief', '', 'Medium', 'Long', 'Full'] },
  ])('rejects invalid slider configuration %j', (changes) => {
    const element = slider();
    expect(() => parseSpec(oneQuestion({ ...element, props: { ...element.props, ...changes } }), 'clarification')).toThrow();
  });
  it('accepts exact decimal steps and the 10,000-step boundary', () => {
    const element = slider();
    element.props = { ...element.props, min: -0.3, max: 0.3, step: 0.1, value: 0.2, labels: null };
    expect(() => parseSpec(oneQuestion(element), 'clarification')).not.toThrow();
    expect(sliderValueAt(element.props, 6)).toBe('0.3');
    element.props = { ...element.props, min: 0, max: 10000, step: 1, value: 5000 };
    expect(() => parseSpec(oneQuestion(element), 'clarification')).not.toThrow();
  });
  it('requires finite, bounded, step-aligned numeric defaults', () => {
    const element = number();
    for (const value of [Infinity, NaN, 1000000000001, -1, 125]) {
      expect(() => parseSpec(oneQuestion({ ...element, props: { ...element.props, value } }), 'clarification')).toThrow();
    }
    expect(() => parseSpec(oneQuestion({ ...element, props: { ...element.props, min: 10, max: 5 } }), 'clarification')).toThrow();
    element.props = { ...element.props, min: -1.5, max: 2.5, step: 0.5, value: 0 };
    expect(() => parseSpec(oneQuestion(element), 'clarification')).not.toThrow();
  });
  it('validates numeric answer bounds and exact step alignment without floating-point rounding', () => {
    const element = number();
    element.props = { ...element.props, min: null, max: null, step: 0.1, value: null, required: true };
    const spec = parseSpec(oneQuestion(element), 'clarification');
    for (const value of ['NaN', 'Infinity', '0x10', '1,000', '0.25', '0.30000000000000004', '1e999', '1e-999', '1000000000000.0001']) {
      expect(validateAnswers(spec, { budget: value }).budget).toBeTruthy();
    }
    expect(validateAnswers(spec, { budget: '0.3' })).toEqual({});
    expect(encodeAnswers(spec, { budget: '1.20e2' })).toEqual({ budget: '120' });
    expect(decimalAnswer('1e-7')).toBe('0.0000001');
    expect(decimalAnswer('-0.000')).toBe('0');
  });
  it('uses real calendar dates and supplied date bounds', () => {
    const element = date();
    for (const value of ['2026-02-29', '2026-13-01', '0000-01-01', '2026-10-32', '10/14/2026', '2026-09-30', '2027-01-01']) {
      expect(() => parseSpec(oneQuestion({ ...element, props: { ...element.props, value } }), 'clarification')).toThrow();
    }
    element.props = { ...element.props, min: '2028-02-01', max: '2028-03-01', value: '2028-02-29' };
    const spec = parseSpec(oneQuestion(element), 'clarification');
    expect(validateAnswers(spec, { deadline: '2028-02-29' })).toEqual({});
    expect(validateAnswers(spec, { deadline: '2028-03-02' }).deadline).toBeTruthy();
    expect(() => parseSpec(oneQuestion({ ...element, props: { ...element.props, min: '2028-03-02' } }), 'clarification')).toThrow();
  });
  it('rejects forged, duplicate and malformed multi-choice values', () => {
    const spec = visualClarificationSpec();
    const element = spec.elements.stakeholders;
    if (element.type !== 'MultiChoiceQuestion') throw new Error('Fixture');
    for (const value of [['unknown'], ['product', 'product']]) {
      expect(() => parseSpec(oneQuestion({ ...element, props: { ...element.props, value } }), 'clarification')).toThrow();
    }
    for (const value of ['[]', '["product","product"]', '["unknown"]', '{"approved":true}', '[true]', 'not-json']) {
      expect(validateAnswers(spec, { detail: '3', stakeholders: value }).stakeholders).toBeTruthy();
    }
    expect(encodeAnswers(spec, { detail: '3', stakeholders: '["sales","product"]' }))
      .toEqual({ detail: '3', stakeholders: '["product","sales"]' });
  });
  it('encodes an unanswered optional set as a JSON-string empty array', () => {
    const spec = visualClarificationSpec();
    const question = spec.elements.stakeholders;
    if (question.type !== 'MultiChoiceQuestion') throw new Error('Fixture');
    question.props.required = false;
    question.props.value = null;
    expect(encodeAnswers(spec, { detail: '3', stakeholders: '' })).toEqual({ detail: '3', stakeholders: '[]' });
  });
  it('treats toggle false as an answer, not missing consent or artifact approval', () => {
    const element = richClarificationSpec().elements.risks;
    if (element.type !== 'ToggleQuestion') throw new Error('Fixture');
    const spec = oneQuestion(element);
    expect(validateAnswers(spec, { risks: 'false' })).toEqual({});
    for (const value of ['yes', 'False', '', '1']) expect(validateAnswers(spec, { risks: value }).risks).toBeTruthy();
  });
  it('does not turn whitespace-only typed answers into successful null-like values', () => {
    const quantity = oneQuestion(number());
    expect(validateAnswers(quantity, { budget: '   ' }).budget).toBeTruthy();
    expect(() => encodeAnswers(quantity, { budget: '   ' })).toThrow(/Correct the highlighted/);
    expect(validateAnswers(quantity, { budget: '' })).toEqual({});
    expect(encodeAnswers(quantity, { budget: '' })).toEqual({ budget: '' });
    expect(validateAnswers(oneQuestion(date()), { deadline: ' ' }).deadline).toBeTruthy();
  });
  it('keeps legacy dense forms structurally readable instead of enforcing the new generation-quality gate', () => {
    const spec: WireSpec = { root: 'root', elements: {
      root: { type: 'Section', props: { anchorId: 'root', title: 'Saved form', description: null }, children: [] },
    } };
    for (let index = 0; index < 8; index++) {
      const id = `question-${index}`;
      spec.elements.root.children.push(id);
      spec.elements[id] = { type: 'TextQuestion', props: {
        anchorId: id, label: `Saved decision ${index}`, help: null, required: true, multiline: false, value: null,
      }, children: [] };
    }
    expect(questionsOf(parseSpec(spec, 'clarification'))).toHaveLength(8);
  });
  it('rejects dynamic actions and expressions in the new control types', () => {
    const element = slider();
    const original = oneQuestion(element);
    const candidate = { ...original, elements: { ...original.elements,
      detail: { ...element, props: { ...element.props, value: { $state: '/permissions' } } },
    } };
    expect(() => parseSpec(candidate, 'clarification')).toThrow();
    const withAction = { ...element, on: { change: { action: 'setState' } } };
    expect(() => parseSpec({ ...oneQuestion(element), elements: { ...oneQuestion(element).elements, detail: withAction } }, 'clarification')).toThrow();
  });
});
