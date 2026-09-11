import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { RealtimeClient, type FreshnessSnapshot } from './sse';

interface RealtimeContextValue {
  snapshot: FreshnessSnapshot;
  /** Episode ids that changed via SSE in the last 600 ms, for the row pulse (08 §0 Motion). */
  pulsing: ReadonlySet<string>;
}

const RealtimeContext = createContext<RealtimeContextValue>({
  snapshot: { state: 'connecting', lastEventAt: null, lastEventId: null, failures: 0 },
  pulsing: new Set(),
});

export function RealtimeProvider({ children, enabled = true }: { children: ReactNode; enabled?: boolean }) {
  const qc = useQueryClient();
  const [snapshot, setSnapshot] = useState<FreshnessSnapshot>({ state: 'connecting', lastEventAt: null, lastEventId: null, failures: 0 });
  const [pulsing, setPulsing] = useState<Set<string>>(new Set());
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());

  useEffect(() => {
    if (!enabled) return;
    const client = new RealtimeClient(qc, {
      onSnapshot: setSnapshot,
      onEpisodeChanged: (id) => {
        setPulsing((prev) => new Set(prev).add(id));
        const existing = timers.current.get(id);
        if (existing) clearTimeout(existing);
        timers.current.set(
          id,
          setTimeout(() => {
            timers.current.delete(id);
            setPulsing((prev) => {
              const next = new Set(prev);
              next.delete(id);
              return next;
            });
          }, 600),
        );
      },
    });
    client.start();
    const pending = timers.current;
    return () => {
      client.stop();
      for (const t of pending.values()) clearTimeout(t);
      pending.clear();
    };
  }, [qc, enabled]);

  const value = useMemo(() => ({ snapshot, pulsing }), [snapshot, pulsing]);
  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>;
}

export function useRealtime(): RealtimeContextValue {
  return useContext(RealtimeContext);
}
