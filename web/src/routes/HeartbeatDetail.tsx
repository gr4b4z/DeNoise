import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { SeverityBadge } from '@/components/Badges';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { OneTimeSecretDialog } from '@/components/OneTimeSecretDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { heartbeatQuery, pingSnippets, useHeartbeatAction, type HeartbeatRun } from '@/heartbeats/queries';
import { local, utc } from '@/lib/time';
import { HeartbeatFormPage } from './HeartbeatForm';
import { HeartbeatStateBadge } from './Heartbeats';

/** 08 §3.4 detail: run history ring, pause/resume with reason, rotate token (confirm → one-time dialog), edit. */
export function HeartbeatDetailPage({ id, edit = false }: { id: string; edit?: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const me = useMe();
  const detail = useQuery(heartbeatQuery(id));
  const act = useHeartbeatAction(id);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [dialog, setDialog] = useState<'pause' | 'rotate' | 'delete' | null>(null);
  const [pingUrl, setPingUrl] = useState<string | null>(null);

  if (detail.isPending) return <div className="p-4 text-sm text-ink-2">Loading…</div>;
  if (detail.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={detail.error as unknown as Problem} />
      </div>
    );
  }
  const hb = detail.data.heartbeat;
  if (edit) return <HeartbeatFormPage existing={hb} />;
  const canManage = can(me.data, P.heartbeatManage, hb.accessScope);

  const run = (action: 'pause' | 'resume' | 'rotate' | 'delete', reason?: string) => {
    setProblem(null);
    act.mutate(
      { action, version: hb.version, reason },
      {
        onSuccess: (result) => {
          setDialog(null);
          if (action === 'rotate' && result && 'pingUrl' in result) setPingUrl(result.pingUrl);
          if (action === 'delete') void navigate({ to: '/heartbeats', search: {} as never });
        },
        onError: (err) => setProblem(err as unknown as Problem),
      },
    );
  };

  return (
    <div className="h-full overflow-y-auto bg-surface">
      <div className="mx-auto max-w-4xl px-4 py-4 text-sm">
        <div className="text-xs text-ink-2">
          <Link to="/heartbeats" search={{} as never}>
            {t('heartbeats.title')}
          </Link>{' '}
          / {hb.name}
        </div>
        <header className="mt-2 flex flex-wrap items-start gap-3">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <HeartbeatStateBadge state={hb.state} />
              <SeverityBadge severity={hb.severityOnMiss} />
              {hb.state === 'paused' && (
                <span className="text-ink-2">
                  {hb.pausedByMaintenance ? t('heartbeats.pausedByMaintenance') : t('heartbeats.pausedBy', { reason: hb.pauseReason ?? '' })}
                  {hb.pausedAt && (
                    <>
                      {' '}
                      <RelativeTime iso={hb.pausedAt} mode="sentence" />
                    </>
                  )}
                </span>
              )}
            </div>
            <h1 className="mt-1 text-lg" data-testid="heartbeat-name">
              {hb.name}
            </h1>
            {hb.description && <p className="m-0 text-ink-2">{hb.description}</p>}
            <dl className="mt-2 grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-sm">
              <dt className="text-ink-2">{t('heartbeats.columns.schedule')}</dt>
              <dd className="m-0">
                {hb.schedule.description} · grace {hb.grace}
              </dd>
              <dt className="text-ink-2">{t('heartbeats.columns.owner')}</dt>
              <dd className="m-0">{hb.owningTeam?.name ?? '—'}</dd>
              <dt className="text-ink-2">{t('heartbeats.columns.lastPing')}</dt>
              <dd className="m-0">
                <RelativeTime iso={hb.lastPingAt} mode="sentence" />
                {hb.lastPingIp && <span className="mono ml-2 text-xs text-ink-2">{hb.lastPingIp}</span>}
                {hb.lastRunDuration && <span className="ml-2 text-xs text-ink-2">run {hb.lastRunDuration}</span>}
              </dd>
              <dt className="text-ink-2">{t('heartbeats.columns.next')}</dt>
              <dd className="m-0">{hb.state === 'paused' ? '—' : <RelativeTime iso={hb.expectedNext} mode="sentence" />}</dd>
              {hb.boundIntegrationName && (
                <>
                  <dt className="text-ink-2">{t('heartbeats.columns.bound')}</dt>
                  <dd className="m-0">{hb.boundIntegrationName}</dd>
                </>
              )}
              {hb.missEpisodeId && (
                <>
                  <dt className="text-ink-2">{t('heartbeats.missEpisode')}</dt>
                  <dd className="m-0">
                    <Link to="/episodes/$id" params={{ id: hb.missEpisodeId }}>
                      {t('heartbeats.openEpisode')}
                    </Link>
                  </dd>
                </>
              )}
              <dt className="text-ink-2">{t('heartbeats.keyId')}</dt>
              <dd className="m-0 mono">
                {hb.keyId} <span className="text-xs text-ink-2">· token rotated {utc(hb.tokenRotatedAt)}</span>
              </dd>
            </dl>
          </div>
          {canManage && (
            <div className="flex flex-wrap gap-1">
              {hb.state === 'paused' ? (
                <button type="button" className="btn btn-primary" onClick={() => run('resume')} disabled={act.isPending}>
                  {t('heartbeats.resume')}
                </button>
              ) : (
                <button type="button" className="btn" onClick={() => setDialog('pause')}>
                  {t('heartbeats.pause')}
                </button>
              )}
              <button type="button" className="btn" onClick={() => setDialog('rotate')}>
                {t('heartbeats.rotate')}
              </button>
              <Link to="/heartbeats/$id/edit" params={{ id }} className="btn hover:no-underline">
                {t('heartbeats.edit')}
              </Link>
              <button type="button" className="btn" onClick={() => setDialog('delete')}>
                {t('heartbeats.delete')}
              </button>
            </div>
          )}
        </header>
        {problem && (
          <div className="mt-3" aria-live="polite">
            <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
          </div>
        )}

        {detail.data.nextRuns.length > 0 && (
          <section className="mt-4">
            <h2 className="text-xs text-ink-2">{t('heartbeats.form.nextRuns')}</h2>
            <ol className="m-0 mt-1 list-none p-0 text-xs">
              {detail.data.nextRuns.map((r) => (
                <li key={r} className="mono">
                  {utc(r)} <span className="text-ink-2">· local {local(r)}</span>
                </li>
              ))}
            </ol>
          </section>
        )}

        <section className="mt-4">
          <h2 className="text-xs text-ink-2">{t('heartbeats.runs')}</h2>
          {detail.data.lastRuns.length === 0 ? (
            <p className="m-0 mt-1 text-ink-2">{t('heartbeats.noRuns')}</p>
          ) : (
            <table className="mt-1 w-full border-collapse text-sm">
              <thead className="text-left text-xs text-ink-2">
                <tr className="h-7 border-b border-line">
                  <th className="font-normal">#</th>
                  <th className="font-normal">{t('heartbeats.runColumns.kind')}</th>
                  <th className="font-normal">{t('heartbeats.runColumns.at')}</th>
                  <th className="font-normal">{t('heartbeats.runColumns.duration')}</th>
                  <th className="font-normal">{t('heartbeats.runColumns.exit')}</th>
                  <th className="font-normal">{t('heartbeats.runColumns.source')}</th>
                  <th className="font-normal">{t('heartbeats.runColumns.body')}</th>
                </tr>
              </thead>
              <tbody>
                {detail.data.lastRuns.map((r: HeartbeatRun) => (
                  <tr key={r.seq} className="border-b border-line align-top" data-run-kind={r.kind}>
                    <td className="py-1 text-ink-2">{r.seq}</td>
                    <td className="py-1">
                      <span className="badge" style={r.kind === 'fail' || (r.kind === 'exit' && r.exitCode !== 0) ? { color: 'var(--sev-critical)', borderColor: 'var(--sev-critical)' } : undefined}>
                        {r.kind}
                      </span>
                    </td>
                    <td className="py-1" title={local(r.finishedAt)}>
                      {utc(r.finishedAt)}
                    </td>
                    <td className="py-1 text-ink-2">{r.startedAt && r.kind !== 'start' ? `${Math.round((new Date(r.finishedAt).getTime() - new Date(r.startedAt).getTime()) / 1000)} s` : ''}</td>
                    <td className="py-1 mono">{r.exitCode ?? ''}</td>
                    <td className="py-1 mono text-xs text-ink-2">{r.sourceIp ?? ''}</td>
                    <td className="py-1">{r.body && <pre className="mono m-0 max-h-24 max-w-md overflow-auto whitespace-pre-wrap text-xs">{r.body}</pre>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      </div>

      <ConfirmDialog open={dialog === 'pause'} onOpenChange={(o) => !o && setDialog(null)} title={t('heartbeats.pauseTitle', { name: hb.name })} description={t('heartbeats.pauseWhy')} verb={t('heartbeats.pause')} withReason reasonRequired busy={act.isPending} onConfirm={(reason) => run('pause', reason)} />
      <ConfirmDialog open={dialog === 'rotate'} onOpenChange={(o) => !o && setDialog(null)} title={t('heartbeats.rotateTitle', { name: hb.name })} description={t('heartbeats.rotateWhy')} verb={t('heartbeats.rotate')} busy={act.isPending} onConfirm={() => run('rotate')} />
      <ConfirmDialog open={dialog === 'delete'} onOpenChange={(o) => !o && setDialog(null)} title={t('heartbeats.deleteTitle', { name: hb.name })} description={t('heartbeats.deleteWhy')} verb={t('heartbeats.delete')} busy={act.isPending} onConfirm={() => run('delete')} />
      {pingUrl && <OneTimeSecretDialog open title={t('heartbeats.pingUrlTitle')} secret={pingUrl} secretLabel={t('heartbeats.pingUrl')} snippets={pingSnippets(pingUrl)} onClose={() => setPingUrl(null)} />}
    </div>
  );
}
