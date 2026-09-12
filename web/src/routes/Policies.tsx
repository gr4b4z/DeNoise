import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { P, can } from '@/auth/permissions';
import { useMe } from '@/auth/queries';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { activePoliciesQuery, POLICY_KINDS, SINGLE_DOCUMENT_KINDS, type PolicyKind } from '@/policies/queries';

/** 08 §3.6 policies per kind: active documents with their version; the editor holds versions, diff, impact and rollback. */
export function PoliciesPage({ kind }: { kind: PolicyKind }) {
  const { t } = useTranslation();
  const me = useMe();
  const canManage = can(me.data, P.policyManage);
  const active = useQuery(activePoliciesQuery(kind));
  const single = SINGLE_DOCUMENT_KINDS.includes(kind);
  const canCreate = canManage && (!single || (active.data ?? []).length === 0);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('policies.title')}</h1>
        <div role="tablist" aria-label={t('policies.kind')} className="flex flex-wrap gap-1">
          {POLICY_KINDS.map((k) => (
            <Link key={k} role="tab" aria-selected={k === kind} to="/policies/$kind" params={{ kind: k }} className={`btn btn-sm hover:no-underline ${k === kind ? 'bg-surface-2 font-semibold' : ''}`} data-testid={`policy-kind-${k}`}>
              {t(`policies.kinds.${k}`)}
            </Link>
          ))}
        </div>
        <span className="ml-auto flex gap-1">
          <Link to="/config" className="btn btn-sm hover:no-underline">
            {t('config.title')}
          </Link>
          {canCreate && (
            <Link to="/policies/$kind/$id" params={{ kind, id: 'new' }} className="btn btn-sm btn-primary hover:no-underline" data-testid="policy-new">
              {t('policies.new')}
            </Link>
          )}
        </span>
      </div>
      <p className="m-0 border-b border-line bg-surface px-3 py-2 text-xs text-ink-2">{t(`policies.about.${kind}`)}</p>
      {active.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={active.error as unknown as Problem} />
        </div>
      )}
      {active.data?.length === 0 ? (
        <EmptyState variant="filtered" title={t('policies.empty', { kind: t(`policies.kinds.${kind}`) })} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto bg-surface" data-testid="policies-table">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('policies.columns.name')}</th>
                <th className="font-normal">{t('policies.columns.id')}</th>
                <th className="font-normal">{t('policies.columns.version')}</th>
                <th className="font-normal">{t('policies.columns.activated')}</th>
                <th className="pr-3 font-normal">{t('policies.columns.createdBy')}</th>
              </tr>
            </thead>
            <tbody>
              {(active.data ?? []).map((p) => (
                <tr key={p.id} className="h-9 border-b border-line hover:bg-surface-2" data-policy-id={p.id}>
                  <td className="pl-3">
                    <Link to="/policies/$kind/$id" params={{ kind, id: p.id }} className="text-ink hover:underline">
                      {p.name ?? t('policies.unnamed')}
                    </Link>
                  </td>
                  <td className="mono text-xs text-ink-2">{p.id}</td>
                  <td>
                    <span className="badge">v{p.version}</span>
                  </td>
                  <td className="text-ink-2">
                    <RelativeTime iso={p.activatedAt} mode="sentence" />
                  </td>
                  <td className="pr-3 text-ink-2">{p.createdBy ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
