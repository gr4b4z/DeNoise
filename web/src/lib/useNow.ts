import { useEffect, useState } from 'react';

/** The current time, re-read every `intervalMs`, for relative ages that must tick without a re-render trigger. */
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(t);
  }, [intervalMs]);
  return now;
}
