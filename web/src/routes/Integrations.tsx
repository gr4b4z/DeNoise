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
import { healthTone, INTEGRATION_TYPES, integrationHealthQuery, integrationsQuery, type IntegrationSummary } from '@/integrations/queries';

const TONE: Record<'ok' | 'warn' | 'bad' | 'muted', { glyph: string; style?: React.CSSProperties }> = {
  ok: { glyph: '✓', style: { color: 'var(--sev-low)', borderColor: 'var(--sev-low)' } },
  warn: { glyph: '!', style: { color: 'var(--sev-high)', borderColor: 'var(--sev-high)' } },
  bad: { glyph: '✕', style: { color: 'var(--sev-critical)', borderColor: 'var(--sev-critical)' } },
  muted: { glyph: '?' },
};

export function typeLabel(type: string): string {
  return INTEGRATION_TYPES.find((t) => t.value === type)?.label ?? type;
}

/** Health badge — text + glyph, colour never alone (WCAG 1.4.1). */
export function IntegrationHealthBadge({ id }: { id: string }) {
  const health = useQuery(integrationHealthQuery(id));
  const { state, tone } = healthTone(health.data);
  const t = TONE[tone];
  return (
    <span className={`badge ${tone === 'muted' ? 'border-dashed' : ''}`} style={t.style} data-health={tone} data-testid="integration-health">
      <span aria-hidden="true">{t.glyph}</span>
      {state}
    </span>
  );
}

function HealthCells({ id }: { id: string }) {
  const { t } = useTranslation();
  const health = useQuery(integrationHealthQuery(id));
  const h = health.data;
  const rate = h && h.acceptedLast15m > 0 ? Math.round((h.mappingFailuresLast15m / h.acceptedLast15m) * 100) : 0;
  return (
    <>
      <td>
        <RelativeTime iso={h?.lastProcessedAlertAt} mode="sentence" />
      </td>
      <td className={rate > 0 ? '' : 'text-ink-2'} style={rate > 0 ? { color: 'var(--sev-high)' } : undefined}>
        {h ? (h.mappingFailuresLast15m > 0 ? t('integrations.failureRate', { rate, count: h.mappingFailuresLast15m }) : '0') : '…'}
      </td>
      <td className="text-ink-2">{h?.oldestPendingSince ? <RelativeTime iso={h.oldestPendingSince} mode="sentence" /> : h ? t('integrations.noBacklog') : '…'}</td>
    </>
  );
}

/** 08 §3.5 list: health badge, last processed alert, mapping failure rate, backlog age. */
export function IntegrationsPage() {
  const { t } = useTranslation();
  const me = useMe();
  const list = useQuery(integrationsQuery());
  const teams = useQuery(teamsQuery());
  const canManage = can(me.data, P.integrationManage);
  const teamName = (id: string | null) => (id ? (teams.data?.find((x) => x.id === id)?.name ?? id.slice(0, 8)) : '—');

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('integrations.title')}</h1>
        {canManage && (
          <Link to="/integrations/new" className="btn btn-primary ml-auto hover:no-underline">
            {t('integrations.new')}
          </Link>
        )}
      </div>
      {list.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={list.error as unknown as Problem} />
        </div>
      )}
      {list.data?.length === 0 ? (
        <EmptyState variant="healthy" title={t('integrations.empty')} detail={t('integrations.emptyDetail')} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto bg-surface">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('integrations.columns.name')}</th>
                <th className="font-normal">{t('integrations.columns.type')}</th>
                <th className="font-normal">{t('integrations.columns.health')}</th>
                <th className="font-normal">{t('integrations.columns.lastProcessed')}</th>
                <th className="font-normal">{t('integrations.columns.failureRate')}</th>
                <th className="font-normal">{t('integrations.columns.backlog')}</th>
                <th className="font-normal max-lg:hidden">{t('integrations.columns.team')}</th>
                <th className="pr-3 font-normal max-lg:hidden">{t('integrations.columns.scope')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data?.map((i: IntegrationSummary) => (
                <tr key={i.id} className="h-9 border-b border-line hover:bg-surface-2" data-integration-id={i.id} data-type={i.type}>
                  <td className="pl-3">
                    <Link to="/integrations/$id" params={{ id: i.id }} className="text-ink hover:underline">
                      {i.name}
                    </Link>
                    {!i.active && <span className="badge ml-2 border-dashed">{t('integrations.inactive')}</span>}
                    {i.shadow && <span className="badge ml-2 border-dashed">{t('integrations.shadow')}</span>}
                  </td>
                  <td className="text-ink-2">{typeLabel(i.type)}</td>
                  <td>
                    <IntegrationHealthBadge id={i.id} />
                  </td>
                  <HealthCells id={i.id} />
                  <td className="text-ink-2 max-lg:hidden">{teamName(i.ownerTeamId)}</td>
                  <td className="mono pr-3 text-xs text-ink-2 max-lg:hidden">{i.accessScope}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
