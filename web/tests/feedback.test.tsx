import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ActionResult } from '../src/api';
import { FeedbackComposer, type ComposerTarget } from '../src/components/FeedbackComposer';
import type { FeedbackRequest } from '../src/domain';
import { deferred } from './fixtures';

function target(elementId: string, revision = 1): ComposerTarget {
  return { instanceId: `instance-${elementId}`, mode: 'feedback', label: `Launch / ${elementId}`,
    quote: 'The original saved passage.', anchor: document.createElement('button'),
    selection: { artifactId: 'artifact-1', revision, elementId } };
}
describe('nonblocking anchored feedback', () => {
  it('submits independent anchors while an earlier enqueue is in flight', async () => {
    const first = deferred<ActionResult>(); const second = deferred<ActionResult>();
    const send = vi.fn<(request: FeedbackRequest) => Promise<ActionResult>>()
      .mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const view = render(<FeedbackComposer key="first" workspaceId="workspace-1" target={target('body')}
      onClose={vi.fn()} onFeedback={send} onEdit={vi.fn()} onLatest={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Your direction' }), { target: { value: 'Make this concise.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send direction' }));
    expect(send).toHaveBeenCalledTimes(1);
    view.rerender(<FeedbackComposer key="second" workspaceId="workspace-1" target={target('heading', 7)}
      onClose={vi.fn()} onFeedback={send} onEdit={vi.fn()} onLatest={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Your direction' }), { target: { value: 'Use a stronger heading.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send direction' }));
    expect(send).toHaveBeenCalledTimes(2);
    expect(send.mock.calls[0][0]).toMatchObject({ artifactId: 'artifact-1', revision: 1, elementId: 'body', text: 'Make this concise.' });
    expect(send.mock.calls[1][0]).toMatchObject({ artifactId: 'artifact-1', revision: 7, elementId: 'heading', text: 'Use a stronger heading.' });
    expect(send.mock.calls[0][0].clientRequestId).not.toBe(send.mock.calls[1][0].clientRequestId);
    await act(async () => { second.resolve({ ok: true }); first.resolve({ ok: true }); });
  });
  it('reuses the idempotency key after an uncertain network failure', async () => {
    const send = vi.fn<(request: FeedbackRequest) => Promise<ActionResult>>()
      .mockResolvedValueOnce({ ok: false, message: 'Connection lost. Your draft is kept.', conflict: false })
      .mockResolvedValueOnce({ ok: true });
    render(<FeedbackComposer workspaceId="workspace-1" target={target('body')}
      onClose={vi.fn()} onFeedback={send} onEdit={vi.fn()} onLatest={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Your direction' }), { target: { value: 'Keep this grounded.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send direction' }));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('Connection lost'));
    fireEvent.click(screen.getByRole('button', { name: 'Send direction' }));
    await waitFor(() => expect(send).toHaveBeenCalledTimes(2));
    expect(send.mock.calls[1][0]).toEqual(send.mock.calls[0][0]);
  });
  it('does not clear a newer draft or steal focus when an earlier submission finishes', async () => {
    const response = deferred<ActionResult>(); const close = vi.fn();
    render(<FeedbackComposer workspaceId="workspace-1" target={target('body')}
      onClose={close} onFeedback={() => response.promise} onEdit={vi.fn()} onLatest={vi.fn()} />);
    const input = screen.getByRole('textbox', { name: 'Your direction' }) as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: 'First direction.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send direction' }));
    fireEvent.change(input, { target: { value: 'A second direction while the request is in flight.' } });
    input.focus();
    await act(async () => { response.resolve({ ok: true }); });
    expect(input.value).toBe('A second direction while the request is in flight.');
    expect(document.activeElement).toBe(input);
    expect(close).not.toHaveBeenCalled();
  });
  it('persists draft text separately for each original revision/anchor', () => {
    const view = render(<FeedbackComposer key="a" workspaceId="workspace-1" target={target('body')}
      onClose={vi.fn()} onFeedback={vi.fn()} onEdit={vi.fn()} onLatest={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Your direction' }), { target: { value: 'My unsent direction.' } });
    view.rerender(<FeedbackComposer key="b" workspaceId="workspace-1" target={target('heading')}
      onClose={vi.fn()} onFeedback={vi.fn()} onEdit={vi.fn()} onLatest={vi.fn()} />);
    expect((screen.getByRole('textbox', { name: 'Your direction' }) as HTMLTextAreaElement).value).toBe('');
    view.rerender(<FeedbackComposer key="c" workspaceId="workspace-1" target={target('body')}
      onClose={vi.fn()} onFeedback={vi.fn()} onEdit={vi.fn()} onLatest={vi.fn()} />);
    expect((screen.getByRole('textbox', { name: 'Your direction' }) as HTMLTextAreaElement).value).toBe('My unsent direction.');
  });
  it('does not let a completed, unmounted composer steal focus from another anchor', async () => {
    const first = deferred<ActionResult>(); const closeFirst = vi.fn();
    const view = render(<FeedbackComposer key="a" workspaceId="workspace-1" target={target('body')}
      onClose={closeFirst} onFeedback={() => first.promise} onEdit={vi.fn()} onLatest={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Your direction' }), { target: { value: 'Queued first.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send direction' }));
    view.rerender(<FeedbackComposer key="b" workspaceId="workspace-1" target={target('heading')}
      onClose={vi.fn()} onFeedback={vi.fn()} onEdit={vi.fn()} onLatest={vi.fn()} />);
    const input = screen.getByRole('textbox', { name: 'Your direction' }) as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: 'Keep writing here.' } });
    input.focus();
    await act(async () => first.resolve({ ok: true }));
    expect(document.activeElement).toBe(input);
    expect(input.value).toBe('Keep writing here.');
    expect(closeFirst).not.toHaveBeenCalled();
  });
});
