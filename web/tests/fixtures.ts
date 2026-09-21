import type { WireSpec } from '../src/ui-schema';
import type { Workspace } from '../src/domain';

export function artifactSpec(): WireSpec {
  return {
    root: 'document',
    elements: {
      document: { type: 'Section', props: { anchorId: 'document', title: 'A thoughtful launch', description: null }, children: ['heading', 'body', 'milestones', 'table', 'decision'] },
      heading: { type: 'Heading', props: { anchorId: 'heading', level: 'h2', text: 'Start with the audience' }, children: [] },
      body: { type: 'Text', props: { anchorId: 'body', text: 'Keep the first release focused on a clear customer problem.' }, children: [] },
      milestones: { type: 'BulletList', props: { anchorId: 'milestones', ordered: false, items: [{ id: 'discovery', text: 'Validate the audience.' }] }, children: [] },
      table: { type: 'DataTable', props: { anchorId: 'table', caption: 'Accountabilities', columns: [{ id: 'owner', label: 'Owner' }], rows: [{ id: 'row-one', cells: ['You'] }] }, children: [] },
      decision: { type: 'Decision', props: { anchorId: 'decision', question: 'How should we launch?',
        options: [{ id: 'phased', label: 'Phased', reason: 'Learn before widening.' }, { id: 'broad', label: 'Broad', reason: 'Reach the whole audience immediately.' }],
        recommendedId: 'phased' }, children: [] },
    },
  };
}
export function clarificationSpec(): WireSpec {
  return {
    root: 'clarification',
    elements: {
      clarification: { type: 'Section', props: { anchorId: 'clarification', title: 'Shape the brief', description: 'A couple of decisions first.' }, children: ['audience', 'constraints'] },
      audience: { type: 'ChoiceQuestion', props: { anchorId: 'audience', label: 'Who is this for?', help: null, required: true,
        options: [{ id: 'leadership', label: 'Leadership' }, { id: 'team', label: 'Project team' }], value: null }, children: [] },
      constraints: { type: 'TextQuestion', props: { anchorId: 'constraints', label: 'What constraints matter?', help: 'Describe the limits.', required: true, multiline: true, value: null }, children: [] },
    },
  };
}
export function visualClarificationSpec(): WireSpec {
  return {
    root: 'visual-clarification',
    elements: {
      'visual-clarification': { type: 'Section', props: { anchorId: 'visual-clarification', title: 'Shape the launch plan', description: 'A couple of decisions before drafting.' }, children: ['detail', 'stakeholders'] },
      detail: { type: 'SliderQuestion', props: {
        anchorId: 'detail', label: 'How detailed should the plan be?', help: null, required: true,
        min: 1, max: 5, step: 1, value: 3, unit: null, minLabel: 'Executive Brief', maxLabel: 'Full Playbook',
        labels: ['Executive Brief', 'Short Overview', 'Standard Plan', 'Detailed Plan', 'Full Playbook'],
      }, children: [] },
      stakeholders: { type: 'MultiChoiceQuestion', props: {
        anchorId: 'stakeholders', label: 'Who is this plan for?', help: 'Choose the teams that need to act.', required: true,
        options: [{ id: 'product', label: 'Product' }, { id: 'marketing', label: 'Marketing' }, { id: 'sales', label: 'Sales' }, { id: 'leadership', label: 'Leadership' }],
        value: ['product', 'marketing'],
      }, children: [] },
    },
  };
}
export function richClarificationSpec(): WireSpec {
  const spec = visualClarificationSpec();
  spec.elements[spec.root].children.push('tone', 'risks', 'budget', 'deadline', 'notes');
  spec.elements.tone = { type: 'ChoiceQuestion', props: {
    anchorId: 'tone', label: 'Preferred tone', help: null, required: true,
    options: [{ id: 'direct', label: 'Clear and direct' }, { id: 'warm', label: 'Warm and encouraging' }], value: 'direct',
  }, children: [] };
  spec.elements.risks = { type: 'ToggleQuestion', props: {
    anchorId: 'risks', label: 'Include launch risks?', help: null, required: true, value: true,
  }, children: [] };
  spec.elements.budget = { type: 'NumberQuestion', props: {
    anchorId: 'budget', label: 'Budget ceiling', help: null, required: false,
    min: 0, max: 100000, step: 250, value: 5000, unit: 'USD',
  }, children: [] };
  spec.elements.deadline = { type: 'DateQuestion', props: {
    anchorId: 'deadline', label: 'Target launch date', help: null, required: false,
    min: '2026-10-01', max: '2026-12-31', value: '2026-10-14',
  }, children: [] };
  spec.elements.notes = { type: 'TextQuestion', props: {
    anchorId: 'notes', label: 'Anything else that changes the plan?', help: 'Only add a constraint not covered above.',
    required: false, multiline: true, value: null,
  }, children: [] };
  return spec;
}
export function workspace(id = 'workspace-1'): Workspace {
  return {
    id, title: 'A thoughtful launch', objective: 'Plan a thoughtful product launch.', provider: 'demo',
    status: 'review', summary: 'Drafts are ready for your review.', createdAt: '2026-09-20T10:00:00Z', updatedAt: '2026-09-20T10:01:00Z',
    revision: 5, eventSequence: 10, clarificationId: null, clarification: null, answers: {},
    agents: ['planner', 'producer', 'reviewer'].map((agentId) => ({
      id: agentId, name: `${agentId[0].toUpperCase()}${agentId.slice(1)}`, role: agentId, assigned: true,
      status: 'complete', message: 'Work completed.', sessionId: null, activeJobId: null,
    })),
    artifacts: [{
      id: 'artifact-1', title: 'Launch plan', currentRevision: 1, acceptedRevision: null,
      revisions: [{ revision: 1, baseRevision: null, spec: artifactSpec(), status: 'proposed',
        source: 'producer', jobId: 'job-1', review: 'The trade-offs are explicit. Check the timing before accepting.',
        conflict: false, createdAt: '2026-09-20T10:01:00Z' }],
    }],
    jobs: [{ id: 'job-1', kind: 'initial', order: 1, status: 'completed', message: 'Proposal created.',
      feedbackId: null, retryOf: null, errorCode: null, createdAt: '2026-09-20T10:00:00Z',
      startedAt: '2026-09-20T10:00:01Z', completedAt: '2026-09-20T10:01:00Z', baseRevisions: {} }],
    feedback: [], decisions: [],
    layout: { positions: {}, viewport: { x: 36, y: 36, zoom: 0.85 } },
  };
}
export const bootstrap = {
  csrfToken: 'local-test-token', model: 'auto', reasoningEffort: null,
  providers: [{ id: 'copilot', label: 'Live Copilot' }, { id: 'demo', label: 'Demo' }], version: 'test',
};
export function deferred<T>() {
  let resolve: (value: T) => void = () => { throw new Error('Deferred promise not initialized'); };
  let reject: (reason: unknown) => void = () => { throw new Error('Deferred promise not initialized'); };
  const promise = new Promise<T>((resolvePromise, rejectPromise) => { resolve = resolvePromise; reject = rejectPromise; });
  return { promise, resolve, reject };
}
