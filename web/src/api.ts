import { z } from 'zod';
import {
  BootstrapSchema, ProviderStatusSchema, SummarySchema, WorkspaceSchema,
  type CanvasLayout, type FeedbackRequest, type Provider,
} from './domain';

const ErrorSchema = z.object({ code: z.string(), message: z.string() });
export class ApiError extends Error {
  constructor(readonly code: string, message: string, readonly status = 0) {
    super(message);
    this.name = 'ApiError';
  }
}
export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'An unexpected error occurred. Please try again.';
}
export function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}
export type ActionResult = { ok: true } | { ok: false; message: string; conflict: boolean };

export class WorkspaceApi {
  private token: string | null = null;
  private bootstrapPromise: Promise<z.infer<typeof BootstrapSchema>> | null = null;

  constructor(private readonly transport: typeof fetch = (...args) => fetch(...args)) {}

  async bootstrap() {
    if (!this.bootstrapPromise) {
      this.bootstrapPromise = this.request('/bootstrap', BootstrapSchema).then((value) => {
        this.token = value.csrfToken;
        return value;
      }).finally(() => { this.bootstrapPromise = null; });
    }
    return this.bootstrapPromise;
  }

  private async request<T>(
    path: string, schema: z.ZodType<T>, method: 'GET' | 'POST' | 'PUT' = 'GET',
    body?: unknown, signal?: AbortSignal,
  ): Promise<T> {
    const mutation = method !== 'GET';
    if (mutation && !this.token) await this.bootstrap();
    let response: Response;
    try {
      response = await this.transport(`/api${path}`, {
        method, signal, credentials: 'same-origin',
        headers: mutation
          ? { 'Content-Type': 'application/json', 'X-Workspace-Token': this.token ?? '' }
          : { Accept: 'application/json' },
        ...(mutation ? { body: JSON.stringify(body ?? {}) } : {}),
      });
    } catch (error) {
      if (isAbort(error)) throw error;
      throw new ApiError('offline', 'Cannot reach the local service at 127.0.0.1:5080. Your drafts are kept; reconnect when the service is available.');
    }
    let payload: unknown;
    try {
      payload = await response.json();
    } catch (error) {
      if (isAbort(error)) throw error;
      throw new ApiError('invalid_response', `The local service returned an unreadable response (HTTP ${response.status}).`, response.status);
    }
    if (!response.ok) {
      const parsed = ErrorSchema.safeParse(payload);
      if (mutation && response.status === 403) {
        this.token = null;
        // Refresh the process-local token, never replay a mutation implicitly.
        try { await this.bootstrap(); }
        catch (error) {
          throw new ApiError('token_refresh_failed', `The local service token changed and could not be renewed. ${errorMessage(error)}`, 403);
        }
      }
      throw new ApiError(parsed.success ? parsed.data.code : 'request_failed',
        parsed.success ? parsed.data.message : `Request failed (HTTP ${response.status}).`, response.status);
    }
    const checked = schema.safeParse(payload);
    if (!checked.success) {
      throw new ApiError('invalid_response', 'The local service returned data that does not match the workspace contract. Please refresh; existing content is kept.');
    }
    return checked.data;
  }

  provider() { return this.request('/provider', ProviderStatusSchema); }
  list() { return this.request('/workspaces', z.array(SummarySchema)); }
  get(id: string, signal?: AbortSignal) {
    return this.request(`/workspaces/${encodeURIComponent(id)}`, WorkspaceSchema, 'GET', undefined, signal);
  }
  create(objective: string, provider: Provider) {
    return this.request('/workspaces', WorkspaceSchema, 'POST', { objective, provider });
  }
  answers(id: string, clarificationId: string, answers: Record<string, string>) {
    return this.request(`/workspaces/${encodeURIComponent(id)}/answers`, WorkspaceSchema, 'POST', { clarificationId, answers });
  }
  regenerateClarification(id: string, clarificationId: string) {
    return this.request(`/workspaces/${encodeURIComponent(id)}/clarification/refresh`,
      WorkspaceSchema, 'POST', { clarificationId });
  }
  feedback(id: string, feedback: FeedbackRequest) {
    return this.request(`/workspaces/${encodeURIComponent(id)}/feedback`, WorkspaceSchema, 'POST', feedback);
  }
  job(id: string, jobId: string, action: 'cancel' | 'retry') {
    return this.request(`/workspaces/${encodeURIComponent(id)}/jobs/${encodeURIComponent(jobId)}/${action}`, WorkspaceSchema, 'POST', {});
  }
  resetAgentSession(id: string, agentId: string) {
    return this.request(`/workspaces/${encodeURIComponent(id)}/agents/${encodeURIComponent(agentId)}/reset-session`,
      WorkspaceSchema, 'POST', {});
  }
  review(id: string, artifactId: string, revision: number, decision: 'accept' | 'reject') {
    return this.request(`/workspaces/${encodeURIComponent(id)}/artifacts/${encodeURIComponent(artifactId)}/review`,
      WorkspaceSchema, 'POST', { revision, decision });
  }
  edit(id: string, artifactId: string, revision: number, elementId: string, text: string) {
    return this.request(`/workspaces/${encodeURIComponent(id)}/artifacts/${encodeURIComponent(artifactId)}/edit`,
      WorkspaceSchema, 'POST', { revision, elementId, text });
  }
  layout(id: string, layout: CanvasLayout) {
    return this.request(`/workspaces/${encodeURIComponent(id)}/layout`, WorkspaceSchema, 'PUT', layout);
  }
  eventsUrl(id: string, after: number) {
    return `/api/workspaces/${encodeURIComponent(id)}/events?after=${encodeURIComponent(after)}`;
  }
}

export const api = new WorkspaceApi();
