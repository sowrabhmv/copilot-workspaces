import { z } from 'zod';

const Id = z.string().min(1).max(200);
const Timestamp = z.string().min(1);
export const ProviderId = z.enum(['copilot', 'demo']);
export type Provider = z.infer<typeof ProviderId>;
export const WorkspaceStatus = z.enum(['queued', 'working', 'needsInput', 'review', 'idle', 'error']);
export const JobStatus = z.enum(['queued', 'running', 'cancelling', 'completed', 'failed', 'cancelled', 'interrupted']);

export const BootstrapSchema = z.object({
  csrfToken: z.string().min(1), model: z.string(), reasoningEffort: z.string().nullable(),
  providers: z.array(z.object({ id: ProviderId, label: z.string() })), version: z.string(),
});
export type Bootstrap = z.infer<typeof BootstrapSchema>;
export const ProviderStatusSchema = z.object({
  id: z.string(), state: z.enum(['ready', 'setupRequired', 'unavailable', 'unsupported']),
  message: z.string(), model: z.string(), reasoningEffort: z.string().nullable(),
  sdkVersion: z.string().nullable().optional(), cliVersion: z.string().nullable().optional(),
  setupCommand: z.string().nullable().optional(),
});
export type ProviderStatus = z.infer<typeof ProviderStatusSchema>;
export const SummarySchema = z.object({
  id: Id, title: z.string(), objective: z.string(), provider: ProviderId, status: WorkspaceStatus,
  updatedAt: Timestamp, activeJobs: z.number().int(), pendingFeedback: z.number().int(),
  pendingReviews: z.number().int(), artifactCount: z.number().int(),
});
export type WorkspaceSummary = z.infer<typeof SummarySchema>;
export const PositionSchema = z.object({ x: z.number().finite(), y: z.number().finite() });
export const ViewportSchema = z.object({
  x: z.number().finite(), y: z.number().finite(), zoom: z.number().finite().positive(),
});
export const LayoutSchema = z.object({
  positions: z.record(z.string(), PositionSchema), viewport: ViewportSchema,
});
export type Point = z.infer<typeof PositionSchema>;
export type Viewport = z.infer<typeof ViewportSchema>;
export type CanvasLayout = z.infer<typeof LayoutSchema>;
export const AgentSchema = z.object({
  id: Id, name: z.string(), role: z.string(), assigned: z.boolean().default(true),
  status: z.enum(['idle', 'working', 'waiting', 'complete', 'error', 'cancelled']),
  message: z.string(), sessionId: z.string().nullable(), activeJobId: z.string().nullable(),
});
export type Agent = z.infer<typeof AgentSchema>;
export const RevisionSchema = z.object({
  revision: z.number().int(), baseRevision: z.number().int().nullable(),
  spec: z.unknown(), status: z.enum(['proposed', 'accepted', 'rejected', 'superseded']),
  source: z.string(), jobId: z.string().nullable(), review: z.string().nullable(),
  conflict: z.boolean(), createdAt: Timestamp,
});
export type ArtifactRevision = z.infer<typeof RevisionSchema>;
export const ArtifactSchema = z.object({
  id: Id, title: z.string(), currentRevision: z.number().int(),
  acceptedRevision: z.number().int().nullable(), revisions: z.array(RevisionSchema),
});
export type Artifact = z.infer<typeof ArtifactSchema>;
export const JobSchema = z.object({
  id: Id, kind: z.enum(['initial', 'answers', 'feedback', 'clarify']), order: z.number().int(), status: JobStatus,
  message: z.string(), feedbackId: z.string().nullable(), retryOf: z.string().nullable(),
  errorCode: z.string().nullable(), createdAt: Timestamp,
  startedAt: Timestamp.nullable(), completedAt: Timestamp.nullable(),
  baseRevisions: z.record(z.string(), z.number().int()),
});
export type Job = z.infer<typeof JobSchema>;
export const FeedbackSchema = z.object({
  id: Id, clientRequestId: z.string(), jobId: Id, text: z.string(),
  artifactId: z.string().nullable(), elementId: z.string().nullable(), revision: z.number().int().nullable(),
  targetLabel: z.string(), quotedText: z.string().nullable(),
  status: z.string(), createdAt: Timestamp,
});
export type Feedback = z.infer<typeof FeedbackSchema>;
export const DecisionSchema = z.object({
  id: Id, artifactId: Id, revision: z.number().int(), kind: z.string(), createdAt: Timestamp,
});
export const WorkspaceSchema = z.object({
  id: Id, title: z.string(), objective: z.string(), provider: ProviderId, status: WorkspaceStatus,
  summary: z.string(), createdAt: Timestamp, updatedAt: Timestamp,
  revision: z.number().int(), eventSequence: z.number().int().nonnegative(),
  clarificationId: z.string().nullable(), clarification: z.unknown().nullable(),
  answers: z.record(z.string(), z.string()), agents: z.array(AgentSchema),
  artifacts: z.array(ArtifactSchema), jobs: z.array(JobSchema), feedback: z.array(FeedbackSchema),
  decisions: z.array(DecisionSchema), layout: LayoutSchema,
});
export type Workspace = z.infer<typeof WorkspaceSchema>;
export const EventSchema = z.object({
  sequence: z.number().int().nonnegative(), workspaceId: Id, type: z.string(), message: z.string(),
  jobId: z.string().nullable(), agentId: z.string().nullable(), createdAt: Timestamp,
});
export type WorkspaceEvent = z.infer<typeof EventSchema>;
export interface Selection { artifactId: string; revision: number; elementId: string }
export interface FeedbackRequest {
  text: string; clientRequestId: string; artifactId?: string; elementId?: string; revision?: number;
}
export const activeJob = (job: Job) => ['queued', 'running', 'cancelling'].includes(job.status);
export function summarize(workspace: Workspace): WorkspaceSummary {
  return {
    id: workspace.id, title: workspace.title, objective: workspace.objective, provider: workspace.provider,
    status: workspace.status, updatedAt: workspace.updatedAt, activeJobs: workspace.jobs.filter(activeJob).length,
    pendingFeedback: workspace.feedback.filter((item) => ['queued', 'running', 'cancelling', 'review'].includes(item.status)).length,
    pendingReviews: workspace.artifacts.reduce((count, artifact) =>
      count + artifact.revisions.filter((revision) => revision.status === 'proposed').length, 0),
    artifactCount: workspace.artifacts.length,
  };
}

export function newerWorkspace(previous: Workspace | null, incoming: Workspace): Workspace {
  if (!previous || previous.id !== incoming.id) return incoming;
  return incoming.eventSequence >= previous.eventSequence && incoming.revision >= previous.revision
    ? incoming : previous;
}
