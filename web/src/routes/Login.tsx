import { useNavigate, useSearch } from '@tanstack/react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { meQuery, providersQuery, useLogin } from '@/auth/queries';
import type { Problem } from '@/api/types';
import { ProblemBanner } from '@/components/ProblemBanner';

/** 08 §3.8: centred single column, one form, inline errors that repeat what the server said, no marketing copy. */
export function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const { returnTo } = useSearch({ from: '/login' });
  const providers = useQuery(providersQuery());
  const login = useLogin();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [keepSignedIn, setKeepSignedIn] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);

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
              await navigate({ to: returnTo?.startsWith('/') ? returnTo : '/queue', search: returnTo ? undefined : { view: 'needsAttention' } } as never);
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
        {providers.data?.oidc && (
          <>
            <a className="btn mt-6 w-full justify-center" href={providers.data.oidc.loginUrl}>
              {t('login.sso')}
            </a>
            <hr className="my-4 border-0 border-t border-line" />
          </>
        )}
        <label className="mt-6 block text-sm">
          <span className="text-ink-2">{t('login.username')}</span>
          <input className="input mt-1" name="username" autoComplete="username" autoFocus value={username} onChange={(e) => setUsername(e.target.value)} required />
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
        <button type="submit" className="btn btn-primary mt-2 w-full justify-center" disabled={login.isPending}>
          {t('login.signIn')}
        </button>
      </form>
    </main>
  );
}
