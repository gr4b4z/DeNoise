import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { SeverityBadge } from '@/components/Badges';
import { heartbeatsQuery, type HeartbeatSummary } from '@/heartbeats/queries';

const STATES = ['healthy', 'late', 'missed', 'paused', 'unknown'] as const;

/** Heartbeat state badge — text + glyph, colour only for missed (critical language) and late (degraded language), 08 §0. */
export function HeartbeatStateBadge({ state }: { state: string }) {
  const style = state === 'missed' ? { color: 'var(--sev-critical)', borderColor: 'var(--sev-critical)' } : state === 'late' ? { color: 'var(--sev-high)', borderColor: 'var(--sev-high)' } : undefined;
  const glyph = state === 'healthy' ? '✓' : state === 'missed' ? '✕' : state === 'late' ? '…' : state === 'paused' ? '❙❙' : '?';
  const label = state === 'healthy' ? 'Healthy' : state === 'late' ? 'Late' : state === 'missed' ? 'Missed' : state === 'paused' ? 'Paused' : 'Unknown';
  return (
    <span className={`badge ${state === 'unknown' ? 'border-dashed' : ''}`} data-heartbeat-state={state} style={style}>
      <span aria-hidden="true">{glyph}</span>
      {label}
    </span>
  );
}

export interface HeartbeatsSearch {
  state?: string;
  team?: string;
  q?: string;
}

/** 08 §3.4 list: name, state, schedule, last ping, owner, bound integration, next expected. Paused ones are visibly a coverage gap. */
export function HeartbeatsPage() {
  const { t } = useTranslation();
  const search = useSearch({ strict: false }) as unknown as HeartbeatsSearch;
  const navigate = useNavigate();
  const me = useMe();
  const list = useQuery(heartbeatsQuery({ state: search.state, team: search.team, q: search.q }));
  const update = (patch: Partial<HeartbeatsSearch>) => void navigate({ to: '/heartbeats', search: (prev: HeartbeatsSearch) => ({ ...prev, ...patch }), replace: true });
  const canManage = can(me.data, P.heartbeatManage);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('heartbeats.title')}</h1>
        <div role="group" aria-label="State" className="flex gap-1">
          {STATES.map((s) => (
            <button key={s} type="button" aria-pressed={search.state === s} className={`btn btn-sm ${search.state === s ? 'bg-surface-2 font-semibold' : ''}`} onClick={() => update({ state: search.state === s ? undefined : s })}>
              <HeartbeatStateBadge state={s} />
            </button>
          ))}
        </div>
        <form
          className="ml-auto flex items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            const q = (new FormData(e.currentTarget).get('q') as string) || undefined;
            update({ q });
          }}
        >
          <input name="q" type="search" className="input w-60" placeholder={t('heartbeats.search')} aria-label={t('heartbeats.search')} defaultValue={search.q ?? ''} />
          {canManage && (
            <Link to="/heartbeats/new" className="btn btn-primary hover:no-underline">
              {t('heartbeats.new')}
            </Link>
          )}
        </form>
      </div>
      {list.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={list.error as unknown as Problem} />
        </div>
      )}
      {list.data?.length === 0 ? (
        <EmptyState variant={search.state || search.q ? 'filtered' : 'healthy'} title={search.state || search.q ? t('heartbeats.emptyFiltered') : t('heartbeats.empty')} detail={search.state || search.q ? undefined : t('heartbeats.emptyDetail')} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto bg-surface">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('heartbeats.columns.name')}</th>
                <th className="font-normal">{t('heartbeats.columns.state')}</th>
                <th className="font-normal">{t('heartbeats.columns.schedule')}</th>
                <th className="font-normal">{t('heartbeats.columns.lastPing')}</th>
                <th className="font-normal">{t('heartbeats.columns.owner')}</th>
                <th className="font-normal max-lg:hidden">{t('heartbeats.columns.bound')}</th>
                <th className="pr-3 font-normal">{t('heartbeats.columns.next')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data?.map((h: HeartbeatSummary) => (
                <tr key={h.id} className="h-9 border-b border-line hover:bg-surface-2" data-heartbeat-id={h.id} data-state={h.state}>
                  <td className="pl-3">
                    <Link to="/heartbeats/$id" params={{ id: h.id }} className="text-ink hover:underline">
                      {h.name}
                    </Link>
                    {h.description && <span className="ml-2 text-xs text-ink-2">{h.description}</span>}
                  </td>
                  <td>
                    <span className="flex items-center gap-2">
                      <HeartbeatStateBadge state={h.state} />
                      {h.state === 'paused' && <span className="text-xs text-ink-2">{h.pausedByMaintenance ? 'maintenance' : h.pauseReason}</span>}
                    </span>
                  </td>
                  <td className="text-ink-2">
                    {h.schedule.description}
                    <span className="ml-2 text-xs">· grace {h.grace}</span>
                    <span className="ml-2">
                      <SeverityBadge severity={h.severityOnMiss} compact />
                    </span>
                  </td>
                  <td>
                    <RelativeTime iso={h.lastPingAt} mode="sentence" />
                  </td>
                  <td className="text-ink-2">{h.owningTeam?.name ?? '—'}</td>
                  <td className="text-ink-2 max-lg:hidden">{h.boundIntegrationName ?? ''}</td>
                  <td className="pr-3">{h.state === 'paused' ? <span className="text-ink-2">—</span> : <RelativeTime iso={h.expectedNext} mode="sentence" />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
