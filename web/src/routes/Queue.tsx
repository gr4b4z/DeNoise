import { useInfiniteQuery } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SEVERITIES, type EpisodeListItem, type Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can } from '@/auth/permissions';
import { ConditionBadge, CoverageBadge, HandlingBadge, SeverityBadge } from '@/components/Badges';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { episodesQuery, type QueueFilters } from '@/episodes/queries';
import { primaryAction, useEpisodeAction } from '@/episodes/actions';
import { useRealtime } from '@/realtime/RealtimeProvider';
import { useSaveFilter } from '@/suppressions/queries';
import { EpisodeView } from './Episode';
import type { QueueSearch } from './queueSearch';

/** Work queue (08 §3.1): filter bar, virtualised table, right-side drawer; state lives in the URL so links are shareable. */
export function QueuePage() {
  const { t } = useTranslation();
  const search = useSearch({ strict: false }) as unknown as QueueSearch;
  const navigate = useNavigate();
  const me = useMe();
  const { pulsing } = useRealtime();
  const filters: QueueFilters = useMemo(
    () => ({ view: search.view, severity: search.severity, team: search.team, environment: search.environment, service: search.service, q: search.q }),
    [search.view, search.severity, search.team, search.environment, search.service, search.q],
  );
  const episodes = useInfiniteQuery(episodesQuery(filters));
  const items = useMemo(() => episodes.data?.pages.flatMap((p) => p.items) ?? [], [episodes.data]);
  const total: number = episodes.data?.pages[0]?.total ?? items.length;
  const act = useEpisodeAction();
  const [problem, setProblem] = useState<Problem | null>(null);
  const [cursor, setCursor] = useState(0);
  const searchRef = useRef<HTMLInputElement>(null);
  const [draft, setDraft] = useState(search.q ?? '');
  const saveFilter = useSaveFilter();

  const update = useCallback(
    (patch: Partial<QueueSearch>) => void navigate({ to: '/queue', search: (prev: Record<string, unknown>) => ({ ...(prev as unknown as QueueSearch), ...patch }), replace: true }),
    [navigate],
  );

  const open = useCallback((id: string | undefined) => update({ episode: id }), [update]);

  const runPrimary = useCallback(
    (item: EpisodeListItem) => {
      const allowed = (p: string) => can(me.data, p, item.accessScope);
      const action = primaryAction(item, me.data, allowed);
      if (!action || action.kind === 'close' || (action.kind === 'ack' && action.force)) {
        open(item.id);
        return;
      }
      setProblem(null);
      act.mutate({ id: item.id, version: item.version, action }, { onError: setProblem });
    },
    [act, me.data, open],
  );

  // Keyboard triage loop (08 §3.1): j/k move, a acknowledge, o open, / search.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return;
      if (document.querySelector('[role="dialog"]')) return;
      switch (e.key) {
        case 'j':
          setCursor((c) => Math.min(items.length - 1, c + 1));
          break;
        case 'k':
          setCursor((c) => Math.max(0, c - 1));
          break;
        case 'o': {
          const item = items[cursor];
          if (item) open(item.id);
          break;
        }
        case 'a': {
          const item = items[cursor];
          if (item) runPrimary(item);
          break;
        }
        case '/':
          e.preventDefault();
          searchRef.current?.focus();
          break;
        case 'Escape':
          if (search.episode) open(undefined);
          break;
        default:
          return;
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [items, cursor, open, runPrimary, search.episode]);

  const toggleSeverity = (s: string) => {
    const current = new Set(search.severity ?? []);
    if (current.has(s)) current.delete(s);
    else current.add(s);
    update({ severity: current.size ? [...current] : undefined });
  };

  const degraded = useMemo(() => new Set(items.filter((i) => i.coverageState !== 'healthy' && i.coverageState !== 'unknown').map((i) => i.integrationName)), [items]);

  return (
    <div className="grid h-full min-h-0 grid-cols-[1fr_auto]">
      <section className="flex min-h-0 min-w-0 flex-col" aria-label={t(`views.${search.view}`)}>
        <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
          <h1 className="mr-2 text-md">{t(`views.${search.view}`)}</h1>
          <div role="group" aria-label={t('queue.severity')} className="flex gap-1">
            {SEVERITIES.map((s) => {
              const active = search.severity?.includes(s) ?? false;
              return (
                <button
                  key={s}
                  type="button"
                  aria-pressed={active}
                  onClick={() => toggleSeverity(s)}
                  className={`btn btn-sm ${active ? 'bg-surface-2 font-semibold' : ''}`}
                >
                  <SeverityBadge severity={s} />
                </button>
              );
            })}
          </div>
          <form
            className="ml-auto flex items-center gap-1"
            onSubmit={(e) => {
              e.preventDefault();
              update({ q: draft || undefined });
            }}
          >
            <input
              ref={searchRef}
              type="search"
              className="input w-72"
              placeholder={t('queue.search')}
              aria-label={t('queue.search')}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
            />
          </form>
          <button
            type="button"
            className="btn btn-sm"
            title={t('queue.saveFilterHint')}
            data-testid="save-filter"
            onClick={() => {
              const name = window.prompt(t('queue.saveFilterPrompt'));
              if (!name?.trim()) return;
              const params = new URLSearchParams();
              params.set('view', search.view);
              for (const s of search.severity ?? []) params.append('severity', s);
              for (const [k, v] of Object.entries({ team: search.team, environment: search.environment, service: search.service, q: search.q })) if (v) params.set(k, v);
              saveFilter.mutate({ name: name.trim(), query: params.toString() }, { onError: (e) => setProblem(e as unknown as Problem) });
            }}
          >
            {t('queue.saveFilter')}
          </button>
          <span className="text-xs text-ink-2" aria-live="polite">
            {episodes.isFetching ? '…' : `${total}`}
          </span>
        </div>
        {problem && (
          <div className="px-3 pt-2">
            <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
          </div>
        )}
        {episodes.isError && (
          <div className="px-3 pt-2">
            <ProblemBanner problem={episodes.error as unknown as Problem} />
          </div>
        )}
        {episodes.data && items.length === 0 ? (
          search.q || search.severity?.length ? (
            <EmptyState variant="filtered" title={t('queue.emptyFiltered')} />
          ) : degraded.size > 0 ? (
            <EmptyState variant="coverageWarning" title={t('queue.emptyCoverage', { count: degraded.size })} detail={[...degraded].join(', ')} />
          ) : (
            <EmptyState variant="healthy" title={t('queue.emptyHealthy')} />
          )
        ) : (
          <QueueTable
            items={items}
            cursor={cursor}
            selectedId={search.episode}
            pulsing={pulsing}
            onOpen={(id, index) => {
              setCursor(index);
              open(id);
            }}
            onPrimary={runPrimary}
            onEndReached={() => {
              if (episodes.hasNextPage && !episodes.isFetchingNextPage) void episodes.fetchNextPage();
            }}
            me={me.data}
          />
        )}
        <div className="border-t border-line px-3 py-1 text-xs text-ink-2 max-md:hidden">{t('queue.keys')}</div>
      </section>
      {search.episode && (
        <aside className="w-[min(var(--drawer-width),100vw)] min-w-0 border-l border-line bg-surface max-md:fixed max-md:inset-0 max-md:z-10" style={{ boxShadow: 'var(--shadow-drawer)' }} aria-label="Episode">
          <EpisodeView id={search.episode} inDrawer onClose={() => open(undefined)} />
        </aside>
      )}
    </div>
  );
}

