import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';
import { TextEncoder, TextDecoder } from 'node:util';

vi.stubGlobal('TextEncoder', TextEncoder);
vi.stubGlobal('TextDecoder', TextDecoder);
vi.stubGlobal('ResizeObserver', class {
  observe() {}
  unobserve() {}
  disconnect() {}
});
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(),
  dispatchEvent: () => true,
}));
if (!globalThis.CSS?.escape) {
  vi.stubGlobal('CSS', { escape: (value: string) => value.replace(/[^A-Za-z0-9_-]/g, '\\$&') });
}
afterEach(() => {
  cleanup();
  localStorage.clear();
});
