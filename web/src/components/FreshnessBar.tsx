import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useRealtime } from '@/realtime/RealtimeProvider';
import { relative } from '@/lib/time';

/**
 * Global freshness indicator (08 §1): live is quiet, stale (>60 s without an event) is amber, disconnected is a red banner.
 * Announced through aria-live so screen-reader users learn about a lost connection too.
 */
export function FreshnessBar() {
  const { snapshot } = useRealtime();
  const { t } = useTranslation();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 5_000);
    return () => clearInterval(id);
  }, []);

  const tone = snapshot.state === 'live' ? 'live' : snapshot.state === 'stale' ? 'stale' : snapshot.state === 'connecting' ? 'connecting' : 'lost';
  const color = tone === 'stale' ? 'var(--sev-high)' : tone === 'lost' ? 'var(--sev-critical)' : tone === 'live' ? 'var(--sev-low)' : 'var(--ink-2)';
  const label = t(`freshness.${snapshot.state}`);
  const lastEvent = snapshot.lastEventAt ? t('freshness.lastEvent', { when: relative(snapshot.lastEventAt, now) }) : null;

  return (
    <footer
      role="status"
      aria-live="polite"
      data-freshness={snapshot.state}
      className="flex h-7 items-center gap-3 border-t border-line px-3 text-xs text-ink-2"
      style={tone === 'lost' ? { background: 'color-mix(in srgb, var(--sev-critical) 12%, var(--surface))', color: 'var(--ink)' } : undefined}
    >
      <span aria-hidden="true" className="inline-block size-2 rounded-full" style={{ background: color }} />
      <span className={tone === 'lost' ? 'font-semibold' : ''}>{label}</span>
      {lastEvent && <span>· {lastEvent}</span>}
    </footer>
  );
}