interface TableProps {
  items: EpisodeListItem[];
  cursor: number;
  selectedId?: string;
  pulsing: ReadonlySet<string>;
  me: { id: string; roles: string[]; scopes: string[]; permissions: string[] } | undefined;
  onOpen: (id: string, index: number) => void;
  onPrimary: (item: EpisodeListItem) => void;
  onEndReached: () => void;
}

function QueueTable({ items, cursor, selectedId, pulsing, me, onOpen, onPrimary, onEndReached }: TableProps) {
  const { t } = useTranslation();
  const parentRef = useRef<HTMLDivElement>(null);
  const rowHeight = useMemo(() => parseInt(getComputedStyle(document.documentElement).getPropertyValue('--row-height') || '36', 10) || 36, []);
  const virtualizer = useVirtualizer({ count: items.length, getScrollElement: () => parentRef.current, estimateSize: () => rowHeight, overscan: 12 });
  const virtualItems = virtualizer.getVirtualItems();

  useEffect(() => {
    const last = virtualItems[virtualItems.length - 1];
    if (last && last.index >= items.length - 20) onEndReached();
  }, [virtualItems, items.length, onEndReached]);

  useEffect(() => {
    if (items[cursor]) virtualizer.scrollToIndex(cursor, { align: 'auto' });
  }, [cursor, items, virtualizer]);

  return (
    <div ref={parentRef} className="min-h-0 flex-1 overflow-auto bg-surface" role="region" aria-label="Episodes">
      <table className="w-full border-collapse text-sm" style={{ tableLayout: 'fixed' }}>
        <thead className="sticky top-0 z-[1] bg-surface text-left text-xs text-ink-2">
          <tr className="h-8 border-b border-line">
            <th className="w-24 pl-3 font-normal">{t('queue.severity')}</th>
            <th className="font-normal">{t('queue.columns.summary')}</th>
            <th className="w-48 font-normal max-lg:hidden">{t('queue.columns.resource')}</th>
            <th className="w-16 font-normal max-lg:hidden">{t('queue.columns.env')}</th>
            <th className="w-24 font-normal">{t('queue.columns.condition')}</th>
            <th className="w-28 font-normal">{t('queue.columns.handling')}</th>
            <th className="w-40 font-normal max-lg:hidden">{t('queue.columns.owner')}</th>
            <th className="w-14 text-right font-normal">{t('queue.columns.age')}</th>
            <th className="w-14 text-right font-normal">{t('queue.columns.seen')}</th>
            <th className="w-24 font-normal" aria-label="Indicators"></th>
            <th className="w-32 pr-3 font-normal" aria-label="Actions"></th>
          </tr>
        </thead>
        <tbody style={{ height: virtualizer.getTotalSize(), position: 'relative', display: 'block' }}>
          {virtualItems.map((v) => {
            const item = items[v.index];
            if (!item) return null;
            const allowed = (p: string) => can(me, p, item.accessScope);
            const primary = primaryAction(item, me, allowed);
            const selected = selectedId === item.id;
            const focused = cursor === v.index;
            return (
              <tr
                key={item.id}
                data-index={v.index}
                data-episode-id={item.id}
                data-version={item.version}
                aria-selected={selected}
                onClick={() => onOpen(item.id, v.index)}
                onDoubleClick={() => onPrimary(item)}
                className={`absolute left-0 flex w-full cursor-default items-center border-b border-line hover:bg-surface-2 ${selected ? 'bg-surface-2' : ''} ${pulsing.has(item.id) ? 'row-pulse' : ''} ${item.conditionState === 'unknown' ? 'border-dashed' : ''}`}
                style={{
                  top: v.start,
                  height: v.size,
                  borderLeft: `3px solid ${item.severity === 'unknown' ? 'var(--sev-high)' : `var(--sev-${item.severity})`}`,
                  outline: focused ? '2px solid var(--accent)' : undefined,
                  outlineOffset: -2,
                  display: 'flex',
                }}
              >
                <td className="w-24 shrink-0 pl-3">
                  <SeverityBadge severity={item.severity} />
                </td>
                <td className={`min-w-0 flex-1 truncate ${item.suppressedUntil ? 'underline decoration-dotted' : ''}`} title={item.summary ?? undefined}>
                  {item.summary ?? '(no summary)'}
                </td>
                <td className="w-48 shrink-0 truncate text-ink-2 max-lg:hidden" title={`${item.resourceName ?? ''} ${item.service ?? ''}`}>
                  <span className="mono text-xs">{item.resourceName ?? '—'}</span>
                  {item.service && <span> · {item.service}</span>}
                </td>
                <td className="w-16 shrink-0 truncate text-ink-2 max-lg:hidden">{item.environment ?? ''}</td>
                <td className="w-24 shrink-0">
                  <ConditionBadge state={item.conditionState} />
                </td>
                <td className="w-28 shrink-0">
                  <HandlingBadge state={item.handlingState} />
                </td>
                <td className="w-40 shrink-0 truncate text-ink-2 max-lg:hidden">
                  {item.owningTeam?.name ?? 'triage'}
                  {item.assignee && <span> · {item.assignee.displayName}</span>}
                </td>
                <td className="w-14 shrink-0 text-right">
                  <RelativeTime iso={item.firstSeen} />
                </td>
                <td className="w-14 shrink-0 text-right">
                  <RelativeTime iso={item.lastSeen} />
                </td>
                <td className="w-24 shrink-0 text-xs">
                  <span className="flex items-center gap-1 pl-2 text-ink-2">
                    {item.ackOverdue && (
                      <span title="Acknowledgement overdue" style={{ color: 'var(--sev-critical)' }}>
                        ⏱
                      </span>
                    )}
                    {item.deliveryFailure && (
                      <span title="A notification failed to deliver" style={{ color: 'var(--sev-high)' }}>
                        ⚠
                      </span>
                    )}
                    {item.suppressedUntil && <span title="Silenced">⏸</span>}
                    {item.groupId && <span title="Member of a group">⧉</span>}
                    {item.stale && <span title="Stale: not verified with the source">◌</span>}
                    {item.routingCorrectionRequired && <span title="Routing correction required">↯</span>}
                    <CoverageBadge state={item.coverageState} />
                  </span>
                </td>
                <td className="w-32 shrink-0 pr-3 text-right">
                  {primary && (
                    <button
                      type="button"
                      className={`btn btn-sm ${primary.kind === 'ack' && !primary.force ? 'btn-primary' : ''}`}
                      onClick={(e) => {
                        e.stopPropagation();
                        onPrimary(item);
                      }}
                      data-row-action={primary.kind}
                    >
                      {primary.kind === 'ack' ? (primary.force ? t('actions.takeOver') : t('actions.acknowledge')) : primary.kind === 'assignToMe' ? t('actions.assignToMe') : primary.kind === 'close' ? t('actions.close') : primary.kind === 'restore' ? t('actions.restore') : t('actions.open')}
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
