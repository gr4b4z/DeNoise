import type { Problem } from '@/api/types';
import { problemMessage } from '@/lib/problems';

export function ProblemBanner({ problem, tone = 'error', onDismiss }: { problem: Problem | null | undefined; tone?: 'error' | 'info'; onDismiss?: () => void }) {
  if (!problem) return null;
  const color = tone === 'error' ? 'var(--sev-critical)' : 'var(--accent)';
  return (
    <div role="alert" className="flex items-start gap-2 rounded-md border px-3 py-2 text-sm" style={{ borderColor: color, background: `color-mix(in srgb, ${color} 8%, var(--surface))` }}>
      <span className="flex-1">
        {problemMessage(problem)}
        {problem.traceId && <span className="ml-2 text-xs text-ink-2 mono">trace {problem.traceId}</span>}
      </span>
      {onDismiss && (
        <button type="button" className="btn btn-sm" onClick={onDismiss} aria-label="Dismiss">
          ×
        </button>
      )}
    </div>
  );
}
