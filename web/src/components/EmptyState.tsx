export function EmptyState({ variant, title, detail }: { variant: 'healthy' | 'coverageWarning' | 'filtered'; title: string; detail?: string }) {
  const accent = variant === 'coverageWarning' ? 'var(--sev-high)' : variant === 'healthy' ? 'var(--sev-low)' : 'var(--ink-2)';
  return (
    <div className="flex flex-col items-start gap-1 px-6 py-10" data-empty={variant}>
      <div className="flex items-center gap-2 text-md">
        <span aria-hidden="true" className="inline-block size-2.5 rounded-full" style={{ background: accent }} />
        <span className="font-semibold">{title}</span>
      </div>
      {detail && <p className="m-0 text-sm text-ink-2">{detail}</p>}
    </div>
  );
}
