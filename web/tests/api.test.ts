import { describe, expect, it, vi } from 'vitest';
import { ApiError, WorkspaceApi } from '../src/api';
import { newerWorkspace } from '../src/domain';
import { bootstrap, visualClarificationSpec, workspace } from './fixtures';

describe('same-origin typed adapter', () => {
  it('bootstraps the token and sends an explicit provider without fallback', async () => {
    const transport = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json(bootstrap))
      .mockResolvedValueOnce(Response.json(workspace(), { status: 201 }));
    const client = new WorkspaceApi(transport);
    await client.create('Plan a launch', 'copilot');
    expect(transport.mock.calls[0][0]).toBe('/api/bootstrap');
    const [path, request] = transport.mock.calls[1];
    expect(path).toBe('/api/workspaces');
    expect(request?.headers).toEqual({ 'Content-Type': 'application/json', 'X-Workspace-Token': bootstrap.csrfToken });
    expect(request?.body).toBe(JSON.stringify({ objective: 'Plan a launch', provider: 'copilot' }));
  });
  it('posts empty JSON for cancel/retry and preserves immutable feedback anchors', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValueOnce(Response.json(bootstrap))
      .mockResolvedValueOnce(Response.json(workspace())).mockResolvedValueOnce(Response.json(workspace()));
    const client = new WorkspaceApi(transport);
    await client.job('workspace-1', 'job-1', 'cancel');
    const body = { text: 'Shorten this', clientRequestId: 'direction-123', artifactId: 'artifact-1', elementId: 'body', revision: 1 };
    await client.feedback('workspace-1', body);
    expect(transport.mock.calls[1][1]?.body).toBe('{}');
    expect(JSON.parse(String(transport.mock.calls[2][1]?.body))).toEqual(body);
    expect(transport.mock.calls[2][1]?.headers).toEqual({ 'Content-Type': 'application/json', 'X-Workspace-Token': bootstrap.csrfToken });
  });
  it('refreshes a restarted service token without replaying a mutation', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValueOnce(Response.json(bootstrap))
      .mockResolvedValueOnce(Response.json({ code: 'invalid_token', message: 'The service restarted. Please retry.' }, { status: 403 }))
      .mockResolvedValueOnce(Response.json({ ...bootstrap, csrfToken: 'new-token' }))
      .mockResolvedValueOnce(Response.json(workspace()));
    const client = new WorkspaceApi(transport);
    await expect(client.create('A task', 'demo')).rejects.toMatchObject({ code: 'invalid_token', status: 403 });
    expect(transport).toHaveBeenCalledTimes(3);
    expect(transport.mock.calls.map((call) => call[1]?.method)).toEqual(['GET', 'POST', 'GET']);
    await client.create('A task', 'demo');
    expect(transport.mock.calls[3][1]?.headers).toEqual({ 'Content-Type': 'application/json', 'X-Workspace-Token': 'new-token' });
  });
  it('reports network, schema, and conflict failures rather than success-shaped data', async () => {
    const client = new WorkspaceApi(vi.fn<typeof fetch>().mockRejectedValue(new TypeError('Failed to fetch')));
    await expect(client.list()).rejects.toMatchObject({ code: 'offline' });
    const invalid = new WorkspaceApi(vi.fn<typeof fetch>().mockResolvedValue(Response.json({ unexpected: true })));
    await expect(invalid.get('task')).rejects.toBeInstanceOf(ApiError);
    const conflict = new WorkspaceApi(vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json(bootstrap))
      .mockResolvedValueOnce(Response.json({ code: 'version_conflict', message: 'This version is stale.' }, { status: 409 })));
    await expect(conflict.edit('task', 'artifact', 1, 'body', 'Mine')).rejects.toMatchObject({ code: 'version_conflict', status: 409 });
  });
  it('does not let an older mutation response replace a newer canonical snapshot', () => {
    const old = workspace(); const current = { ...old, eventSequence: 14, revision: 9 };
    expect(newerWorkspace(current, old)).toBe(current);
    expect(newerWorkspace(old, current)).toBe(current);
  });
  it('resets only the selected saved session with the normal token and no automatic retry', async () => {
    const original = { ...workspace(), provider: 'copilot' as const };
    original.agents[0].sessionId = 'first-saved-session';
    original.agents[1].sessionId = 'another-saved-session';
    const updated = structuredClone(original);
    updated.agents[0].sessionId = null;
    updated.revision++;
    updated.eventSequence++;
    const transport = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json(bootstrap))
      .mockResolvedValueOnce(Response.json(updated));
    const client = new WorkspaceApi(transport);
    const result = await client.resetAgentSession(original.id, 'planner');
    expect(transport).toHaveBeenCalledTimes(2);
    expect(transport.mock.calls[1][0]).toBe('/api/workspaces/workspace-1/agents/planner/reset-session');
    expect(transport.mock.calls[1][1]?.method).toBe('POST');
    expect(transport.mock.calls[1][1]?.body).toBe('{}');
    expect(transport.mock.calls[1][1]?.headers).toEqual({
      'Content-Type': 'application/json', 'X-Workspace-Token': bootstrap.csrfToken,
    });
    expect(result.agents[0].sessionId).toBeNull();
    expect(result.agents[1].sessionId).toBe('another-saved-session');
    expect(result.artifacts).toEqual(original.artifacts);
    expect(result.jobs).toEqual(original.jobs);
    expect(result.objective).toBe(original.objective);
  });
  it('explicitly queues clarification refresh with the current form identity and preserves the old spec', async () => {
    const original = { ...workspace(), clarificationId: 'clarify-current', clarification: visualClarificationSpec() };
    const updated = { ...original, jobs: [{ ...original.jobs[0], kind: 'clarify' as const, status: 'queued' as const }] };
    const transport = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json(bootstrap))
      .mockResolvedValueOnce(Response.json(updated));
    const client = new WorkspaceApi(transport);
    const result = await client.regenerateClarification(original.id, original.clarificationId);
    expect(transport).toHaveBeenCalledTimes(2);
    expect(transport.mock.calls[1][0]).toBe('/api/workspaces/workspace-1/clarification/refresh');
    expect(transport.mock.calls[1][1]?.method).toBe('POST');
    expect(transport.mock.calls[1][1]?.headers).toEqual({
      'Content-Type': 'application/json', 'X-Workspace-Token': bootstrap.csrfToken,
    });
    expect(JSON.parse(String(transport.mock.calls[1][1]?.body))).toEqual({ clarificationId: 'clarify-current' });
    expect(result.jobs[0].kind).toBe('clarify');
    expect(result.clarification).toEqual(original.clarification);
  });
});
