import { Link, Outlet, useNavigate } from '@tanstack/react-router';
import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { authEvents } from '@/api/client';
import { meQuery, useLogout } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { countsQuery, teamsQuery } from '@/episodes/queries';
import { FreshnessBar } from '@/components/FreshnessBar';
import { RealtimeProvider } from '@/realtime/RealtimeProvider';
import { QUEUE_VIEWS } from '@/api/types';
import { getDensity, getTheme, setDensity, setTheme, type Density, type Theme } from './preferences';

/** Fixed 240 px rail, work surface, FreshnessBar at the bottom (08 §0 Layout). */
export function Shell() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const me = useQuery(meQuery());
  const counts = useQuery(countsQuery());
  const teams = useQuery({ ...teamsQuery(), enabled: !!me.data });
  const logout = useLogout();

  useEffect(() => {
    const onUnauthorized = () => {
      void navigate({ to: '/login', search: { returnTo: `${location.pathname}${location.search}` } });
    };
    authEvents.addEventListener('unauthorized', onUnauthorized);
    return () => authEvents.removeEventListener('unauthorized', onUnauthorized);
  }, [navigate]);

  return (
    <RealtimeProvider enabled={!!me.data}>
      <div className="grid h-dvh grid-rows-[1fr_auto]">
        <div className="grid min-h-0 grid-cols-[var(--rail-width)_1fr] max-md:grid-cols-1">
          <nav aria-label="Primary" className="flex min-h-0 flex-col border-r border-line bg-surface-2 max-md:hidden">
            <div className="flex h-11 items-center px-3 text-base font-semibold">{t('app.name')}</div>
            <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
              <RailHeading>{t('nav.views')}</RailHeading>
              <ul className="m-0 list-none p-0">
                {QUEUE_VIEWS.filter((v) => v !== 'closed' || can(me.data, P.historyRead)).map((view) => (
                  <li key={view}>
                    <Link
                      to="/queue"
                      search={{ view }}
                      className="flex h-7 items-center justify-between rounded-sm px-2 text-sm text-ink hover:bg-surface hover:no-underline"
                      activeProps={{ className: 'bg-surface font-semibold' }}
                      activeOptions={{ includeSearch: true, exact: false }}
                    >
                      <span>{t(`views.${view}`)}</span>
                      <span className="text-xs text-ink-2">{counts.data?.[view] ?? ''}</span>
                    </Link>
                  </li>
                ))}
              </ul>
              {teams.data && teams.data.length > 0 && (
                <>
                  <RailHeading>{t('nav.teams')}</RailHeading>
                  <ul className="m-0 list-none p-0">
                    {teams.data.map((team) => (
                      <li key={team.id}>
                        <Link
                          to="/queue"
                          search={{ view: 'needsAttention', team: team.id }}
                          className="flex h-7 items-center rounded-sm px-2 text-sm text-ink hover:bg-surface hover:no-underline"
                        >
                          {team.name}
                        </Link>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </div>
            <div className="border-t border-line p-2 text-xs text-ink-2">
              <div className="truncate px-1" title={me.data?.username}>
                {me.data?.displayName}
              </div>
              <div className="mt-1 flex flex-wrap gap-1 px-1">
                <PrefSelect label={t('nav.theme')} value={getTheme()} options={['system', 'light', 'dark']} onChange={(v) => setTheme(v as Theme)} />
                <PrefSelect label={t('nav.density')} value={getDensity()} options={['compact', 'comfortable']} onChange={(v) => setDensity(v as Density)} />
              </div>
              <button
                type="button"
                className="btn btn-sm mt-2 w-full"
                onClick={() => {
                  logout.mutate(undefined, { onSettled: () => void navigate({ to: '/login', search: { returnTo: undefined } }) });
                }}
              >
                {t('nav.signOut')}
              </button>
            </div>
          </nav>
          <main className="min-h-0 min-w-0 overflow-hidden bg-bg">
            <Outlet />
          </main>
        </div>
        <FreshnessBar />
      </div>
    </RealtimeProvider>
  );
}

function RailHeading({ children }: { children: string }) {
  return <div className="mt-3 mb-1 px-2 text-xs text-ink-2">{children}</div>;
}

function PrefSelect({ label, value, options, onChange }: { label: string; value: string; options: string[]; onChange: (v: string) => void }) {
  return (
    <label className="flex items-center gap-1">
      <span>{label}</span>
      <select className="h-6 rounded-sm border border-line bg-surface px-1 text-xs" defaultValue={value} onChange={(e) => onChange(e.target.value)}>
        {options.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </select>
    </label>
  );
}
