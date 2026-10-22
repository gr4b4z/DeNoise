import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { BUILTIN, templatesQuery, type WebhookTemplate } from '@/notifications/queries';

export function BuiltinBadge() {
  const { t } = useTranslation();
  return (
    <span className="badge">
      <span aria-hidden="true">🔒</span>
      {t('templates.builtin')}
    </span>
  );
}

/** 08 §3.7b template list: active version of every template, built-in badge; built-ins open read-only with a Clone action. */
export function TemplatesPage() {
  const { t } = useTranslation();
  const me = useMe();
  const list = useQuery(templatesQuery());
  const canManage = can(me.data, P.destinationManage);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('templates.title')}</h1>
        <Link to="/destinations" className="btn btn-sm hover:no-underline">
          {t('destinations.title')}
        </Link>
        {canManage && (
          <Link to="/templates/new" search={{}} className="btn btn-primary ml-auto hover:no-underline">
            {t('templates.new')}
          </Link>
        )}
      </div>
      {list.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={list.error as unknown as Problem} />
        </div>
      )}
      <div className="min-h-0 flex-1 overflow-auto bg-surface">
        <table className="w-full border-collapse text-sm">
          <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
            <tr className="h-8 border-b border-line">
              <th className="pl-3 font-normal">{t('templates.columns.name')}</th>
              <th className="font-normal">{t('templates.columns.format')}</th>
              <th className="font-normal">{t('templates.columns.contentType')}</th>
              <th className="font-normal">{t('templates.columns.version')}</th>
              <th className="font-normal">{t('templates.columns.activated')}</th>
              <th className="pr-3 font-normal">{t('templates.columns.description')}</th>
            </tr>
          </thead>
          <tbody>
            {list.data?.map((x: WebhookTemplate) => (
              <tr key={x.id} className="h-9 border-b border-line hover:bg-surface-2" data-template-id={x.id} data-builtin={x.builtin}>
                <td className="pl-3">
                  <Link to="/templates/$id" params={{ id: x.id }} className="text-ink hover:underline">
                    {x.name}
                  </Link>
                  {x.builtin && (
                    <span className="ml-2">
                      <BuiltinBadge />
                    </span>
                  )}
                  {x.id === BUILTIN.genericJson && <span className="ml-2 text-xs text-ink-2">{t('templates.genericNote')}</span>}
                </td>
                <td className="mono text-ink-2">{x.format}</td>
                <td className="mono text-xs text-ink-2">{x.contentType}</td>
                <td className="text-ink-2">v{x.version}</td>
                <td>
                  <RelativeTime iso={x.activatedAt} mode="sentence" />
                </td>
                <td className="pr-3 text-xs text-ink-2">{x.description}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
