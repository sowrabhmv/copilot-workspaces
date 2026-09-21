import {
  forwardRef, useEffect, useImperativeHandle, useRef, useState,
  type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode,
} from 'react';
import { errorMessage } from '../api';
import type { CanvasLayout, Point, Viewport } from '../domain';
import { Icon } from './Icons';

export interface CanvasItem {
  id: string; label: string; kind: 'context' | 'agent' | 'artifact';
  position: Point; width: number; content: ReactNode;
}
export interface CanvasHandle { fit: (ids?: string[]) => void }
interface Props {
  items: CanvasItem[]; connections: Array<[string, string]>; initialLayout: CanvasLayout;
  focused: boolean; selectedNode?: string; onSave: (layout: CanvasLayout) => Promise<void>;
}
export const MIN_ZOOM = 0.3;
export const MAX_ZOOM = 2;
export const snap = (value: number) => Math.round(value / 16) * 16;
export const clampZoom = (zoom: number) => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom));
export function zoomAt(viewport: Viewport, screen: Point, zoom: number): Viewport {
  const next = clampZoom(zoom);
  return {
    x: screen.x - (screen.x - viewport.x) * next / viewport.zoom,
    y: screen.y - (screen.y - viewport.y) * next / viewport.zoom,
    zoom: next,
  };
}
const interactive = 'button,input,textarea,select,a,[contenteditable],[data-canvas-interactive],[data-scroll-region]';
interface Drag {
  pointerId: number; start: Point; original: CanvasLayout; nodeId?: string; nodeStart?: Point;
}

