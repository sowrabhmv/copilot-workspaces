import { useState } from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { api, type ActionResult } from '../src/api';
import type { Agent, Job, Workspace } from '../src/domain';
import { AgentCard } from '../src/components/ActivityPanel';
import { WorkspaceScreen } from '../src/components/WorkspaceScreen';
import { deferred, workspace } from './fixtures';

function failedAgent(): Agent {
  return {
    id: 'planner', name: 'Planner', role: 'planner', assigned: true, status: 'error',
    message: 'Copilot could not resume the saved session.', sessionId: 'saved-before-first-prompt', activeJobId: 'job-1',
  };
}
function failedWorkspace(): Workspace {
  const document = workspace();
  return {
    ...document, provider: 'copilot', status: 'error',
    agents: [failedAgent(), ...document.agents.slice(1)],
    jobs: [{ ...document.jobs[0], status: 'failed', errorCode: 'copilot_session_unavailable', message: 'The saved conversation cannot be resumed.' }],
  };
}
function ScreenHarness({ initial }: { initial: Workspace }) {
  const [document, setDocument] = useState(initial);
  return <WorkspaceScreen workspace={document} bootstrap={null} connection="connected" error={null}
    onDocument={setDocument} onRefresh={vi.fn()} onReconnect={vi.fn()} onMenu={vi.fn()} onHome={vi.fn()} />;
}

