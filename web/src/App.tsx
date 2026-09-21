import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage } from './api';
import { summarize, type Bootstrap, type Provider, type ProviderStatus, type Workspace, type WorkspaceSummary } from './domain';
import { useWorkspace, useWorkspaceRoute } from './hooks';
import { Home } from './components/Home';
import { WorkspaceScreen } from './components/WorkspaceScreen';
import { CopilotMark, Icon } from './components/Icons';
import { InlineError, Spinner, StatusBadge, dateLabel } from './components/Common';

export default function App() {
  const route = useWorkspaceRoute();
  const [bootstrap, setBootstrap] = useState<Bootstrap | null>(null);
  const [provider, setProvider] = useState<ProviderStatus | null>(null);
  const [tasks, setTasks] = useState<WorkspaceSummary[]>([]);
  const [providerLoading, setProviderLoading] = useState(true);
  const [tasksLoading, setTasksLoading] = useState(true);
  const [providerError, setProviderError] = useState<string | null>(null);
  const [tasksError, setTasksError] = useState<string | null>(null);
  const [startupError, setStartupError] = useState<string | null>(null);
  const [createError, setCreateError] = useState<string | null>(null);
  const [creating, setCreating] = useState<Provider | null>(null);
  const [navigationOpen, setNavigationOpen] = useState(false);
  const [search, setSearch] = useState('');

  const refreshTasks = useCallback(async () => {
    setTasksLoading(true);
    try { setTasks(await api.list()); setTasksError(null); }
    catch (error) { setTasksError(errorMessage(error)); }
    finally { setTasksLoading(false); }
  }, []);
  const startup = useCallback(async () => {
    setProviderLoading(true);
    await Promise.all([
      api.bootstrap().then((value) => { setBootstrap(value); setStartupError(null); })
        .catch((error: unknown) => setStartupError(errorMessage(error))),
      api.provider().then((value) => { setProvider(value); setProviderError(null); })
        .catch((error: unknown) => setProviderError(errorMessage(error))),
      refreshTasks(),
    ]);
    setProviderLoading(false);
  }, [refreshTasks]);
  useEffect(() => { void startup(); }, [startup]);
  useEffect(() => { if (!route.id) void refreshTasks(); }, [route.id, refreshTasks]);

  const mergeDocument = useCallback((document: Workspace) => {
    const summary = summarize(document);
    setTasks((previous) => {
      const existing = previous.find((task) => task.id === summary.id);
      if (existing && new Date(existing.updatedAt) > new Date(summary.updatedAt)) return previous;
      return [summary, ...previous.filter((task) => task.id !== summary.id)]
        .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());
    });
  }, []);
  const controller = useWorkspace(route.id, mergeDocument);
  const navigate = (id: string | null) => { route.navigate(id); setNavigationOpen(false); setCreateError(null); };
  const create = async (objective: string, mode: Provider) => {
    if (creating) return;
    setCreating(mode); setCreateError(null);
    try {
      const document = await api.create(objective, mode);
      mergeDocument(document); navigate(document.id);
    } catch (error) { setCreateError(errorMessage(error)); }
    finally { setCreating(null); }
  };
  const visibleTasks = tasks.filter((task) => `${task.title} ${task.objective}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));

  return <div className="app-shell">
    <a className="skip-link" href="#main-content" onClick={(event) => {
      event.preventDefault(); document.getElementById('main-content')?.focus();
    }}>Skip to workspace</a>
    <aside className={`app-sidebar ${navigationOpen ? 'is-open' : ''}`} aria-label="Task navigation">
      <div className="app-brand"><CopilotMark /><strong>Workspaces</strong>
        <button className="mobile-menu icon-button" aria-label="Close task navigation" onClick={() => setNavigationOpen(false)}><Icon name="dismiss" /></button>
      </div>
      <button className={`new-task-button ${route.id ? '' : 'is-current'}`} onClick={() => navigate(null)}><Icon name="add" />New task</button>
      <div className="sidebar-heading"><h2>Your workspaces</h2><button className="icon-button" aria-label="Refresh task history"
        disabled={tasksLoading} onClick={() => void refreshTasks()}>{tasksLoading ? <Spinner /> : <Icon name="retry" />}</button></div>
      {tasks.length > 4 && <label className="task-search"><span className="sr-only">Find a workspace</span>
        <input placeholder="Find a workspace" value={search} onChange={(event) => setSearch(event.target.value)} /></label>}
      {tasksError && <InlineError message={tasksError} onRetry={() => void refreshTasks()} />}
      <nav className="task-history" aria-label="Saved workspaces">
        {visibleTasks.map((task) => <button key={task.id}
          className={`task-history-item ${route.id === task.id ? 'is-current' : ''}`}
          aria-current={route.id === task.id ? 'page' : undefined} onClick={() => navigate(task.id)}>
          <span className="task-item-icon"><Icon name="document" /></span>
          <span className="task-item-copy"><strong>{task.title}</strong><span>{task.provider === 'demo' && 'Demo / '}{dateLabel(task.updatedAt)}</span>
            <StatusBadge status={task.status} /></span>
          {task.activeJobs > 0 && <span className="task-active-count" aria-label={`${task.activeJobs} active jobs`}>{task.activeJobs}</span>}
        </button>)}
        {!tasksLoading && !tasksError && !visibleTasks.length && <div className="empty-history">
          <Icon name="history" /><p>{search ? 'No matching workspaces.' : 'Your work starts here.'}</p>
          {!search && <span>Create a task and return to it anytime.</span>}
        </div>}
      </nav>
      <div className="sidebar-footer"><Icon name="shield" /><div><strong>Human-led, locally saved</strong><span>Review every proposal. Keep the final say.</span></div></div>
    </aside>
    <div id="main-content" className="main-content" tabIndex={-1}>
      {!route.id ? <Home bootstrap={bootstrap} provider={provider} providerLoading={providerLoading}
        providerError={providerError} startupError={createError ?? startupError ?? route.error}
        creating={creating} tasks={tasks} onCreate={(objective, mode) => void create(objective, mode)}
        onRecheck={() => void startup()} onOpen={navigate} onMenu={() => setNavigationOpen(true)} />
        : controller.workspace ? <WorkspaceScreen key={controller.workspace.id} workspace={controller.workspace}
          bootstrap={bootstrap} connection={controller.connection} error={controller.error}
          onDocument={controller.accept} onRefresh={controller.refresh} onReconnect={controller.reconnect}
          onHome={() => navigate(null)} onMenu={() => setNavigationOpen(true)} />
          : <main className="workspace-loading"><button className="text-button" onClick={() => navigate(null)}><Icon name="context" />Back to your workspaces</button>
            {controller.loading ? <div className="loading-message"><Spinner label="Loading saved workspace" /><h1>Opening your workspace</h1><p>Restoring saved artifacts, directions, and layout.</p></div>
              : <InlineError message={controller.error ?? 'The workspace could not be loaded.'} onRetry={controller.reconnect} />}
          </main>}
    </div>
  </div>;
}
