import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { SEVERITIES, type Problem } from '@/api/types';
import { SeverityBadge } from '@/components/Badges';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { teamsQuery } from '@/episodes/queries';
import { utc } from '@/lib/time';
import { CLOSURE_REASONS, EVIDENCE, historyQuery, type HistoryFilters } from '@/suppressions/queries';

export interface HistorySearch {
  severity?: string[];
  team?: string;
  q?: string;
  closureReason?: string;
  evidence?: string;
}

/** History screen (06 §4 `/history`, 08 §3.1 "Closed"): closed episodes with closure reason and evidence filters; rows open the episode. */
export function HistoryPage() {
  const { t } = useTranslation();
  const search = useSearch({ strict: false }) as unknown as HistorySearch;
  const navigate = useNavigate();
  const teams = useQuery(teamsQuery());
  const filters: HistoryFilters = useMemo(() => ({ severity: search.severity, team: search.team, q: search.q, closureReason: search.closureReason, evidence: search.evidence }), [search.severity, search.team, search.q, search.closureReason, search.evidence]);
  const history = useInfiniteQuery(historyQuery(filters));
  const items = useMemo(() => history.data?.pages.flatMap((p) => p.items) ?? [], [history.data]);
  const total = history.data?.pages[0]?.total ?? items.length;
  const update = (patch: Partial<HistorySearch>) => void navigate({ to: '/history', search: (prev: HistorySearch) => ({ ...prev, ...patch }), replace: true });
  const toggleSeverity = (s: string) => {
    const current = new Set(search.severity ?? []);
    if (current.has(s)) current.delete(s);
    else current.add(s);
    update({ severity: current.size ? [...current] : undefined });
  };
  const filtered = !!(search.q || search.severity?.length || search.closureReason || search.evidence || search.team);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('history.title')}</h1>
        <div role="group" aria-label={t('queue.severity')} className="flex gap-1">
          {SEVERITIES.map((s) => (
            <button key={s} type="button" aria-pressed={search.severity?.includes(s) ?? false} className={`btn btn-sm ${search.severity?.includes(s) ? 'bg-surface-2 font-semibold' : ''}`} onClick={() => toggleSeverity(s)}>
              <SeverityBadge severity={s} />
            </button>
          ))}
        </div>
        <select className="input h-7 w-auto text-sm" aria-label={t('history.closureReason')} value={search.closureReason ?? ''} onChange={(e) => update({ closureReason: e.target.value || undefined })} data-testid="history-reason">
          <option value="">{t('history.anyReason')}</option>
          {CLOSURE_REASONS.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>
        <select className="input h-7 w-auto text-sm" aria-label={t('history.evidence')} value={search.evidence ?? ''} onChange={(e) => update({ evidence: e.target.value || undefined })}>
          <option value="">{t('history.anyEvidence')}</option>
          {EVIDENCE.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>
        <select className="input h-7 w-auto text-sm" aria-label={t('history.team')} value={search.team ?? ''} onChange={(e) => update({ team: e.target.value || undefined })}>
          <option value="">{t('history.anyTeam')}</option>
          {teams.data?.map((team) => (
            <option key={team.id} value={team.id}>
              {team.name}
            </option>
          ))}
        </select>
        <form
          className="ml-auto flex items-center gap-1"
          onSubmit={(e) => {
            e.preventDefault();
            update({ q: ((new FormData(e.currentTarget).get('q') as string) || '').trim() || undefined });
          }}
        >
          <input name="q" type="search" className="input w-64" placeholder={t('queue.search')} aria-label={t('queue.search')} defaultValue={search.q ?? ''} />
        </form>
        <span className="text-xs text-ink-2" aria-live="polite">
          {history.isFetching ? '…' : `${total}`}
        </span>
      </div>
      {history.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={history.error as unknown as Problem} />
        </div>
      )}
      {history.data && items.length === 0 ? (
        <EmptyState variant={filtered ? 'filtered' : 'healthy'} title={filtered ? t('queue.emptyFiltered') : t('history.empty')} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto bg-surface" data-testid="history-table">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('queue.severity')}</th>
                <th className="font-normal">{t('queue.columns.summary')}</th>
                <th className="font-normal max-lg:hidden">{t('queue.columns.resource')}</th>
                <th className="font-normal">{t('history.closedAt')}</th>
                <th className="font-normal">{t('history.closureReason')}</th>
                <th className="font-normal">{t('history.evidence')}</th>
                <th className="pr-3 font-normal max-lg:hidden">{t('queue.columns.owner')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((i) => (
                <tr key={i.id} className="h-9 border-b border-line hover:bg-surface-2" data-episode-id={i.id}>
                  <td className="pl-3">
                    <SeverityBadge severity={i.severity} />
                  </td>
                  <td className="max-w-[40vw] truncate">
                    <Link to="/episodes/$id" params={{ id: i.id }} className="text-ink hover:underline">
                      {i.summary ?? i.id}
                    </Link>
                  </td>
                  <td className="mono text-xs text-ink-2 max-lg:hidden">
                    {i.resourceName ?? '—'}
                    {i.service && <span> · {i.service}</span>}
                  </td>
                  <td className="text-ink-2" title={i.closedAt ? utc(i.closedAt) : undefined}>
                    <RelativeTime iso={i.closedAt} mode="sentence" />
                  </td>
                  <td className="mono text-xs">{i.closureReason ?? '—'}</td>
                  <td className="mono text-xs text-ink-2">{i.resolutionEvidence ?? '—'}</td>
                  <td className="pr-3 text-ink-2 max-lg:hidden">{i.owningTeam?.name ?? 'triage'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {history.hasNextPage && (
            <div className="p-3">
              <button type="button" className="btn btn-sm" onClick={() => void history.fetchNextPage()} disabled={history.isFetchingNextPage}>
                {t('history.more')}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
