import { applyEpisodeChanged, parseEpisodeChanged } from './cache';
import type { QueryClient } from '@tanstack/react-query';

const STORAGE_KEY = 'alerthub.sse.lastEventId';

/** The last id survives a reload so the server can replay what a closed tab missed (ADR-12). */
function readStoredId(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function storeId(id: string): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, id);
  } catch {
    // storage unavailable: replay still works within the tab's lifetime
  }
}

export type ConnectionState = 'connecting' | 'live' | 'stale' | 'disconnected' | 'polling';

export interface FreshnessSnapshot {
  state: ConnectionState;
  lastEventAt: number | null;
  lastEventId: string | null;
  failures: number;
}

export interface RealtimeOptions {
  url?: string;
  staleAfterMs?: number;
  pollEveryMs?: number;
  retryBaseMs?: number;
  maxRetriesBeforePolling?: number;
  onSnapshot?: (s: FreshnessSnapshot) => void;
  onEpisodeChanged?: (id: string, outcome: 'patched' | 'ignored' | 'unknown') => void;
  createEventSource?: (url: string) => EventSource;
}

/**
 * One EventSource per tab (08 §5). The browser reconnects on its own and sends `Last-Event-ID`; when it gives up or the
 * stream keeps failing we back off, and after three failures fall back to 30 s polling while probing the stream again.
 */
export class RealtimeClient {
  private source: EventSource | null = null;
  private snapshot: FreshnessSnapshot = { state: 'connecting', lastEventAt: null, lastEventId: null, failures: 0 };
  private staleTimer: ReturnType<typeof setTimeout> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private stopped = false;
  private readonly opts: Required<Omit<RealtimeOptions, 'onSnapshot' | 'onEpisodeChanged' | 'createEventSource'>> & RealtimeOptions;

  constructor(
    private readonly qc: QueryClient,
    options: RealtimeOptions = {},
  ) {
    this.snapshot = { ...this.snapshot, lastEventId: readStoredId() };
    this.opts = {
      url: '/api/v1/events/stream',
      staleAfterMs: 60_000,
      pollEveryMs: 30_000,
      retryBaseMs: 2_000,
      maxRetriesBeforePolling: 3,
      ...options,
    };
  }

  get current(): FreshnessSnapshot {
    return this.snapshot;
  }

  start(): void {
    this.stopped = false;
    this.open();
  }

  stop(): void {
    this.stopped = true;
    this.closeSource();
    this.clearTimers();
  }

  private open(): void {
    if (this.stopped) return;
    this.closeSource();
    const url = this.snapshot.lastEventId ? `${this.opts.url}?lastEventId=${encodeURIComponent(this.snapshot.lastEventId)}` : this.opts.url;
    const factory = this.opts.createEventSource ?? ((u: string) => new EventSource(u, { withCredentials: true }));
    let source: EventSource;
    try {
      source = factory(url);
    } catch {
      this.onFailure();
      return;
    }
    this.source = source;
    this.set({ state: this.snapshot.state === 'polling' ? 'polling' : 'connecting' });

    source.onopen = () => {
      // Back after a failure: the server replays from Last-Event-ID when it knows it, but a reload or a long outage may have
      // lost that id, so a reconnect always refreshes what is on screen. Cheap, and it never shows stale rows as current.
      const wasDown = this.snapshot.failures > 0 || this.snapshot.state === 'polling';
      this.set({ state: 'live', failures: 0, lastEventAt: Date.now() });
      this.stopPolling();
      this.armStale();
      if (wasDown) {
        void this.qc.invalidateQueries({ queryKey: ['episodes'] });
        void this.qc.invalidateQueries({ queryKey: ['episode-counts'] });
        void this.qc.invalidateQueries({ queryKey: ['episode'] });
      }
    };
    source.onerror = () => {
      // readyState CONNECTING = the browser is retrying by itself; CLOSED = it gave up (e.g. a non-2xx response).
      const gaveUp = source.readyState === EventSource.CLOSED;
      this.onFailure(gaveUp);
    };
    source.addEventListener('heartbeat', (e) => this.touch(e));
    source.addEventListener('resync', (e) => {
      this.touch(e);
      void this.qc.invalidateQueries();
    });
    source.addEventListener('episode.changed', (e) => {
      const message = e as MessageEvent<string>;
      this.touch(message);
      const change = parseEpisodeChanged(message.data);
      if (!change) return;
      const outcome = applyEpisodeChanged(this.qc, change);
      this.opts.onEpisodeChanged?.(change.id, outcome);
    });
    for (const type of ['coverage.changed', 'heartbeat.changed', 'hub.health']) {
      source.addEventListener(type, (e) => {
        this.touch(e);
        void this.qc.invalidateQueries({ queryKey: [type.split('.')[0]] });
      });
    }
  }

  private touch(e: MessageEvent): void {
    const lastEventId = e.lastEventId || this.snapshot.lastEventId;
    if (lastEventId && lastEventId !== this.snapshot.lastEventId) storeId(lastEventId);
    this.set({ lastEventAt: Date.now(), lastEventId, state: 'live', failures: 0 });
    this.armStale();
  }

  private onFailure(gaveUp = false): void {
    if (this.stopped) return;
    const failures = this.snapshot.failures + 1;
    if (failures >= this.opts.maxRetriesBeforePolling) {
      this.closeSource();
      this.set({ state: 'polling', failures });
      this.startPolling();
      this.scheduleRetry(this.opts.pollEveryMs);
      return;
    }
    this.set({ state: 'disconnected', failures });
    if (gaveUp) {
      this.closeSource();
      this.scheduleRetry(this.opts.retryBaseMs * 2 ** (failures - 1));
    }
  }

  private scheduleRetry(delay: number): void {
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.open();
    }, delay);
  }

  private startPolling(): void {
    if (this.pollTimer) return;
    this.pollTimer = setInterval(() => {
      void this.qc.invalidateQueries({ queryKey: ['episodes'] });
      void this.qc.invalidateQueries({ queryKey: ['episode-counts'] });
    }, this.opts.pollEveryMs);
  }

  private stopPolling(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  private armStale(): void {
    if (this.staleTimer) clearTimeout(this.staleTimer);
    this.staleTimer = setTimeout(() => {
      if (this.snapshot.state === 'live') this.set({ state: 'stale' });
    }, this.opts.staleAfterMs);
  }

  private closeSource(): void {
    if (this.source) {
      this.source.onopen = null;
      this.source.onerror = null;
      this.source.close();
    }
    this.source = null;
  }

  private clearTimers(): void {
    if (this.staleTimer) clearTimeout(this.staleTimer);
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.staleTimer = null;
    this.retryTimer = null;
    this.stopPolling();
  }

  private set(patch: Partial<FreshnessSnapshot>): void {
    this.snapshot = { ...this.snapshot, ...patch };
    this.opts.onSnapshot?.(this.snapshot);
  }
}
