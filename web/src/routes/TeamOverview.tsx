import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { EpisodeListItem, Problem } from '@/api/types';
import { SeverityBadge } from '@/components/Badges';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { integrationsQuery } from '@/integrations/queries';
import { destinationsQuery } from '@/notifications/queries';
import { IntegrationHealthBadge } from '@/routes/Integrations';
import { teamOverviewQuery } from '@/suppressions/queries';

/** 08 §3.3: cards (unassigned, ack overdue, acknowledged, stale, coverage) linking to the queue with filters; members, destinations with fallback. */
export function TeamOverviewPage({ id }: { id: string }) {
  const { t } = useTranslation();
  const overview = useQuery(teamOverviewQuery(id));
  const destinations = useQuery(destinationsQuery());
  const integrations = useQuery(integrationsQuery());
  if (overview.isPending) return <p className="p-4 text-ink-2">{t('detail.loading')}</p>;
  if (overview.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={overview.error as unknown as Problem} />
      </div>
    );
  }
  const o = overview.data;
  const teamDestinations = (destinations.data ?? []).filter((d) => d.teamId === id);
  const byId = new Map((destinations.data ?? []).map((d) => [d.id, d]));
  const teamIntegrations = (integrations.data ?? []).filter((i) => i.ownerTeamId === id);
  const cards: { key: string; count: number; search: Record<string, unknown>; tone?: string }[] = [
    { key: 'unassigned', count: o.unassigned, search: { view: 'unassigned', team: id } },
    { key: 'ackOverdue', count: o.ackOverdue, search: { view: 'needsAttention', team: id }, tone: o.ackOverdue > 0 ? 'var(--sev-critical)' : undefined },
    { key: 'acknowledged', count: o.acknowledgedActive, search: { view: 'acknowledged', team: id } },
    { key: 'stale', count: o.stale, search: { view: 'stale', team: id }, tone: o.stale > 0 ? 'var(--sev-high)' : undefined },
    { key: 'open', count: o.openTotal, search: { view: 'myTeams', team: id } },
  ];

  return (
    <div className="h-full overflow-y-auto bg-surface">
      <div className="mx-auto grid max-w-5xl gap-4 px-4 py-4 text-sm">
        <header className="flex flex-wrap items-center gap-2">
          <h1 className="m-0 text-md" data-testid="team-title">
            {o.team.name}
          </h1>
          {o.team.isTriage && <span className="badge">{t('teams.triage')}</span>}
          <span className="mono text-xs text-ink-2">{o.team.accessScopes.join(', ')}</span>
          <span className="ml-auto text-xs text-ink-2">{t('teams.members', { count: o.team.memberCount })}</span>
        </header>

        <div className="grid gap-2 sm:grid-cols-3 lg:grid-cols-5" data-testid="team-cards">
          {cards.map((c) => (
            <Link key={c.key} to="/queue" search={c.search as never} className="rounded-md border border-line bg-surface-2 p-3 text-ink hover:no-underline hover:border-accent" data-card={c.key}>
              <span className="block text-2xl font-semibold" style={c.tone ? { color: c.tone } : undefined}>
                {c.count}
              </span>
              <span className="block text-xs text-ink-2">{t(`teams.cards.${c.key}`)}</span>
            </Link>
          ))}
        </div>

        <section>
          <h2 className="m-0 text-xs text-ink-2">{t('teams.coverage')}</h2>
          {teamIntegrations.length === 0 ? (
            <p className="m-0 mt-1 text-ink-2">{t('teams.noIntegrations')}</p>
          ) : (
            <ul className="m-0 mt-1 grid list-none gap-1 p-0 sm:grid-cols-2">
              {teamIntegrations.map((i) => (
                <li key={i.id} className="flex items-center gap-2 rounded-md border border-line p-2">
                  <Link to="/integrations/$id" params={{ id: i.id }} className="text-ink hover:underline">
                    {i.name}
                  </Link>
                  <span className="ml-auto">
                    <IntegrationHealthBadge id={i.id} />
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section>
          <h2 className="m-0 text-xs text-ink-2">{t('teams.destinations')}</h2>
          {teamDestinations.length === 0 ? (
            <p className="m-0 mt-1 text-ink-2">
              {t('teams.noDestinations')}{' '}
              <Link to="/destinations/new" className="underline">
                {t('destinations.new')}
              </Link>
            </p>
          ) : (
            <table className="mt-1 w-full border-collapse text-sm">
              <tbody>
                {teamDestinations.map((d) => (
                  <tr key={d.id} className="border-t border-line">
                    <td className="py-1">
                      <Link to="/destinations/$id" params={{ id: d.id }} className="text-ink hover:underline">
                        {d.name}
                      </Link>
                      {!d.active && <span className="badge ml-2 border-dashed">{t('destinations.inactive')}</span>}
                    </td>
                    <td className="py-1 text-ink-2">{d.channelType === 'webhook' ? 'Webhook' : 'Email'}</td>
                    <td className="py-1 text-ink-2">
                      {t('teams.fallback')} {byId.get(d.fallbackDestinationId)?.name ?? d.fallbackDestinationId.slice(0, 8)}
                    </td>
                    <td className="py-1 text-right text-xs text-ink-2">
                      <RelativeTime iso={d.lastSuccessAt} mode="sentence" />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <div className="grid gap-4 lg:grid-cols-2">
          <ItemList title={t('teams.cards.unassigned')} items={o.unassignedItems} testId="team-unassigned" />
          <ItemList title={t('teams.cards.ackOverdue')} items={o.overdueItems} testId="team-overdue" />
        </div>
      </div>
    </div>
  );
}

function ItemList({ title, items, testId }: { title: string; items: EpisodeListItem[]; testId: string }) {
  const { t } = useTranslation();
  return (
    <section data-testid={testId}>
      <h2 className="m-0 text-xs text-ink-2">{title}</h2>
      {items.length === 0 ? (
        <p className="m-0 mt-1 text-ink-2">{t('teams.none')}</p>
      ) : (
        <ul className="m-0 mt-1 list-none p-0">
          {items.map((i) => (
            <li key={i.id} className="flex items-center gap-2 border-t border-line py-1">
              <SeverityBadge severity={i.severity} compact />
              <Link to="/episodes/$id" params={{ id: i.id }} className="min-w-0 flex-1 truncate text-ink hover:underline">
                {i.summary ?? i.id}
              </Link>
              <RelativeTime iso={i.lastSeen} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