describe('explicit Copilot session recovery', () => {
  it.each([false, true])('keeps Activity named when compact styling hides its text (pending work: %s)', (pendingWork) => {
    const initial = workspace();
    if (pendingWork) initial.jobs[0].status = 'queued';
    const view = render(<ScreenHarness initial={initial} />);
    const activity = view.container.querySelector<HTMLButtonElement>('button.activity-toggle');
    if (!activity) throw new Error('Activity control missing');
    activity.querySelectorAll<HTMLSpanElement>(':scope > span:not(.count-badge)')
      .forEach((label) => { label.style.display = 'none'; });
    expect(activity.getAttribute('aria-label')).toBe('Activity');
    expect(screen.getByRole('button', { name: 'Activity' })).toBe(activity);
    for (const control of view.container.querySelectorAll('button.icon-button, button.composer-send')) {
      expect(control.getAttribute('aria-label')).toMatch(/\S/);
    }
    fireEvent.click(activity);
    expect(activity.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('complementary', { name: 'Workspace activity' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Close activity' })).toBeTruthy();
  });
  it('does not expose reset controls for demo agents or missing session references', () => {
    const reset = vi.fn();
    const view = render(<AgentCard agent={failedAgent()} provider="demo" workPending={false} sessionUnavailable
      onResetSession={reset} onOpenActivity={vi.fn()} />);
    expect(screen.queryByText('Session options')).toBeNull();
    view.rerender(<AgentCard agent={{ ...failedAgent(), sessionId: null }} provider="copilot" workPending={false} sessionUnavailable
      onResetSession={reset} onOpenActivity={vi.fn()} />);
    expect(screen.queryByRole('button', { name: 'Start a new Planner session' })).toBeNull();
    expect(reset).not.toHaveBeenCalled();
  });
  it('explains conditional recovery for a cancelled session without asserting a missing prompt', () => {
    render(<AgentCard agent={{ ...failedAgent(), status: 'cancelled' }} provider="copilot" workPending={false}
      sessionUnavailable={false} onResetSession={vi.fn()} onOpenActivity={vi.fn()} />);
    expect(screen.getByText(/If the run failed or was cancelled before its first prompt/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start a new Planner session' })).toBeTruthy();
  });
  it.each(['queued', 'running', 'cancelling'] as const)('disables session reset for any %s workspace job', (status) => {
    const document = failedWorkspace();
    const pending: Job = { ...document.jobs[0], id: 'other-job', order: 2, status, errorCode: null };
    render(<ScreenHarness initial={{ ...document, jobs: [...document.jobs, pending] }} />);
    const button = screen.getByRole('button', { name: 'Start a new Planner session' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fireEvent.click(button);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByText(/Available after all queued, running, and cancelling work finishes/)).toBeTruthy();
  });
  it('requires confirmation, traps focus, supports Escape, and restores the trigger', () => {
    const root = document.createElement('div');
    root.id = 'root';
    document.body.append(root);
    const reset = vi.fn();
    render(<AgentCard agent={failedAgent()} provider="copilot" workPending={false} sessionUnavailable
      onResetSession={reset} onOpenActivity={vi.fn()} />, { container: root });
    const trigger = screen.getByRole('button', { name: 'Start a new Planner session' });
    trigger.focus();
    fireEvent.click(trigger);
    const dialog = screen.getByRole('dialog', { name: 'Start a new Planner session?' });
    const cancel = within(dialog).getByRole('button', { name: 'Cancel' });
    const confirm = within(dialog).getByRole('button', { name: 'Start new session' });
    expect(document.activeElement).toBe(cancel);
    expect(root.inert).toBe(true);
    expect(root.getAttribute('aria-hidden')).toBe('true');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(within(dialog).getByText(/The old session is not deleted/)).toBeTruthy();
    expect(within(dialog).getByText(/old session reference remains in the event audit/)).toBeTruthy();
    expect(within(dialog).getByText(/Then choose Retry in Activity/)).toBeTruthy();
    fireEvent.keyDown(cancel, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(confirm);
    fireEvent.keyDown(confirm, { key: 'Tab' });
    expect(document.activeElement).toBe(cancel);
    fireEvent.keyDown(cancel, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(trigger);
    expect(root.inert).toBe(false);
    expect(root.hasAttribute('aria-hidden')).toBe(false);
    expect(reset).not.toHaveBeenCalled();
  });
  it('blocks an open confirmation if another job becomes pending', () => {
    const reset = vi.fn();
    const props = { agent: failedAgent(), provider: 'copilot' as const, sessionUnavailable: true, onResetSession: reset, onOpenActivity: vi.fn() };
    const view = render(<AgentCard {...props} workPending={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start a new Planner session' }));
    view.rerender(<AgentCard {...props} workPending />);
    const dialog = screen.getByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Start new session' }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    expect(within(dialog).getByRole('status').textContent).toContain('Work is queued, running, or cancelling');
    fireEvent.click(confirm);
    expect(reset).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(document.activeElement).toBe(screen.getByRole('article', { name: 'Planner agent' }));
  });
  it('requires a fresh confirmation if the saved reference changes', () => {
    const reset = vi.fn();
    const props = { provider: 'copilot' as const, workPending: false, sessionUnavailable: true, onResetSession: reset, onOpenActivity: vi.fn() };
    const view = render(<AgentCard {...props} agent={failedAgent()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start a new Planner session' }));
    view.rerender(<AgentCard {...props} agent={{ ...failedAgent(), sessionId: 'a-different-conversation' }} />);
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByRole('status').textContent).toContain('saved session changed');
    expect((within(dialog).getByRole('button', { name: 'Start new session' }) as HTMLButtonElement).disabled).toBe(true);
    expect(reset).not.toHaveBeenCalled();
  });
  it('surfaces a rejected reset and preserves the saved reference', async () => {
    const reset = vi.fn<() => Promise<ActionResult>>()
      .mockResolvedValue({ ok: false, message: 'A job was queued before the reset. Wait for it to finish.', conflict: true });
    render(<AgentCard agent={failedAgent()} provider="copilot" workPending={false} sessionUnavailable
      onResetSession={reset} onOpenActivity={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start a new Planner session' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Start new session' }));
    await waitFor(() => expect(within(dialog).getByRole('alert').textContent).toContain('A job was queued'));
    expect(reset).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Saved session reference cleared/)).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(screen.getByRole('button', { name: 'Start a new Planner session' })).toBeTruthy();
  });
  it('deduplicates confirmation while the reset request is in flight', async () => {
    const request = deferred<ActionResult>();
    const reset = vi.fn<() => Promise<ActionResult>>().mockReturnValue(request.promise);
    render(<AgentCard agent={failedAgent()} provider="copilot" workPending={false} sessionUnavailable
      onResetSession={reset} onOpenActivity={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start a new Planner session' }));
    const dialog = screen.getByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Start new session' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    expect(reset).toHaveBeenCalledTimes(1);
    expect(dialog.getAttribute('aria-busy')).toBe('true');
    expect((within(dialog).getByRole('button', { name: 'Clearing reference...' }) as HTMLButtonElement).disabled).toBe(true);
    await act(async () => request.resolve({ ok: false, message: 'Request interrupted. Try again.', conflict: false }));
    expect(within(dialog).getByRole('alert').textContent).toContain('Request interrupted');
  });
  it('applies the canonical reset response, retains artifacts, and retries only on a separate user action', async () => {
    const original = failedWorkspace();
    original.agents[1].sessionId = 'producer-conversation-kept';
    const updated = structuredClone(original);
    updated.agents[0].sessionId = null;
    updated.revision++;
    updated.eventSequence++;
    const reset = vi.spyOn(api, 'resetAgentSession').mockResolvedValue(updated);
    const retry = vi.spyOn(api, 'job').mockResolvedValue(updated);
    render(<ScreenHarness initial={original} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start a new Planner session' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Start new session' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(reset).toHaveBeenCalledWith(original.id, 'planner');
    expect(retry).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Start a new Planner session' })).toBeNull();
    expect(screen.getByText(/Saved session reference cleared/)).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'A thoughtful launch' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Open Activity to retry' }));
    expect(retry).not.toHaveBeenCalled();
    expect(screen.getByText(/If you have not already cleared that reference/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(retry).toHaveBeenCalledWith(original.id, 'job-1', 'retry'));
  });
});
