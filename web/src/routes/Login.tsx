import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { meQuery, providersQuery, useLogin } from '@/auth/queries';
import type { Problem } from '@/api/types';
import { ProblemBanner } from '@/components/ProblemBanner';
import { DEFAULT_QUEUE_SEARCH } from './queueSearch';
import { ssoHref } from './loginLinks';

/**
 * 08 §3.8: centred single column, inline errors that repeat what the server said, no marketing copy. M11b: the SSO button
 * and the password form sit together; with password sign-in disabled (`providers.local` false) the form is behind a
 * break-glass link, never removed. A failed SSO round trip comes back as `?error=sso`, deliberately without a reason.
 */
export function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const { returnTo, error, breakGlass } = useSearch({ from: '/login' });
  const providers = useQuery(providersQuery());
  const login = useLogin();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [keepSignedIn, setKeepSignedIn] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const oidc = providers.data?.oidc ?? null;
  // Until /auth/providers answers, assume local sign-in is on (the pre-M11b behaviour) so the form never flickers away.
  const localEnabled = providers.data?.local ?? true;
  const showForm = localEnabled || breakGlass === true;

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setProblem(null);
    login.mutate(
      { username, password, keepSignedIn },
      {
        onSuccess: () => {
          void (async () => {
            const me = await qc.query(meQuery());
            if (me.mustChangePassword) {
              await navigate({ to: '/login/change-password', search: { returnTo } });
            } else {
              await navigate({ to: returnTo?.startsWith('/') ? returnTo : '/queue', search: returnTo ? undefined : DEFAULT_QUEUE_SEARCH } as never);
            }
          })();
        },
        onError: (err) => setProblem(err as unknown as Problem),
      },
    );
  };

  return (
    <main className="flex min-h-dvh items-start justify-center bg-bg px-4 pt-[18vh]">
      <form onSubmit={submit} className="w-full max-w-[360px]" aria-labelledby="login-title">
        <h1 id="login-title" className="text-xl font-semibold">
          {t('app.name')}
        </h1>
        {error === 'sso' && (
          <div role="alert" className="mt-4 rounded-md border px-3 py-2 text-sm" style={{ borderColor: 'var(--sev-critical)', background: 'color-mix(in srgb, var(--sev-critical) 8%, var(--surface))' }}>
            {t('login.ssoFailed')}
          </div>
        )}
        {oidc && (
          <>
            <a className="btn btn-primary mt-6 w-full justify-center" href={ssoHref(oidc.loginUrl, returnTo)}>
              {t('login.ssoWith', { name: oidc.displayName })}
            </a>
            {showForm && (
              <div className="my-4 flex items-center gap-3 text-xs text-ink-2" aria-hidden="true">
                <hr className="flex-1 border-0 border-t border-line" />
                {t('login.or')}
                <hr className="flex-1 border-0 border-t border-line" />
              </div>
            )}
          </>
        )}
        {!showForm && (
          <p className="mt-6 text-sm text-ink-2">
            {t('login.breakGlassHint')}{' '}
            <Link to="/login" search={{ returnTo, breakGlass: true }} className="font-medium text-ink underline">
              {t('login.breakGlass')}
            </Link>
          </p>
        )}
        {showForm && (
          <>
            {!localEnabled && <p className="mt-6 text-sm text-ink-2">{t('login.breakGlassTitle')}</p>}
            <label className="mt-6 block text-sm">
              <span className="text-ink-2">{t('login.username')}</span>
              <input className="input mt-1" name="username" autoComplete="username" autoFocus={!oidc || breakGlass === true} value={username} onChange={(e) => setUsername(e.target.value)} required />
            </label>
            <label className="mt-3 block text-sm">
              <span className="text-ink-2">{t('login.password')}</span>
              <input className="input mt-1" name="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
            </label>
            <label className="mt-3 flex items-center gap-2 text-sm">
              <input type="checkbox" checked={keepSignedIn} onChange={(e) => setKeepSignedIn(e.target.checked)} />
              {t('login.keepSignedIn')}
            </label>
            <div className="mt-3 min-h-6" aria-live="polite">
              <ProblemBanner problem={problem} />
            </div>
            <button type="submit" className={`btn ${oidc ? '' : 'btn-primary'} mt-2 w-full justify-center`} disabled={login.isPending}>
              {t('login.signIn')}
            </button>
          </>
        )}
      </form>
    </main>
  );
}
