import { QueryClient } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RealtimeClient, type FreshnessSnapshot } from './sse';

/** A hand-driven EventSource: tests decide when it opens, errors or emits. */
class FakeSource extends EventTarget {
  static instances: FakeSource[] = [];
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;
  readyState = 0;
  onopen: ((e: Event) => void) | null = null;
  onerror: ((e: Event) => void) | null = null;
  closed = false;
  constructor(public url: string) {
    super();
    FakeSource.instances.push(this);
  }
  close() {
    this.closed = true;
    this.readyState = 2;
  }
  open() {
    this.readyState = 1;
    this.onopen?.(new Event('open'));
  }
  fail(gaveUp = false) {
    this.readyState = gaveUp ? 2 : 0;
    this.onerror?.(new Event('error'));
  }
  emit(type: string, data: string, lastEventId = '') {
    this.dispatchEvent(new MessageEvent(type, { data, lastEventId }));
  }
}

describe('RealtimeClient', () => {
  let qc: QueryClient;
  let snapshots: FreshnessSnapshot[];
  let client: RealtimeClient;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal('EventSource', FakeSource);
    FakeSource.instances = [];
    qc = new QueryClient();
    snapshots = [];
    client = new RealtimeClient(qc, {
      createEventSource: (url) => new FakeSource(url) as unknown as EventSource,
      onSnapshot: (s) => snapshots.push(s),
      staleAfterMs: 60_000,
      pollEveryMs: 30_000,
      retryBaseMs: 1_000,
    });
  });

  afterEach(() => {
    client.stop();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('is live after open, stale after 60 s of silence, live again on a heartbeat', () => {
    client.start();
    const source = FakeSource.instances[0]!;
    source.open();
    expect(client.current.state).toBe('live');
    vi.advanceTimersByTime(60_000);
    expect(client.current.state).toBe('stale');
    source.emit('heartbeat', '{"at":"x"}');
    expect(client.current.state).toBe('live');
  });

  it('remembers the last event id and reconnects with it after the browser gives up', () => {
    client.start();
    const first = FakeSource.instances[0]!;
    first.open();
    first.emit('episode.changed', '{"id":"e1","version":1}', '7-42');
    first.fail(true);
    expect(client.current.state).toBe('disconnected');
    vi.advanceTimersByTime(1_000);
    const second = FakeSource.instances[1]!;
    expect(second.url).toContain('lastEventId=7-42');
  });

  it('falls back to polling after three failures and keeps probing the stream', () => {
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    client.start();
    const source = FakeSource.instances[0]!;
    source.fail();
    source.fail();
    expect(client.current.state).toBe('disconnected');
    source.fail();
    expect(client.current.state).toBe('polling');
    expect(source.closed).toBe(true);
    vi.advanceTimersByTime(30_000);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['episodes'] });
    expect(FakeSource.instances.length).toBe(2);
    FakeSource.instances[1]!.open();
    expect(client.current.state).toBe('live');
  });

  it('refreshes the lists when the stream comes back after a failure', () => {
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    client.start();
    const first = FakeSource.instances[0]!;
    first.open();
    invalidate.mockClear();
    first.fail(true);
    vi.advanceTimersByTime(1_000);
    FakeSource.instances[1]!.open();
    expect(client.current.state).toBe('live');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['episodes'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['episode-counts'] });
  });

  it('invalidates everything on resync', () => {
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    client.start();
    const source = FakeSource.instances[0]!;
    source.open();
    source.emit('resync', '{"reason":"gap"}');
    expect(invalidate).toHaveBeenCalledWith();
  });
});