export const SpatialCanvas = forwardRef<CanvasHandle, Props>(function SpatialCanvas(
  { items, connections, initialLayout, focused, selectedNode, onSave }, ref,
) {
  const [layout, setLayout] = useState<CanvasLayout>(() => ({
    positions: { ...initialLayout.positions },
    viewport: { ...initialLayout.viewport, zoom: clampZoom(initialLayout.viewport.zoom) },
  }));
  const layoutRef = useRef(layout);
  const savedLayout = useRef(layout);
  const [tool, setTool] = useState<'select' | 'pan'>('select');
  const [dragging, setDragging] = useState(false);
  const [localSelection, setLocalSelection] = useState<string | undefined>(undefined);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [sizes, setSizes] = useState<Record<string, number>>({});
  const viewportElement = useRef<HTMLDivElement>(null);
  const nodeElements = useRef(new Map<string, HTMLDivElement>());
  const drag = useRef<Drag | null>(null);
  const space = useRef(false);
  const queuedSave = useRef<CanvasLayout | null>(null);
  const savingRef = useRef(false);
  const saveRef = useRef(onSave);
  saveRef.current = onSave;
  const wheelTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  async function persist(next: CanvasLayout) {
    queuedSave.current = next;
    if (savingRef.current) return;
    savingRef.current = true;
    setSaving(true);
    setSaveError(null);
    try {
      while (queuedSave.current) {
        const pending = queuedSave.current;
        queuedSave.current = null;
        await saveRef.current(pending);
        savedLayout.current = pending;
      }
    } catch (error) {
      setSaveError(errorMessage(error));
    } finally {
      savingRef.current = false;
      setSaving(false);
      setDirty(savedLayout.current !== layoutRef.current);
    }
  }
  const update = (next: CanvasLayout, save = false) => {
    layoutRef.current = next;
    setLayout(next);
    setDirty(true);
    if (save) void persist(next);
  };
  const positionOf = (item: CanvasItem) => layoutRef.current.positions[item.id] ?? item.position;
  function fit(ids?: string[]) {
    const element = viewportElement.current;
    const targets = items.filter((item) => !ids || ids.includes(item.id));
    if (!element || !targets.length || focused) return;
    const bounds = targets.map((item) => ({ ...positionOf(item), width: item.width, height: sizes[item.id] ?? 260 }));
    const left = Math.min(...bounds.map((item) => item.x));
    const top = Math.min(...bounds.map((item) => item.y));
    const right = Math.max(...bounds.map((item) => item.x + item.width));
    const bottom = Math.max(...bounds.map((item) => item.y + item.height));
    const width = element.clientWidth;
    const height = element.clientHeight;
    const zoom = clampZoom(Math.min((width - 96) / Math.max(1, right - left), (height - 140) / Math.max(1, bottom - top), 1));
    update({ ...layoutRef.current, viewport: {
      x: (width - (right - left) * zoom) / 2 - left * zoom,
      y: (height - 72 - (bottom - top) * zoom) / 2 - top * zoom,
      zoom,
    } }, true);
  }
  useImperativeHandle(ref, () => ({ fit }));

  useEffect(() => {
    const observer = new ResizeObserver((entries) => {
      setSizes((previous) => {
        let next = previous;
        for (const entry of entries) {
          const node = entry.target;
          if (!(node instanceof HTMLDivElement)) continue;
          const id = node.dataset.nodeId;
          if (id && previous[id] !== node.offsetHeight) {
            if (next === previous) next = { ...previous };
            next[id] = node.offsetHeight;
          }
        }
        return next;
      });
    });
    nodeElements.current.forEach((node) => observer.observe(node));
    return () => observer.disconnect();
  }, [items.map((item) => item.id).join('|')]);

  useEffect(() => {
    const element = viewportElement.current;
    if (!element || focused) return;
    const wheel = (event: WheelEvent) => {
      const target = event.target;
      if (!(target instanceof Element)) return;
      if (!event.ctrlKey && !event.metaKey && target.closest(interactive)) return;
      event.preventDefault();
      const previous = layoutRef.current;
      if (event.ctrlKey || event.metaKey) {
        const rect = element.getBoundingClientRect();
        update({ ...previous, viewport: zoomAt(previous.viewport, {
          x: event.clientX - rect.left, y: event.clientY - rect.top,
        }, previous.viewport.zoom * Math.exp(-event.deltaY * 0.002)) });
      } else {
        update({ ...previous, viewport: { ...previous.viewport,
          x: previous.viewport.x - event.deltaX, y: previous.viewport.y - event.deltaY,
        } });
      }
      if (wheelTimer.current) clearTimeout(wheelTimer.current);
      wheelTimer.current = setTimeout(() => {
        wheelTimer.current = null;
        void persist(layoutRef.current);
      }, 250);
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => {
      element.removeEventListener('wheel', wheel);
      if (wheelTimer.current) {
        clearTimeout(wheelTimer.current);
        wheelTimer.current = null;
        void persist(layoutRef.current);
      }
    };
  }, [focused]);

  useEffect(() => {
    const release = () => { space.current = false; };
    window.addEventListener('keyup', release);
    window.addEventListener('blur', release);
    return () => { window.removeEventListener('keyup', release); window.removeEventListener('blur', release); };
  }, []);

  const onDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (focused || (event.button !== 0 && event.button !== 1)) return;
    const target = event.target;
    if (!(target instanceof Element) || target.closest('button,input,textarea,select,a,[data-canvas-interactive]')) return;
    const node = target.closest<HTMLElement>('[data-node-id]');
    const shouldPan = event.button === 1 || tool === 'pan' || space.current;
    if (node && !shouldPan && !target.closest('[data-drag-handle]')) return;
    const id = node?.dataset.nodeId;
    const item = items.find((entry) => entry.id === id);
    if (item && !shouldPan) setLocalSelection(item.id);
    drag.current = {
      pointerId: event.pointerId, start: { x: event.clientX, y: event.clientY },
      original: layoutRef.current,
      ...(!shouldPan && item ? { nodeId: item.id, nodeStart: positionOf(item) } : {}),
    };
    viewportElement.current?.setPointerCapture(event.pointerId);
    setDragging(true);
    event.preventDefault();
  };
  const onMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const current = drag.current;
    if (!current || current.pointerId !== event.pointerId) return;
    const dx = event.clientX - current.start.x;
    const dy = event.clientY - current.start.y;
    if (current.nodeId && current.nodeStart) {
      update({ ...layoutRef.current, positions: { ...layoutRef.current.positions, [current.nodeId]: {
        x: snap(current.nodeStart.x + dx / current.original.viewport.zoom),
        y: snap(current.nodeStart.y + dy / current.original.viewport.zoom),
      } } });
    } else {
      update({ ...layoutRef.current, viewport: { ...current.original.viewport,
        x: current.original.viewport.x + dx, y: current.original.viewport.y + dy,
      } });
    }
  };
  const onUp = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (drag.current?.pointerId !== event.pointerId) return;
    drag.current = null;
    setDragging(false);
    if (viewportElement.current?.hasPointerCapture(event.pointerId)) viewportElement.current.releasePointerCapture(event.pointerId);
    void persist(layoutRef.current);
  };
  const zoomBy = (factor: number) => {
    const element = viewportElement.current;
    if (!element) return;
    update({ ...layoutRef.current, viewport: zoomAt(layoutRef.current.viewport, {
      x: element.clientWidth / 2, y: element.clientHeight / 2,
    }, layoutRef.current.viewport.zoom * factor) }, true);
  };
  const byId = new Map(items.map((item) => [item.id, item]));
  const selected = selectedNode ?? localSelection;
  const { viewport } = layout;
  const gridStyle: CSSProperties = {
    backgroundSize: `${32 * viewport.zoom}px ${32 * viewport.zoom}px`,
    backgroundPosition: `${viewport.x}px ${viewport.y}px`,
  };

  return <div ref={viewportElement}
    className={`spatial-canvas ${focused ? 'is-focused' : ''} ${dragging || tool === 'pan' ? 'is-panning' : ''}`}
    role="region" aria-label={focused ? 'Focused workspace' : 'Spatial workspace'}
    aria-describedby="canvas-instructions" tabIndex={0}
    onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp}
    onKeyDown={(event) => {
      if (focused || (event.target instanceof Element && event.target.closest(interactive))) return;
      const key = event.key.toLowerCase();
      if (key === ' ') { event.preventDefault(); space.current = true; }
      else if (key === 'home') { event.preventDefault(); fit(); }
      else if (key === 'h') setTool('pan');
      else if (key === 'v') setTool('select');
      else if (key === '+' || key === '=') { event.preventDefault(); zoomBy(1.2); }
      else if (key === '-') { event.preventDefault(); zoomBy(1 / 1.2); }
      else if (key === '0') { event.preventDefault(); zoomBy(1 / layoutRef.current.viewport.zoom); }
      else if (key.startsWith('arrow') && selected) {
        const item = byId.get(selected);
        if (!item) return;
        event.preventDefault();
        const point = positionOf(item);
        const step = event.shiftKey ? 80 : 16;
        update({ ...layoutRef.current, positions: { ...layoutRef.current.positions, [selected]: {
          x: point.x + (key === 'arrowright' ? step : key === 'arrowleft' ? -step : 0),
          y: point.y + (key === 'arrowdown' ? step : key === 'arrowup' ? -step : 0),
        } } }, true);
      }
    }}>
    {!focused && <div className="canvas-grid" aria-hidden="true" style={gridStyle} />}
    <div className="canvas-world" style={focused ? undefined : {
      transform: `translate(${viewport.x}px, ${viewport.y}px) scale(${viewport.zoom})`,
    }}>
      {!focused && <svg className="canvas-connections" aria-hidden="true">
        <defs><marker id="connection-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto">
          <path d="M 0 0 L 10 5 L 0 10" fill="currentColor" />
        </marker></defs>
        {connections.map(([fromId, toId]) => {
          const from = byId.get(fromId); const to = byId.get(toId);
          if (!from || !to) return null;
          const a = positionOf(from); const b = positionOf(to);
          const start = { x: a.x + from.width, y: a.y + Math.min(sizes[fromId] ?? 160, 160) / 2 };
          const end = { x: b.x, y: b.y + 75 };
          const bend = Math.max(36, Math.abs(end.x - start.x) * 0.45);
          return <path key={`${fromId}-${toId}`} data-from={fromId} data-to={toId} markerEnd="url(#connection-arrow)"
            d={`M ${start.x} ${start.y} C ${start.x + bend} ${start.y}, ${end.x - bend} ${end.y}, ${end.x} ${end.y}`} />;
        })}
      </svg>}
      {items.map((item) => {
        const point = layout.positions[item.id] ?? item.position;
        return <div key={item.id} data-node-id={item.id}
          ref={(node) => { if (node) nodeElements.current.set(item.id, node); else nodeElements.current.delete(item.id); }}
          className={`canvas-node node-${item.kind} ${selected === item.id ? 'node-selected' : ''}`}
          role="group" aria-label={`${item.label} card`}
          style={focused ? undefined : { left: point.x, top: point.y, width: item.width }}>
          {item.content}
        </div>;
      })}
    </div>
    {!focused && <div className="canvas-controls" data-canvas-interactive>
      <div className="canvas-tools" role="toolbar" aria-label="Canvas tools">
        <button className="icon-button" aria-label="Select tool (V)" aria-pressed={tool === 'select'} onClick={() => setTool('select')}><Icon name="cursor" /></button>
        <button className="icon-button" aria-label="Pan tool (H)" aria-pressed={tool === 'pan'} onClick={() => setTool('pan')}><Icon name="pan" /></button>
        <span className="control-divider" />
        <button className="icon-button" aria-label="Zoom out" onClick={() => zoomBy(1 / 1.2)} disabled={viewport.zoom <= MIN_ZOOM}><Icon name="subtract" /></button>
        <button className="zoom-label" aria-label="Reset zoom to 100 percent" onClick={() => zoomBy(1 / viewport.zoom)}>{Math.round(viewport.zoom * 100)}%</button>
        <button className="icon-button" aria-label="Zoom in" onClick={() => zoomBy(1.2)} disabled={viewport.zoom >= MAX_ZOOM}><Icon name="add" /></button>
        <span className="control-divider" />
        <button className="fit-button" onClick={() => fit()}><Icon name="fit" />Fit</button>
      </div>
      <div className={`layout-save ${saveError ? 'has-error' : ''}`} role={saveError ? 'alert' : 'status'}>
        {saveError ? <button title={saveError} onClick={() => void persist(layoutRef.current)}>Layout not saved. Retry</button> : saving ? 'Saving layout...' : dirty ? 'Layout changes not yet saved' : 'Layout saved locally'}
      </div>
    </div>}
    <p id="canvas-instructions" className="sr-only">Drag card headers to move. Drag empty space or hold Space to pan. Control or Command plus scroll zooms. Home fits all cards. H pans, V selects, and arrow keys move the selected card.</p>
  </div>;
});
