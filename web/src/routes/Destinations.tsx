import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { teamsQuery } from '@/episodes/queries';
import { destinationsQuery, templatesQuery, type DestinationSummary } from '@/notifications/queries';

/** Channel badge — text + glyph (WCAG 1.4.1: never colour alone). */
export function ChannelBadge({ channelType }: { channelType: string }) {
  const email = channelType === 'smtp_email';
  return (
    <span className="badge" data-channel={channelType}>
      <span aria-hidden="true">{email ? '✉' : '⇢'}</span>
      {email ? 'Email' : 'Webhook'}
    </span>
  );
}

/** 08 §3.7b list: name, channel, team, subscribed event types, last success / failure, consecutive failures, fallback. */
export function DestinationsPage() {
  const { t } = useTranslation();
  const me = useMe();
  const list = useQuery(destinationsQuery());
  const teams = useQuery(teamsQuery());
  const templates = useQuery(templatesQuery());
  const canManage = can(me.data, P.destinationManage);
  const teamName = (id: string | null) => (id ? (teams.data?.find((x) => x.id === id)?.name ?? id.slice(0, 8)) : t('destinations.global'));
  const byId = new Map((list.data ?? []).map((d) => [d.id, d]));
  const templateName = (id: string | null | undefined) => (id ? (templates.data?.find((x) => x.id === id)?.name ?? id.slice(0, 8)) : 'generic-json');

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('destinations.title')}</h1>
        <Link to="/templates" className="btn btn-sm hover:no-underline">
          {t('templates.title')}
        </Link>
        {canManage && (
          <Link to="/destinations/new" className="btn btn-primary ml-auto hover:no-underline">
            {t('destinations.new')}
          </Link>
        )}
      </div>
      {list.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={list.error as unknown as Problem} />
        </div>
      )}
      {list.data?.length === 0 ? (
        <EmptyState variant="healthy" title={t('destinations.empty')} detail={t('destinations.emptyDetail')} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto bg-surface">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('destinations.columns.name')}</th>
                <th className="font-normal">{t('destinations.columns.channel')}</th>
                <th className="font-normal">{t('destinations.columns.team')}</th>
                <th className="font-normal max-lg:hidden">{t('destinations.columns.template')}</th>
                <th className="font-normal max-xl:hidden">{t('destinations.columns.events')}</th>
                <th className="font-normal">{t('destinations.columns.lastSuccess')}</th>
                <th className="font-normal">{t('destinations.columns.lastFailure')}</th>
                <th className="font-normal">{t('destinations.columns.failures')}</th>
                <th className="pr-3 font-normal">{t('destinations.columns.fallback')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data?.map((d: DestinationSummary) => (
                <tr key={d.id} className="h-9 border-b border-line hover:bg-surface-2" data-destination-id={d.id} data-active={d.active}>
                  <td className="pl-3">
                    <Link to="/destinations/$id" params={{ id: d.id }} className="text-ink hover:underline">
                      {d.name}
                    </Link>
                    {!d.active && <span className="badge ml-2 border-dashed">{t('destinations.inactive')}</span>}
                  </td>
                  <td>
                    <ChannelBadge channelType={d.channelType} />
                  </td>
                  <td className="text-ink-2">{teamName(d.teamId)}</td>
                  <td className="text-ink-2 max-lg:hidden">{d.channelType === 'webhook' ? templateName(d.bodyTemplateId) : '—'}</td>
                  <td className="text-xs text-ink-2 max-xl:hidden" title={d.eventTypes.join(', ')}>
                    {t('destinations.eventCount', { count: d.eventTypes.length })}
                  </td>
                  <td>
                    <RelativeTime iso={d.lastSuccessAt} mode="sentence" />
                  </td>
                  <td>
                    <RelativeTime iso={d.lastFailureAt} mode="sentence" />
                  </td>
                  <td>
                    {d.consecutiveFailures > 0 ? (
                      <span className="badge" style={{ color: 'var(--sev-high)', borderColor: 'var(--sev-high)' }}>
                        <span aria-hidden="true">!</span>
                        {d.consecutiveFailures}
                      </span>
                    ) : (
                      <span className="text-ink-2">0</span>
                    )}
                  </td>
                  <td className="pr-3 text-ink-2">
                    {byId.get(d.fallbackDestinationId) ? (
                      <Link to="/destinations/$id" params={{ id: d.fallbackDestinationId }} className="text-ink-2 hover:underline">
                        {byId.get(d.fallbackDestinationId)?.name}
                      </Link>
                    ) : (
                      d.fallbackDestinationId.slice(0, 8)
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
