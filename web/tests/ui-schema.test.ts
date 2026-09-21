import { describe, expect, it } from 'vitest';
import { catalog, parseSpec, validateAnswers, type WireSpec } from '../src/ui-schema';
import { artifactSpec, clarificationSpec } from './fixtures';

describe('strict literal UI contract', () => {
  it('accepts every artifact component and a host-controlled clarification', () => {
    expect(parseSpec(artifactSpec(), 'artifact').elements.decision.type).toBe('Decision');
    expect(parseSpec(clarificationSpec(), 'clarification').elements.audience.type).toBe('ChoiceQuestion');
  });
  it.each(['on', 'watch', 'visible', 'repeat', 'slots'])('rejects generated %s fields', (field) => {
    const candidate = artifactSpec();
    const hostile = { ...candidate, elements: { ...candidate.elements, body: { ...candidate.elements.body, [field]: {} } } };
    expect(() => parseSpec(hostile, 'artifact')).toThrow();
  });
  it.each(['state', 'actions', 'script', 'html'])('rejects extra top-level %s', (field) => {
    expect(() => parseSpec({ ...artifactSpec(), [field]: {} }, 'artifact')).toThrow();
  });
  it('rejects expressions and props the upstream catalog validator accepts', () => {
    const candidate = artifactSpec();
    const hostile = { ...candidate, elements: { ...candidate.elements,
      body: { ...candidate.elements.body, props: { anchorId: 'body', text: { $bindState: '/approved' } } },
    } };
    expect(catalog.validate(hostile).success).toBe(true);
    expect(() => parseSpec(hostile, 'artifact')).toThrow();
  });
  it('rejects unknown components and arbitrary DOM properties', () => {
    const candidate = artifactSpec();
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements, body: { type: 'iframe', props: { src: 'https://example.com' }, children: [] } } }, 'artifact')).toThrow();
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      body: { ...candidate.elements.body, props: { anchorId: 'body', text: 'Hello', style: { color: 'red' } } },
    } }, 'artifact')).toThrow();
  });
  it('does not accept SubmitAnswers or question components in artifacts', () => {
    expect(() => parseSpec(clarificationSpec(), 'artifact')).toThrow(/cannot contain/);
    expect(() => parseSpec(artifactSpec(), 'clarification')).toThrow(/Clarification documents/);
    expect(() => parseSpec({ root: 'submit', elements: { submit: {
      type: 'SubmitAnswers', props: { anchorId: 'submit', label: 'Continue' }, children: [],
    } } }, 'clarification')).toThrow();
  });
  it('requires exact anchors, explicit nullable props, and safe IDs', () => {
    const candidate = artifactSpec();
    expect(() => parseSpec({ ...candidate, root: 'constructor' }, 'artifact')).toThrow();
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      body: { type: 'Text', props: { anchorId: 'different', text: 'Hello' }, children: [] },
    } }, 'artifact')).toThrow(/mismatched anchor/);
    expect(() => parseSpec({ root: 'root', elements: { root: { type: 'Section', props: { anchorId: 'root', title: 'Missing null' }, children: [] } } }, 'artifact')).toThrow();
  });
  it('rejects cyclic, shared, duplicate, orphaned and missing references', () => {
    const candidate = artifactSpec();
    const root = candidate.elements.document;
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      document: { ...root, children: [...root.children, 'document'] },
    } }, 'artifact')).toThrow(/cycle/);
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      document: { ...root, children: [...root.children, 'body'] },
    } }, 'artifact')).toThrow(/duplicate/);
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      document: { ...root, children: ['body'] },
    } }, 'artifact')).toThrow(/unreachable/);
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      document: { ...root, children: ['missing'] },
    } }, 'artifact')).toThrow(/missing/);
    expect(() => parseSpec({ ...candidate, elements: { ...candidate.elements,
      document: { ...root, children: ['toString'] },
    } }, 'artifact')).toThrow(/missing/);
    const shared: WireSpec = {
      root: 'root', elements: {
        root: { type: 'Section', props: { anchorId: 'root', title: 'Root', description: null }, children: ['a', 'b'] },
        a: { type: 'Section', props: { anchorId: 'a', title: 'A', description: null }, children: ['text'] },
        b: { type: 'Section', props: { anchorId: 'b', title: 'B', description: null }, children: ['text'] },
        text: { type: 'Text', props: { anchorId: 'text', text: 'Shared' }, children: [] },
      },
    };
    expect(() => parseSpec(shared, 'artifact')).toThrow(/multiple parents/);
  });
  it('enforces the exact 96-node and depth-8 limits', () => {
    const wide: WireSpec = { root: 'root', elements: {
      root: { type: 'Section', props: { anchorId: 'root', title: 'Root', description: null }, children: [] },
    } };
    for (let group = 0; group < 4; group++) {
      const id = `group-${group}`;
      wide.elements.root.children.push(id);
      wide.elements[id] = { type: 'Section', props: { anchorId: id, title: id, description: null }, children: [] };
      for (let item = 0; item < (group === 3 ? 19 : 24); item++) {
        const leaf = `text-${group}-${item}`;
        wide.elements[id].children.push(leaf);
        wide.elements[leaf] = { type: 'Text', props: { anchorId: leaf, text: 'Content' }, children: [] };
      }
    }
    expect(Object.keys(parseSpec(wide, 'artifact').elements)).toHaveLength(96);
    wide.elements.extra = { type: 'Text', props: { anchorId: 'extra', text: 'Too much' }, children: [] };
    wide.elements['group-3'].children.push('extra');
    expect(() => parseSpec(wide, 'artifact')).toThrow(/96/);
    const deep: WireSpec = { root: 'section-1', elements: {} };
    for (let depth = 1; depth <= 8; depth++) {
      const id = `section-${depth}`;
      deep.elements[id] = { type: 'Section', props: { anchorId: id, title: id, description: null }, children: depth < 8 ? [`section-${depth + 1}`] : [] };
    }
    expect(() => parseSpec(deep, 'artifact')).not.toThrow();
    deep.elements['section-8'].children.push('leaf');
    deep.elements.leaf = { type: 'Text', props: { anchorId: 'leaf', text: 'Too deep' }, children: [] };
    expect(() => parseSpec(deep, 'artifact')).toThrow(/depth of 8/);
  });
  it('counts UTF-8 bytes rather than JavaScript string length', () => {
    const candidate: WireSpec = { root: 'root', elements: {
      root: { type: 'Section', props: { anchorId: 'root', title: 'Root', description: null }, children: [] },
    } };
    for (let index = 0; index < 13; index++) {
      const id = `text-${index}`;
      candidate.elements.root.children.push(id);
      candidate.elements[id] = { type: 'Text', props: { anchorId: id, text: '\u00e9'.repeat(8000) }, children: [] };
    }
    expect(JSON.stringify(candidate).length).toBeLessThan(192 * 1024);
    expect(() => parseSpec(candidate, 'artifact')).toThrow(/192 KiB/);
  });
  it('rejects duplicate nested IDs, bad table shapes and unoffered selections', () => {
    const candidate = artifactSpec();
    const table = candidate.elements.table;
    if (table.type !== 'DataTable') throw new Error('Fixture');
    table.props.rows[0].cells = ['one', 'two'];
    expect(() => parseSpec(candidate, 'artifact')).toThrow(/column count/);
    const clarification = clarificationSpec();
    const choice = clarification.elements.audience;
    if (choice.type !== 'ChoiceQuestion') throw new Error('Fixture');
    choice.props.value = 'not-offered';
    expect(() => parseSpec(clarification, 'clarification')).toThrow(/offered option/);
    choice.props.value = null;
    choice.props.options[1].id = choice.props.options[0].id;
    expect(() => parseSpec(clarification, 'clarification')).toThrow(/duplicate/);
  });
  it('validates required answers and choice membership in the host', () => {
    const spec = parseSpec(clarificationSpec(), 'clarification');
    expect(Object.keys(validateAnswers(spec, {}))).toHaveLength(2);
    expect(validateAnswers(spec, { audience: 'forged', constraints: 'A month' }).audience).toMatch(/available options/);
    expect(validateAnswers(spec, { audience: 'team', constraints: 'A month' })).toEqual({});
  });
});
