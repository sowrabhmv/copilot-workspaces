import { useRef } from 'react';
import { z } from 'zod';
import type { Bootstrap, Provider, ProviderStatus, WorkspaceSummary } from '../domain';
import { useStoredDraft } from '../hooks';
import { CopilotMark, Icon, type IconName } from './Icons';
import { InlineError, Spinner, StatusBadge, dateLabel } from './Common';

const templates: Array<{ title: string; description: string; icon: IconName; prompt: string }> = [
  { title: 'Plan a product launch', description: 'Turn an ambition into milestones, owners, and decisions.', icon: 'calendar',
    prompt: 'Plan a thoughtful product launch. Help me define the audience, milestones, owners, risks, and measures of success.' },
  { title: 'Compare the options', description: 'Explore trade-offs before making the call.', icon: 'table',
    prompt: 'Help me compare options for a decision. Ask what I am choosing between, identify my criteria, and make the trade-offs explicit.' },
  { title: 'Shape a clear brief', description: 'Work together on a focused, decision-ready artifact.', icon: 'document',
    prompt: 'Help me shape a clear project brief. Clarify the problem, audience, desired outcome, and constraints before drafting.' },
];
export const DEMO_OBJECTIVE = 'Plan a thoughtful launch for a new team workspace. Define an audience, launch milestones, owners, and measures of success.';
interface Props {
  bootstrap: Bootstrap | null; provider: ProviderStatus | null; providerLoading: boolean;
  providerError: string | null; startupError: string | null; creating: Provider | null;
  tasks: WorkspaceSummary[]; onCreate: (objective: string, provider: Provider) => void;
  onRecheck: () => void; onOpen: (id: string) => void; onMenu: () => void;
}
export function Home({ bootstrap, provider, providerLoading, providerError, startupError, creating, tasks, onCreate, onRecheck, onOpen, onMenu }: Props) {
  const draft = useStoredDraft('workspaces:v1:new-objective', z.string().max(8000), () => '');
  const input = useRef<HTMLTextAreaElement>(null);
  return <main className="home-main">
    <header className="home-topbar"><button className="mobile-menu icon-button" aria-label="Open task navigation" onClick={onMenu}><Icon name="tasks" /></button>
      <span>Workspace / New task</span><span className="local-pill"><span className="status-dot" />Local, single-user workspace</span>
    </header>
    <div className="home-content">
      <div className="home-intro"><CopilotMark size={48} /><span className="eyebrow">One objective. A shared place to work.</span>
        <h1>What should we get done?</h1>
        <p>Bring a goal. Copilot shapes the work and recruits specialists when useful.<br className="desktop-break" />You guide the work, review the drafts, and make the decisions.</p>
      </div>
      <form className="task-composer" onSubmit={(event) => {
        event.preventDefault();
        if (draft.value.trim() && !creating) onCreate(draft.value.trim(), 'copilot');
      }}>
        <label className="sr-only" htmlFor="new-objective">What would you like to accomplish?</label>
        <textarea id="new-objective" ref={input} rows={3} maxLength={8000}
          placeholder="Describe an outcome, not just a question..."
          value={draft.value} onChange={(event) => draft.setValue(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
              event.preventDefault(); event.currentTarget.form?.requestSubmit();
            }
          }} />
        <div className="composer-bottom"><div className="provider-mode"><Icon name="sparkle" /><strong>Live Copilot</strong>
          <span className="model-auto" title="Copilot chooses the model">Auto</span>
        </div>
          <button className="button-primary start-task" type="submit" disabled={!!creating || !draft.value.trim() || !bootstrap}>
            {creating === 'copilot' ? <Spinner /> : <Icon name="arrow" />}{creating === 'copilot' ? 'Opening workspace...' : 'Start task'}
          </button>
        </div>
      </form>
      <p className="auto-model-note">Copilot chooses the model. You stay focused on the outcome.</p>
      {draft.storageError && <InlineError message={draft.storageError} />}
      {startupError && <InlineError message={startupError} onRetry={onRecheck} />}
      <div className={`provider-notice ${provider?.state === 'ready' ? 'is-ready' : ''}`}>
        <span>{providerLoading ? <Spinner /> : <Icon name={provider?.state === 'ready' ? 'approved' : 'shield'} />}</span>
        <div><strong>{providerLoading ? 'Checking the local provider...' : provider?.state === 'ready' ? 'Copilot is ready' : 'Live provider setup'}</strong>
          <p>{providerError ?? provider?.message ?? 'Connect to the local service to check Copilot. Demo mode is always an explicit choice.'}</p>
          {provider?.setupCommand && <code>{provider.setupCommand}</code>}
        </div>
        <button className="text-button" disabled={providerLoading} onClick={onRecheck}><Icon name="retry" />Recheck</button>
      </div>
      <section className="starting-points" aria-labelledby="starting-points-title">
        <div className="section-heading"><h2 id="starting-points-title">A starting point, if you need one</h2><span>Make it your own</span></div>
        <div className="template-grid">{templates.map((template) => <button className="template-card" key={template.title}
          onClick={() => { draft.setValue(template.prompt); input.current?.focus(); }}>
          <span className="template-top"><span className="template-icon"><Icon name={template.icon} /></span><Icon name="right" /></span>
          <strong>{template.title}</strong><span>{template.description}</span>
        </button>)}</div>
      </section>
      <div className="demo-starter"><div><strong>Explore the experience first</strong>
        <p>Open a clearly labelled demo with deterministic agents. No model call or account access.</p></div>
        <button className="button-secondary" disabled={!!creating || !bootstrap} onClick={() => onCreate(DEMO_OBJECTIVE, 'demo')}>
          {creating === 'demo' ? <Spinner /> : <Icon name="arrow" />}{creating === 'demo' ? 'Opening demo...' : 'Try a demo workspace'}
        </button>
      </div>
      {tasks.length > 0 && <section className="resume-section" aria-labelledby="resume-title">
        <div className="section-heading"><h2 id="resume-title">Pick up where you left off</h2><span>Saved on this device</span></div>
        {tasks.slice(0, 3).map((task) => <button className="resume-row" key={task.id} onClick={() => onOpen(task.id)}>
          <Icon name="document" /><div><strong>{task.title}</strong><span>{task.provider === 'demo' ? 'Demo / ' : ''}{dateLabel(task.updatedAt)} / {task.artifactCount} artifacts</span></div>
          <StatusBadge status={task.status} /><Icon name="right" />
        </button>)}
      </section>}
      <p className="privacy-note"><Icon name="shield" />Your tasks are saved locally. Live Copilot sends task context to the configured model; credentials stay out of the browser.</p>
    </div>
  </main>;
}
