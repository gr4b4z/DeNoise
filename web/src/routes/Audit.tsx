import { useInfiniteQuery } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Fragment, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { utc } from '@/lib/time';
import { AUDIT_TARGET_TYPES, auditQuery, auditTargetLink, policyKindFromAction, type AuditEntry, type AuditFilters } from '@/policies/queries';

export type AuditSearch = AuditFilters;

/** 06 §4 `/audit`: who did what to which target, newest first, with the filters in the URL and before/after on demand. */
export function AuditPage() {
  const { t } = useTranslation();
  const search = useSearch({ strict: false }) as unknown as AuditSearch;
  const navigate = useNavigate();
  const filters: AuditFilters = useMemo(
    () => ({ target: search.target, targetType: search.targetType, actor: search.actor, action: search.action, from: search.from, to: search.to }),
    [search.target, search.targetType, search.actor, search.action, search.from, search.to],
  );
  const audit = useInfiniteQuery(auditQuery(filters));
  const items = useMemo(() => audit.data?.pages.flatMap((p) => p.items) ?? [], [audit.data]);
  const [open, setOpen] = useState<string | null>(null);
  const update = (patch: Partial<AuditSearch>) => void navigate({ to: '/audit', search: (prev: AuditSearch) => ({ ...prev, ...patch }), replace: true });
  const filtered = Object.values(filters).some((v) => !!v);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <form
        className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2"
        onSubmit={(e) => {
          e.preventDefault();
          const data = new FormData(e.currentTarget);
          const text = (key: string) => ((data.get(key) as string) || '').trim() || undefined;
          const instant = (key: string) => {
            const v = text(key);
            return v ? new Date(v).toISOString() : undefined;
          };
          update({ target: text('target'), actor: text('actor'), action: text('action'), from: instant('from'), to: instant('to') });
        }}
      >
        <h1 className="mr-2 text-md">{t('audit.title')}</h1>
        <select className="input h-7 w-auto text-sm" aria-label={t('audit.targetType')} value={search.targetType ?? ''} onChange={(e) => update({ targetType: e.target.value || undefined })} data-testid="audit-target-type">
          <option value="">{t('audit.anyType')}</option>
          {AUDIT_TARGET_TYPES.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </select>
        <input name="target" className="input mono h-7 w-56 text-xs" placeholder={t('audit.target')} aria-label={t('audit.target')} defaultValue={search.target ?? ''} key={`target:${search.target ?? ''}`} />
        <input name="actor" className="input h-7 w-32 text-sm" placeholder={t('audit.actor')} aria-label={t('audit.actor')} defaultValue={search.actor ?? ''} key={`actor:${search.actor ?? ''}`} />
        <input name="action" className="input mono h-7 w-40 text-xs" placeholder={t('audit.action')} aria-label={t('audit.action')} defaultValue={search.action ?? ''} key={`action:${search.action ?? ''}`} data-testid="audit-action" />
        <input name="from" type="datetime-local" className="input h-7 w-auto text-xs" aria-label={t('audit.from')} defaultValue={toLocalInput(search.from)} key={`from:${search.from ?? ''}`} />
        <input name="to" type="datetime-local" className="input h-7 w-auto text-xs" aria-label={t('audit.to')} defaultValue={toLocalInput(search.to)} key={`to:${search.to ?? ''}`} />
        <button type="submit" className="btn btn-sm" data-testid="audit-apply">
          {t('audit.filter')}
        </button>
        {filtered && (
          <Link to="/audit" search={{}} className="btn btn-sm hover:no-underline">
            {t('audit.clear')}
          </Link>
        )}
        <span className="ml-auto text-xs text-ink-2" aria-live="polite">
          {audit.isFetching ? '…' : t('audit.shown', { count: items.length })}
        </span>
      </form>
      {audit.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={audit.error as unknown as Problem} />
        </div>
      )}
      {audit.data && items.length === 0 ? (
        <EmptyState variant="filtered" title={filtered ? t('audit.emptyFiltered') : t('audit.empty')} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto bg-surface" data-testid="audit-table">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('audit.when')}</th>
                <th className="font-normal">{t('audit.actor')}</th>
                <th className="font-normal">{t('audit.action')}</th>
                <th className="font-normal">{t('audit.targetCol')}</th>
                <th className="font-normal max-lg:hidden">{t('audit.scope')}</th>
                <th className="pr-3 font-normal">{t('audit.reason')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((a) => (
                <Fragment key={a.id}>
                  <tr className="h-9 border-b border-line hover:bg-surface-2" data-audit-id={a.id} data-action={a.action}>
                    <td className="pl-3 text-ink-2" title={utc(a.at)}>
                      <RelativeTime iso={a.at} mode="sentence" />
                    </td>
                    <td>
                      <button type="button" className="text-ink hover:underline" onClick={() => update({ actor: a.actorDisplay ?? a.actorId })} title={`${a.actorType} ${a.actorId}`}>
                        {a.actorDisplay ?? a.actorId}
                      </button>
                    </td>
                    <td className="mono text-xs">
                      <button type="button" className="hover:underline" onClick={() => update({ action: a.action })}>
                        {a.action}
                      </button>
                    </td>
                    <td className="text-xs">
                      <TargetCell entry={a} />
                    </td>
                    <td className="mono text-xs text-ink-2 max-lg:hidden">{a.accessScope ?? '—'}</td>
                    <td className="pr-3 text-ink-2">
                      <span className="flex items-center gap-2">
                        <span className="min-w-0 flex-1 truncate">{a.reason ?? ''}</span>
                        {(a.before != null || a.after != null) && (
                          <button type="button" className="btn btn-sm" aria-expanded={open === a.id} onClick={() => setOpen(open === a.id ? null : a.id)} data-testid="audit-details">
                            {open === a.id ? t('audit.hide') : t('audit.details')}
                          </button>
                        )}
                      </span>
                    </td>
                  </tr>
                  {open === a.id && (
                    <tr className="border-b border-line bg-surface-2">
                      <td colSpan={6} className="px-3 py-2">
                        <div className="grid gap-2 lg:grid-cols-2">
                          <JsonBlock label={t('audit.before')} value={a.before} />
                          <JsonBlock label={t('audit.after')} value={a.after} />
                        </div>
                        <p className="m-0 mt-1 text-xs text-ink-2">
                          {t('audit.correlation')} <span className="mono">{a.correlationId}</span>
                          {a.requestIp && (
                            <>
                              {' · '}
                              {t('audit.ip')} <span className="mono">{a.requestIp}</span>
                            </>
                          )}
                        </p>
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
          {audit.hasNextPage && (
            <div className="p-3">
              <button type="button" className="btn btn-sm" onClick={() => void audit.fetchNextPage()} disabled={audit.isFetchingNextPage} data-testid="audit-more">
                {t('audit.more')}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function TargetCell({ entry }: { entry: AuditEntry }) {
  const kind = entry.targetType === 'policy' ? policyKindFromAction(entry.action) : null;
  const short = <span className="mono">{entry.targetId.length > 12 ? `${entry.targetId.slice(0, 8)}…` : entry.targetId}</span>;
  if (kind) {
    return (
      <span>
        <span className="text-ink-2">{entry.targetType} </span>
        <Link to="/policies/$kind/$id" params={{ kind, id: entry.targetId }} className="text-ink hover:underline">
          {short}
        </Link>
      </span>
    );
  }
  const link = auditTargetLink(entry.targetType, entry.targetId);
  return (
    <span title={entry.targetId}>
      <span className="text-ink-2">{entry.targetType} </span>
      {link ? (
        <Link to={link.to} params={link.params} className="text-ink hover:underline">
          {short}
        </Link>
      ) : (
        short
      )}
    </span>
  );
}

function JsonBlock({ label, value }: { label: string; value: unknown }) {
  return (
    <div className="rounded-md border border-line">
      <div className="flex h-6 items-center border-b border-line px-2 text-xs text-ink-2">{label}</div>
      <pre tabIndex={0} className="mono m-0 max-h-64 overflow-auto p-2 text-xs leading-snug">
        {value === null || value === undefined ? '—' : JSON.stringify(value, null, 2)}
      </pre>
    </div>
  );
}

/** ISO instant → `YYYY-MM-DDTHH:MM` in the browser zone for a datetime-local input. */
function toLocalInput(iso: string | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
