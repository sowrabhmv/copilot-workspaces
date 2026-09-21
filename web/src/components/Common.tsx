import { Icon } from './Icons';

const labels: Record<string, string> = {
  queued: 'Queued', working: 'Working', running: 'Working', needsInput: 'Needs your input',
  review: 'Ready for review', idle: 'Ready', error: 'Needs attention', waiting: 'Waiting',
  complete: 'Complete', completed: 'Complete', failed: 'Failed', cancelled: 'Cancelled',
  cancelling: 'Cancelling', interrupted: 'Interrupted', proposed: 'Proposal', accepted: 'Accepted',
  rejected: 'Rejected', superseded: 'Earlier version',
};
export function StatusBadge({ status }: { status: string }) {
  return <span className={`status-badge status-${status}`}><span className="status-dot" />{labels[status] ?? status}</span>;
}
export function Spinner({ label }: { label?: string }) {
  return <span className="spinner" role={label ? 'status' : undefined} aria-label={label} aria-hidden={label ? undefined : true} />;
}
export function InlineError({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return <div className="inline-error" role="alert">
    <span>{message}</span>
    {onRetry && <button className="text-button" onClick={onRetry}><Icon name="retry" />Try again</button>}
  </div>;
}
export function timeLabel(iso: string) {
  const time = new Date(iso);
  return Number.isNaN(time.getTime()) ? iso : new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' }).format(time);
}
export function dateLabel(iso: string) {
  const time = new Date(iso);
  return Number.isNaN(time.getTime()) ? iso : new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' }).format(time);
}
