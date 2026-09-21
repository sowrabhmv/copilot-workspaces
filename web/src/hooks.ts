import { useCallback, useEffect, useRef, useState } from 'react';
import { z } from 'zod';
import { api, errorMessage, isAbort, type WorkspaceApi } from './api';
import { EventSchema, newerWorkspace, type Workspace } from './domain';

export type Connection = 'connecting' | 'connected' | 'reconnecting' | 'offline';

export function useWorkspace(id: string | null, onUpdate: (workspace: Workspace) => void, client: WorkspaceApi = api) {
  const [workspace, setWorkspace] = useState<Workspace | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [connection, setConnection] = useState<Connection>('connecting');
  const [reconnect, setReconnect] = useState(0);
  const currentId = useRef(id);
  currentId.current = id;
  const updateRef = useRef(onUpdate);
  updateRef.current = onUpdate;
  const refreshRef = useRef<() => void>(() => {});
  const accept = useCallback((incoming: Workspace) => {
    updateRef.current(incoming);
    if (currentId.current === incoming.id) setWorkspace((previous) => newerWorkspace(previous, incoming));
  }, []);

  useEffect(() => {
    if (!id) { setWorkspace(null); setError(null); setLoading(false); return; }
    let disposed = false;
    let source: EventSource | null = null;
    let refreshing = false;
    let refreshAgain = false;
    let seen = 0;
    const controller = new AbortController();
    setWorkspace((previous) => previous?.id === id ? previous : null);
    setError(null);
    setLoading(true);
    setConnection('connecting');

    async function refresh() {
      if (disposed) return;
      if (refreshing) { refreshAgain = true; return; }
      refreshing = true;
      do {
        refreshAgain = false;
        try {
          const document = await client.get(id!, controller.signal);
          if (disposed) return;
          seen = Math.max(seen, document.eventSequence);
          accept(document);
          setLoading(false);
          setError(null);
          if (!source) connect(document.eventSequence);
        } catch (failure) {
          if (!disposed && !isAbort(failure)) {
            setError(errorMessage(failure));
            setConnection('offline');
            setLoading(false);
          }
        }
      } while (refreshAgain && !disposed);
      refreshing = false;
    }

    function connect(after: number) {
      source = new EventSource(client.eventsUrl(id!, after));
      source.onopen = () => {
        if (disposed) return;
        setConnection('connected');
        void refresh();
      };
      source.onerror = () => { if (!disposed) setConnection('reconnecting'); };
      source.addEventListener('workspace', (event) => {
        if (disposed || !(event instanceof MessageEvent)) return;
        try {
          const checked = EventSchema.safeParse(JSON.parse(String(event.data)));
          if (!checked.success || checked.data.workspaceId !== id) {
            setError('A progress event could not be verified. Refreshing the saved workspace.');
            void refresh();
            return;
          }
          if (checked.data.sequence <= seen) return;
          seen = checked.data.sequence;
          void refresh();
        } catch {
          setError('The progress connection returned an invalid event. Refreshing the saved workspace.');
          void refresh();
        }
      });
    }
    refreshRef.current = () => { void refresh(); };
    void refresh();
    return () => {
      disposed = true;
      controller.abort();
      source?.close();
      refreshRef.current = () => {};
    };
  }, [id, reconnect, client, accept]);

  return {
    workspace: workspace?.id === id ? workspace : null,
    loading, error, connection, accept,
    refresh: useCallback(() => refreshRef.current(), []),
    reconnect: useCallback(() => setReconnect((value) => value + 1), []),
  };
}

export function useStoredDraft<T>(key: string, schema: z.ZodType<T>, create: () => T) {
  const [initial] = useState(() => {
    try {
      const serialized = window.localStorage.getItem(key);
      if (serialized === null) return { value: create(), error: null };
      const parsed = schema.safeParse(JSON.parse(serialized));
      if (parsed.success) return { value: parsed.data, error: null };
      return { value: create(), error: 'The saved draft could not be read. This new draft will replace the invalid copy.' };
    } catch {
      return { value: create(), error: 'Browser draft storage is unavailable. Keep this tab open until your changes are submitted.' };
    }
  });
  const [value, setValue] = useState(initial.value);
  const [storageError, setStorageError] = useState<string | null>(initial.error);
  useEffect(() => {
    try { window.localStorage.setItem(key, JSON.stringify(value)); }
    catch { setStorageError('Your browser could not save this draft. Keep this tab open until it is submitted.'); }
  }, [key, value]);
  return { value, setValue, storageError };
}

function readRoute(): { id: string | null; error: string | null } {
  const match = /^#\/workspaces\/([^/]+)$/.exec(window.location.hash);
  if (!match) return { id: null, error: null };
  try { return { id: decodeURIComponent(match[1]), error: null }; }
  catch { return { id: null, error: 'The workspace address is invalid. Choose a saved task or start a new one.' }; }
}
export function useWorkspaceRoute() {
  const [route, setRoute] = useState(readRoute);
  useEffect(() => {
    const change = () => setRoute(readRoute());
    window.addEventListener('hashchange', change);
    return () => window.removeEventListener('hashchange', change);
  }, []);
  const navigate = useCallback((id: string | null) => {
    window.location.hash = id ? `/workspaces/${encodeURIComponent(id)}` : '/';
    setRoute({ id, error: null });
  }, []);
  return { ...route, navigate };
}

export function useCompact() {
  const [compact, setCompact] = useState(() => window.matchMedia('(max-width: 760px)').matches);
  useEffect(() => {
    const query = window.matchMedia('(max-width: 760px)');
    const change = () => setCompact(query.matches);
    query.addEventListener('change', change);
    return () => query.removeEventListener('change', change);
  }, []);
  return compact;
}
