import type { Severity } from '@/api/types';

const SEVERITY_LABEL: Record<string, string> = {
  critical: 'Critical',
  high: 'High',
  medium: 'Medium',
  low: 'Low',
  info: 'Info',
  unknown: 'Unknown',
};

/** Severity is the only saturated colour in the product; text + a filled dot carry the meaning as well (WCAG 1.4.1). */
export function SeverityBadge({ severity, compact = false }: { severity: string; compact?: boolean }) {
  const key: Severity = (SEVERITY_LABEL[severity] ? severity : 'unknown') as Severity;
  const label = SEVERITY_LABEL[key] ?? 'Unknown';
  return (
    <span className="inline-flex items-center gap-1.5 text-sm" data-severity={key} aria-label={`Severity ${label}`}>
      <span aria-hidden="true" className="inline-block size-2.5 rounded-full" style={key === 'unknown' ? { background: 'var(--sev-unknown)' } : { background: `var(--sev-${key})` }} />
      {!compact && <span className={key === 'critical' ? 'font-semibold' : ''}>{label}</span>}
    </span>
  );
}

const CONDITION: Record<string, { label: string; glyph: string }> = {
  firing: { label: 'Firing', glyph: '▲' },
  resolved: { label: 'Resolved', glyph: '▽' },
  unknown: { label: 'Unknown', glyph: '?' },
  not_applicable: { label: 'N/A', glyph: '–' },
};

/** Condition (what the source last said) is never merged with handling (what people did) — 08 §1. */
export function ConditionBadge({ state }: { state: string }) {
  const c = CONDITION[state] ?? { label: state, glyph: '·' };
  return (
    <span className={`badge ${state === 'unknown' ? 'border-dashed' : ''}`} data-condition={state} title="Condition reported by the source">
      <span aria-hidden="true">{c.glyph}</span>
      {c.label}
    </span>
  );
}

const HANDLING: Record<string, { label: string; glyph: string }> = {
  new: { label: 'New', glyph: '○' },
  acknowledged: { label: 'Acknowledged', glyph: '◐' },
  closed: { label: 'Closed', glyph: '●' },
};

export function HandlingBadge({ state }: { state: string }) {
  const h = HANDLING[state] ?? { label: state, glyph: '·' };
  return (
    <span className="badge" data-handling={state} title="Handling state">
      <span aria-hidden="true">{h.glyph}</span>
      {h.label}
    </span>
  );
}

const EVIDENCE: Record<string, string> = {
  source_recovery: 'Source reported recovery',
  verified_state: 'State verified with the source',
  human: 'Closed by a person',
  none: 'No evidence',
};

export function EvidenceBadge({ evidence }: { evidence: string | null | undefined }) {
  const key = evidence ?? 'none';
  const weak = key === 'none';
  return (
    <span className={`badge ${weak ? 'border-dashed' : ''}`} data-evidence={key} title={EVIDENCE[key] ?? key}>
      <span aria-hidden="true">{weak ? '?' : '✓'}</span>
      {EVIDENCE[key] ?? key}
    </span>
  );
}

const COVERAGE: Record<string, { label: string; tone: 'ok' | 'high' | 'critical' | 'muted' }> = {
  healthy: { label: 'Coverage healthy', tone: 'ok' },
  degraded: { label: 'Coverage degraded', tone: 'high' },
  unavailable: { label: 'Coverage unavailable', tone: 'critical' },
  not_configured: { label: 'Coverage not configured', tone: 'muted' },
  unknown: { label: 'Coverage unknown', tone: 'muted' },
};

/** Shown on every row whose integration is not healthy: uncertainty is exposed, never hidden (08 §1). */
export function CoverageBadge({ state, always = false }: { state: string; always?: boolean }) {
  const c = COVERAGE[state] ?? { label: 'Coverage unknown', tone: 'muted' as const };
  if (state === 'healthy' && !always) return null;
  const color = c.tone === 'high' ? 'var(--sev-high)' : c.tone === 'critical' ? 'var(--sev-critical)' : 'var(--ink-2)';
  return (
    <span className="badge" data-coverage={state} title={c.label} style={{ color, borderColor: c.tone === 'muted' || c.tone === 'ok' ? undefined : color }}>
      <span aria-hidden="true">{c.tone === 'ok' ? '✓' : '⚠'}</span>
      {c.label}
    </span>
  );
}
