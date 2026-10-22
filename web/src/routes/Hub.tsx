import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { P, can } from '@/auth/permissions';
import { useMe } from '@/auth/queries';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { ageSeconds, hubFailuresQuery, hubHealthQuery, retentionStatusQuery, useRetryFailure, useRunRetention, type RetentionReport } from '@/hub/queries';
import { duration, utc } from '@/lib/time';
import { useNow } from '@/lib/useNow';

/** 08 §3.9 hub health: component heartbeats, queue depth and age per job kind, outbox lag, failure queue with retry, dead-man, database, retention. */
export function HubPage() {
  const { t } = useTranslation();
  const me = useMe();
  const isAdmin = can(me.data, P.hubAdmin);
  const health = useQuery(hubHealthQuery());
  const failures = useQuery({ ...hubFailuresQuery(), enabled: isAdmin });
  const retention = useQuery(retentionStatusQuery());
  const now = useNow(10_000);
  const retry = useRetryFailure();
  const run = useRunRetention();
  const [confirmRun, setConfirmRun] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [lastReport, setLastReport] = useState<RetentionReport | null>(null);

  if (health.isPending) return <p className="p-4 text-ink-2">{t('detail.loading')}</p>;
  if (health.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={health.error as unknown as Problem} />
      </div>
    );
  }
  const h = health.data;
  const unhealthy = h.components.filter((c) => !c.healthy).length;
  const outboxAge = ageSeconds(h.outboxOldestPending, now);
  const cards: { key: string; value: string; ok: boolean; hint?: string }[] = [
    { key: 'database', value: h.databaseOk ? t('hub.ok') : t('hub.down'), ok: h.databaseOk },
    { key: 'components', value: `${h.components.length - unhealthy}/${h.components.length}`, ok: unhealthy === 0 && h.components.length > 0, hint: t('hub.componentsHint') },
    { key: 'outbox', value: outboxAge === null ? '0' : `${h.outboxPending} · ${duration(outboxAge * 1000)}`, ok: outboxAge === null || outboxAge < 300, hint: t('hub.outboxHint') },
    { key: 'failed', value: `${h.jobsFailed + h.outboxFailed}`, ok: h.jobsFailed + h.outboxFailed === 0, hint: t('hub.failedHint') },
    { key: 'deadman', value: !h.deadman.configured ? t('hub.notConfigured') : h.deadman.ok ? t('hub.ok') : t('hub.stale'), ok: h.deadman.configured && h.deadman.ok, hint: h.deadman.configured ? undefined : t('hub.deadmanHint') },
  ];

  return (
    <div className="h-full overflow-y-auto bg-surface">
      <div className="mx-auto grid max-w-6xl gap-4 px-4 py-4 text-sm">
        <header className="flex flex-wrap items-center gap-2">
          <h1 className="m-0 text-md">{t('hub.title')}</h1>
          <span className="text-xs text-ink-2">
            {t('hub.asOf')} <RelativeTime iso={h.at} mode="sentence" />
          </span>
          <Link to="/audit" search={{ action: 'retention' }} className="ml-auto text-xs underline">
            {t('nav.audit')}
          </Link>
        </header>

        <div className="grid gap-2 sm:grid-cols-3 lg:grid-cols-5" data-testid="hub-cards">
          {cards.map((c) => (
            <div key={c.key} className="rounded-md border border-line bg-surface-2 p-3" data-card={c.key} data-ok={c.ok}>
              <span className="block text-xl font-semibold">
                <span aria-hidden="true">{c.ok ? '● ' : '○ '}</span>
                {c.value}
              </span>
              <span className="block text-xs text-ink-2">{t(`hub.cards.${c.key}`)}</span>
              {c.hint && <span className="block text-xs text-ink-2">{c.hint}</span>}
            </div>
          ))}
        </div>

        <div className="grid gap-4 lg:grid-cols-2">
          <section>
            <h2 className="m-0 text-xs text-ink-2">{t('hub.components')}</h2>
            {h.components.length === 0 ? (
              <p className="m-0 mt-1 text-ink-2">{t('hub.noComponents')}</p>
            ) : (
              <table className="mt-1 w-full border-collapse text-sm" data-testid="hub-components">
                <tbody>
                  {h.components.map((c) => (
                    <tr key={`${c.component}:${c.instance}`} className="border-t border-line" data-healthy={c.healthy}>
                      <td className="py-1">
                        <span aria-hidden="true">{c.healthy ? '● ' : '○ '}</span>
                        {c.component}
                      </td>
                      <td className="mono py-1 text-xs text-ink-2">{c.instance}</td>
                      <td className="py-1 text-right text-xs text-ink-2" title={utc(c.lastSeen)}>
                        <RelativeTime iso={c.lastSeen} mode="sentence" />
                      </td>
                      <td className="py-1 pl-2 text-right text-xs">{c.healthy ? t('hub.healthy') : t('hub.stale')}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>

          <section>
            <h2 className="m-0 text-xs text-ink-2">{t('hub.queues')}</h2>
            {h.queues.length === 0 ? (
              <p className="m-0 mt-1 text-ink-2">{t('hub.noQueues')}</p>
            ) : (
              <table className="mt-1 w-full border-collapse text-sm" data-testid="hub-queues">
                <thead className="text-left text-xs text-ink-2">
                  <tr className="border-b border-line">
                    <th className="font-normal">{t('hub.queue.kind')}</th>
                    <th className="text-right font-normal">{t('hub.queue.pending')}</th>
                    <th className="text-right font-normal">{t('hub.queue.reserved')}</th>
                    <th className="text-right font-normal">{t('hub.queue.suspended')}</th>
                    <th className="text-right font-normal">{t('hub.queue.failed')}</th>
                    <th className="text-right font-normal">{t('hub.queue.oldest')}</th>
                  </tr>
                </thead>
                <tbody>
                  {h.queues.map((q) => {
                    const age = ageSeconds(q.oldestPending, now);
                    return (
                      <tr key={q.kind} className="border-t border-line">
                        <td className="mono py-1 text-xs">{q.kind}</td>
                        <td className="py-1 text-right">{q.pending}</td>
                        <td className="py-1 text-right">{q.reserved}</td>
                        <td className="py-1 text-right">{q.suspended}</td>
                        <td className="py-1 text-right">{q.failed}</td>
                        <td className="py-1 text-right text-xs text-ink-2">{age === null ? '—' : duration(age * 1000)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </section>
        </div>

        <section data-testid="hub-retention">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="m-0 text-xs text-ink-2">{t('hub.retention.title')}</h2>
            {isAdmin && (
              <button type="button" className="btn btn-sm ml-auto" onClick={() => setConfirmRun(true)} disabled={run.isPending} data-testid="retention-run">
                {t('hub.retention.runNow')}
              </button>
            )}
          </div>
          {retention.isError && <ProblemBanner problem={retention.error as unknown as Problem} />}
          {retention.data && (
            <dl className="m-0 mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs sm:grid-cols-[auto_1fr_auto_1fr]">
              <dt className="text-ink-2">{t('hub.retention.rawBoundary')}</dt>
              <dd className="m-0" title={utc(retention.data.rawBoundary)}>
                {utc(retention.data.rawBoundary)} · {t('hub.retention.rawDays', { count: retention.data.settings.rawDays })}
              </dd>
              <dt className="text-ink-2">{t('hub.retention.partitions')}</dt>
              <dd className="m-0 mono">
                {retention.data.partitionCount} ({retention.data.oldestPartition ?? '—'} → {retention.data.newestPartition ?? '—'})
              </dd>
              <dt className="text-ink-2">{t('hub.retention.rows')}</dt>
              <dd className="m-0">{t('hub.retention.rowsText', retention.data.settings)}</dd>
              <dt className="text-ink-2">{t('hub.retention.nextRun')}</dt>
              <dd className="m-0" title={utc(retention.data.nextRunAt)}>
                <RelativeTime iso={retention.data.nextRunAt} mode="sentence" />
              </dd>
              <dt className="text-ink-2">{t('hub.retention.lastRun')}</dt>
              <dd className="m-0 sm:col-span-3" data-testid="retention-last-run">
                {retention.data.lastRun ? <ReportText report={retention.data.lastRun} /> : t('hub.retention.never')}
              </dd>
            </dl>
          )}
          {lastReport && (
            <p className="m-0 mt-2 rounded-md border border-line p-2 text-xs" aria-live="polite" data-testid="retention-result">
              <ReportText report={lastReport} />
            </p>
          )}
          {problem && <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />}
        </section>

        {isAdmin && (
          <section data-testid="hub-failures">
            <h2 className="m-0 text-xs text-ink-2">{t('hub.failures.title')}</h2>
            {failures.isError && <ProblemBanner problem={failures.error as unknown as Problem} />}
            {failures.data?.length === 0 ? (
              <p className="m-0 mt-1 text-ink-2">{t('hub.failures.empty')}</p>
            ) : (
              <table className="mt-1 w-full border-collapse text-sm">
                <thead className="text-left text-xs text-ink-2">
                  <tr className="border-b border-line">
                    <th className="font-normal">{t('hub.failures.when')}</th>
                    <th className="font-normal">{t('hub.failures.source')}</th>
                    <th className="font-normal">{t('hub.failures.type')}</th>
                    <th className="text-right font-normal">{t('hub.failures.attempts')}</th>
                    <th className="font-normal">{t('hub.failures.error')}</th>
                    <th className="font-normal">{t('hub.failures.episode')}</th>
                    <th className="font-normal"></th>
                  </tr>
                </thead>
                <tbody>
                  {(failures.data ?? []).map((f) => (
                    <tr key={f.id} className="border-t border-line align-top" data-failure-id={f.id}>
                      <td className="py-1 text-xs text-ink-2" title={utc(f.at)}>
                        <RelativeTime iso={f.at} mode="sentence" />
                      </td>
                      <td className="py-1">
                        <span className="badge">{f.source}</span>
                      </td>
                      <td className="mono py-1 text-xs">{f.type}</td>
                      <td className="py-1 text-right">{f.attempts}</td>
                      <td className="py-1 text-xs text-ink-2">
                        <span className="line-clamp-2 max-w-md">{f.lastError ?? '—'}</span>
                      </td>
                      <td className="py-1 text-xs">
                        {f.episodeId ? (
                          <Link to="/episodes/$id" params={{ id: f.episodeId }} className="text-ink hover:underline">
                            {f.episodeId.slice(0, 8)}…
                          </Link>
                        ) : (
                          '—'
                        )}
                      </td>
                      <td className="py-1 text-right">
                        <button type="button" className="btn btn-sm" disabled={retry.isPending} onClick={() => retry.mutate(f.id, { onError: (e) => setProblem(e as unknown as Problem) })}>
                          {t('hub.failures.retry')}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        )}
      </div>

      <ConfirmDialog
        open={confirmRun}
        onOpenChange={setConfirmRun}
        title={t('hub.retention.confirmTitle')}
        description={t('hub.retention.confirmWhy')}
        verb={t('hub.retention.runNow')}
        busy={run.isPending}
        onConfirm={() =>
          run.mutate(undefined, {
            onSuccess: (report) => {
              setLastReport(report);
              setConfirmRun(false);
            },
            onError: (e) => {
              setConfirmRun(false);
              setProblem(e as unknown as Problem);
            },
          })
        }
      />
    </div>
  );
}

function ReportText({ report }: { report: RetentionReport }) {
  const { t } = useTranslation();
  const deleted = Object.entries(report.deleted)
    .filter(([, n]) => n > 0)
    .map(([k, n]) => `${k} ${n}`)
    .join(', ');
  return (
    <span>
      <RelativeTime iso={report.at} mode="sentence" /> · {t('hub.retention.dropped', { count: report.droppedPartitions.length })} · {report.totalDeleted > 0 ? `${t('hub.retention.deleted')} ${deleted}` : t('hub.retention.nothingDeleted')} · {report.durationMs} ms
    </span>
  );
}
