import { describe, expect, it } from 'vitest';
import { clampZoom, snap, zoomAt } from '../src/components/SpatialCanvas';

describe('canvas geometry', () => {
  it('snaps in world coordinates and clamps zoom to usable bounds', () => {
    expect(snap(39)).toBe(32);
    expect(snap(-39)).toBe(-32);
    expect(clampZoom(0.01)).toBe(0.3);
    expect(clampZoom(5)).toBe(2);
  });
  it('keeps the same world point under the zoom pointer', () => {
    const start = { x: 45, y: -23, zoom: 0.8 };
    const pointer = { x: 310, y: 200 };
    const result = zoomAt(start, pointer, 1.6);
    expect((pointer.x - result.x) / result.zoom).toBe((pointer.x - start.x) / start.zoom);
    expect((pointer.y - result.y) / result.zoom).toBe((pointer.y - start.y) / start.zoom);
  });
});
