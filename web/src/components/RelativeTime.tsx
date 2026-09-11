import { useEffect, useState } from 'react';
import { local, relative, shortAge, utc } from '@/lib/time';

function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(t);
  }, [intervalMs]);
  return now;
}

/** Age in the column, exact UTC and local time in the tooltip (08 §4). */
export function RelativeTime({ iso, mode = 'short', className }: { iso: string | null | undefined; mode?: 'short' | 'sentence'; className?: string }) {
  const now = useNow(30_000);
  if (!iso) return <span className={className}>—</span>;
  const title = `${utc(iso)} · local ${local(iso)}`;
  return (
    <time dateTime={iso} title={title} className={className}>
      {mode === 'short' ? shortAge(iso, now) : relative(iso, now)}
    </time>
  );
}
