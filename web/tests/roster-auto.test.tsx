import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AgentSchema, BootstrapSchema, ProviderStatusSchema, WorkspaceSchema, type Agent } from '../src/domain';
import { Home } from '../src/components/Home';
import { WorkspaceScreen } from '../src/components/WorkspaceScreen';
import { bootstrap, workspace } from './fixtures';

function agent(id: string, name: string, assigned = true): Agent {
  return { id, name, role: `A task-specific purpose for ${name}.`, assigned, status: 'idle',
    message: 'Ready for the task.', sessionId: `saved-${id}`, activeJobId: null };
}
function show(agents: Agent[], review: string | null = null) {
  const document = workspace();
  document.provider = 'copilot';
  document.agents = agents;
  document.artifacts[0].revisions[0].review = review;
  const view = render(<WorkspaceScreen workspace={document} bootstrap={BootstrapSchema.parse(bootstrap)}
    connection="connected" error={null} onDocument={vi.fn()} onRefresh={vi.fn()}
    onReconnect={vi.fn()} onMenu={vi.fn()} onHome={vi.fn()} />);
  return { ...view, document };
}

describe('task-dependent agent roster', () => {
  it('defaults legacy agents to assigned while preserving explicit historical membership', () => {
    const original = agent('planner', 'Coordinator');
    const legacy = { ...original };
    Reflect.deleteProperty(legacy, 'assigned');
    expect(AgentSchema.parse(legacy).assigned).toBe(true);
    expect(AgentSchema.parse({ ...original, assigned: false }).assigned).toBe(false);
    const document = workspace();
    document.jobs[0].kind = 'clarify';
    expect(WorkspaceSchema.parse(document).jobs[0].kind).toBe('clarify');
  });
  it('renders no phantom participants or edges when no agents are assigned', () => {
    const view = show([agent('planner', 'Old coordinator', false), agent('reviewer', 'Historical reviewer', false)]);
    expect(view.container.querySelectorAll('.node-agent')).toHaveLength(0);
    expect(view.container.querySelectorAll('.canvas-connections > path')).toHaveLength(0);
    const navigation = screen.getByRole('button', { name: /Agents 0/ }) as HTMLButtonElement;
    expect(navigation.disabled).toBe(true);
    expect(screen.queryByRole('article', { name: 'Historical reviewer agent' })).toBeNull();
    expect(view.document.agents).toHaveLength(2);
    expect(view.document.agents[1].sessionId).toBe('saved-reviewer');
  });
  it('supports a Coordinator-only task and direct artifacts with human approval but no fabricated AI review', () => {
    const view = show([agent('planner', 'Coordinator')]);
    expect(view.container.querySelectorAll('.node-agent')).toHaveLength(1);
    expect(screen.getByRole('article', { name: 'Coordinator agent' })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Agents 1/ })).toBeTruthy();
    const paths = view.container.querySelectorAll('.canvas-connections > path');
    expect(paths).toHaveLength(1);
    expect(paths[0].getAttribute('data-from')).toBe('context');
    expect(paths[0].getAttribute('data-to')).toBe('agent-planner');
    expect(screen.queryByText('AI review')).toBeNull();
    expect(screen.queryByText('Not human approval')).toBeNull();
    expect(screen.getByRole('button', { name: 'Accept version' })).toBeTruthy();
  });
  it('renders and connects the recruited specialists, excluding historical sessions without deleting them', () => {
    const assigned = [agent('planner', 'Coordinator'), agent('market-analyst', 'Market analyst'),
      agent('release-designer', 'Release designer'), agent('budget-specialist', 'Budget specialist'),
      agent('risk-scout', 'Risk scout')];
    const historical = agent('reviewer', 'Old mandatory reviewer', false);
    const view = show([...assigned, historical]);
    expect(view.container.querySelectorAll('.node-agent')).toHaveLength(5);
    expect(screen.getByRole('button', { name: /Agents 5/ })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Old mandatory reviewer' })).toBeNull();
    const edges = Array.from(view.container.querySelectorAll('.canvas-connections > path'))
      .map((path) => [path.getAttribute('data-from'), path.getAttribute('data-to')]);
    expect(edges).toEqual([
      ['context', 'agent-planner'],
      ['agent-planner', 'agent-market-analyst'],
      ['agent-planner', 'agent-release-designer'],
      ['agent-planner', 'agent-budget-specialist'],
      ['agent-planner', 'agent-risk-scout'],
    ]);
    expect(view.container.querySelector<HTMLElement>('[data-node-id="agent-risk-scout"]')?.style.top).toBe('364px');
    expect(view.container.querySelector<HTMLElement>('[data-node-id="artifact-artifact-1"]')?.style.top).toBe('744px');
    expect(view.document.agents.at(-1)?.sessionId).toBe('saved-reviewer');
    expect(view.document.agents).toHaveLength(6);
  });
  it.each([null, '', '   \n'])('does not display an empty AI review (%j)', (review) => {
    show([agent('planner', 'Coordinator')], review);
    expect(screen.queryByText('AI review')).toBeNull();
    expect(screen.getByRole('button', { name: 'Accept version' })).toBeTruthy();
  });
});

describe('Auto model policy', () => {
  it('parses nullable effort and labels live work as Auto, never auto/null', () => {
    const status = ProviderStatusSchema.parse({ id: 'copilot', state: 'ready', message: 'Ready for work.',
      model: 'auto', reasoningEffort: null, sdkVersion: null, cliVersion: null, setupCommand: null });
    const create = vi.fn();
    render(<Home bootstrap={BootstrapSchema.parse(bootstrap)} provider={status} providerLoading={false}
      providerError={null} startupError={null} creating={null} tasks={[]} onCreate={create}
      onRecheck={vi.fn()} onOpen={vi.fn()} onMenu={vi.fn()} />);
    expect(screen.getByText('Auto')).toBeTruthy();
    expect(screen.getByText('Copilot chooses the model. You stay focused on the outcome.')).toBeTruthy();
    expect(screen.queryByRole('combobox', { name: /model/i })).toBeNull();
    expect(globalThis.document.body.textContent).not.toContain('auto / null');
    expect(globalThis.document.body.textContent).not.toContain('gpt-6-astra');
    fireEvent.change(screen.getByRole('textbox', { name: 'What would you like to accomplish?' }), { target: { value: 'Shape my project.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start task' }));
    expect(create).toHaveBeenCalledWith('Shape my project.', 'copilot');
  });
  it('uses the same Auto policy label on the collaboration canvas', () => {
    show([agent('planner', 'Coordinator')]);
    expect(screen.getByText('Auto / Copilot chooses the model / human-led')).toBeTruthy();
    expect(globalThis.document.body.textContent).not.toContain('auto / null');
    expect(globalThis.document.body.textContent).not.toContain(' / max');
  });
});
