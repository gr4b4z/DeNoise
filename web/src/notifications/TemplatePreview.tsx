import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { episodesQuery } from '@/episodes/queries';
import { prettyBody, templateRenderQuery } from '@/notifications/queries';

/**
 * "Render against …" selector shared by the destination form and the template editor (08 §3.7b): the built-in sample, or a
 * real recent episode from the queue so the operator sees what a receiver would get today.
 */
export function SampleSourcePicker({ value, onChange }: { value: string; onChange: (episodeId: string) => void }) {
  const { t } = useTranslation();
  const recent = useInfiniteQuery(episodesQuery({ view: 'needsAttention' }, 20));
  const items = recent.data?.pages[0]?.items ?? [];
  return (
    <label className="flex items-center gap-2 text-xs text-ink-2">
      {t('templates.renderAgainst')}
      <select className="input h-7 w-auto text-sm" value={value} onChange={(e) => onChange(e.target.value)} aria-label={t('templates.renderAgainst')}>
        <option value="">{t('templates.sample')}</option>
        {items.map((e) => (
          <option key={e.id} value={e.id}>
            {e.severity} · {(e.summary ?? e.id).slice(0, 60)}
          </option>
        ))}
      </select>
    </label>
  );
}

/** Rendered output of a stored template version, pretty-printed for JSON; errors are shown where the body would be. */
export function StoredTemplatePreview({ templateId, version, episodeId, className }: { templateId: string; version: number; episodeId?: string; className?: string }) {
  const render = useQuery(templateRenderQuery(templateId, version, episodeId));
  return <PreviewPane body={render.data ? prettyBody(render.data.body, render.data.contentType) : null} contentType={render.data?.contentType} problem={render.isError ? (render.error as unknown as Problem) : null} loading={render.isPending} className={className} />;
}

export function PreviewPane({ body, contentType, problem, loading, className }: { body: string | null; contentType?: string; problem?: Problem | null; loading?: boolean; className?: string }) {
  const { t } = useTranslation();
  return (
    <div className={`rounded-md border border-line bg-surface-2 ${className ?? ''}`} data-testid="template-preview">
      <div className="flex h-7 items-center justify-between border-b border-line px-2 text-xs text-ink-2">
        <span>{t('templates.preview')}</span>
        {contentType && <span className="mono">{contentType}</span>}
      </div>
      {problem ? (
        <p className="m-0 p-2 text-sm" style={{ color: 'var(--sev-critical)' }} role="alert">
          {problem.detail ?? problem.title ?? t('templates.renderFailed')}
          {problem.errors?.map((e) => (
            <span key={e.path} className="mono block text-xs">
              {e.path}: {e.message}
            </span>
          ))}
        </p>
      ) : (
        <pre tabIndex={0} className="mono m-0 max-h-[420px] overflow-auto p-2 text-xs leading-snug whitespace-pre-wrap break-words">{loading ? '…' : body}</pre>
      )}
    </div>
  );
}
