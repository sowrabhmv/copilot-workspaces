import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { WorkspaceApi } from '../src/api';
import { useWorkspace } from '../src/hooks';
import type { Workspace } from '../src/domain';
import { deferred, workspace } from './fixtures';

class TestEventSource {
  static instances: TestEventSource[] = [];
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  closed = false;
  private listeners = new Map<string, EventListenerOrEventListenerObject>();
  constructor(readonly url: string) { TestEventSource.instances.push(this); }
  addEventListener(type: string, listener: EventListenerOrEventListenerObject) { this.listeners.set(type, listener); }
  close() { this.closed = true; }
  emit(sequence: number, workspaceId = 'workspace-1') {
    const listener = this.listeners.get('workspace');
    const event = new MessageEvent('workspace', { data: JSON.stringify({
      sequence, workspaceId, type: 'job', message: 'Updated', jobId: 'job-1', agentId: null, createdAt: '2026-09-20T10:02:00Z',
    }) });
    if (typeof listener === 'function') listener(event);
    else listener?.handleEvent(event);
  }
}
afterEach(() => { TestEventSource.instances = []; });

describe('canonical per-workspace SSE', () => {
  it('refreshes on events and reconnect without cancelling durable jobs', async () => {
    vi.stubGlobal('EventSource', TestEventSource);
    const client = new WorkspaceApi();
    const get = vi.spyOn(client, 'get').mockResolvedValue(workspace());
    const cancel = vi.spyOn(client, 'job');
    const update = vi.fn();
    const view = renderHook(() => useWorkspace('workspace-1', update, client));
    await waitFor(() => expect(view.result.current.workspace?.id).toBe('workspace-1'));
    const source = TestEventSource.instances[0];
    expect(source.url).toBe('/api/workspaces/workspace-1/events?after=10');
    act(() => source.onopen?.());
    await waitFor(() => expect(get).toHaveBeenCalledTimes(2));
    expect(view.result.current.connection).toBe('connected');
    get.mockResolvedValue({ ...workspace(), eventSequence: 11, revision: 6, summary: 'New durable update.' });
    act(() => source.emit(11));
    await waitFor(() => expect(view.result.current.workspace?.summary).toBe('New durable update.'));
    const calls = get.mock.calls.length;
    act(() => source.emit(11));
    expect(get.mock.calls.length).toBe(calls);
    act(() => source.onerror?.());
    expect(view.result.current.connection).toBe('reconnecting');
    act(() => source.onopen?.());
    await waitFor(() => expect(get.mock.calls.length).toBe(calls + 1));
    view.unmount();
    expect(source.closed).toBe(true);
    expect(cancel).not.toHaveBeenCalled();
  });
  it('ignores a late response from a previously selected workspace', async () => {
    vi.stubGlobal('EventSource', TestEventSource);
    const first = deferred<Workspace>(); const second = deferred<Workspace>();
    const client = new WorkspaceApi();
    vi.spyOn(client, 'get').mockImplementation((id) => id === 'first' ? first.promise : second.promise);
    const update = vi.fn();
    const view = renderHook(({ id }) => useWorkspace(id, update, client), { initialProps: { id: 'first' } });
    view.rerender({ id: 'second' });
    await act(async () => second.resolve(workspace('second')));
    await waitFor(() => expect(view.result.current.workspace?.id).toBe('second'));
    await act(async () => first.resolve(workspace('first')));
    expect(view.result.current.workspace?.id).toBe('second');
    expect(update.mock.calls.every((call) => call[0].id === 'second')).toBe(true);
  });
});
