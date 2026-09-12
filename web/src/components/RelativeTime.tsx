import { local, relative, shortAge, utc } from '@/lib/time';
import { useNow } from '@/lib/useNow';

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
