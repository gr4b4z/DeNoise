import { useNavigate, useSearch } from '@tanstack/react-router';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useChangePassword } from '@/auth/queries';
import type { Problem } from '@/api/types';
import { ProblemBanner } from '@/components/ProblemBanner';

/** Forced change after first login (08 §3.8): one sentence of why, inline feedback, straight back to the return URL. */
export function ChangePasswordPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { returnTo } = useSearch({ from: '/login/change-password' });
  const change = useChangePassword();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [problem, setProblem] = useState<Problem | null>(null);

  const mismatch = confirm.length > 0 && next !== confirm;
  const tooShort = next.length > 0 && next.length < 12;

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (mismatch || tooShort) return;
    setProblem(null);
    change.mutate(
      { current, new: next },
      {
        onSuccess: () => void navigate({ to: returnTo?.startsWith('/') ? returnTo : '/queue', search: returnTo ? undefined : { view: 'needsAttention' } } as never),
        onError: (err) => setProblem(err as unknown as Problem),
      },
    );
  };

  return (
    <main className="flex min-h-dvh items-start justify-center bg-bg px-4 pt-[18vh]">
      <form onSubmit={submit} className="w-full max-w-[360px]" aria-labelledby="change-title">
        <h1 id="change-title" className="text-xl font-semibold">
          {t('login.changeTitle')}
        </h1>
        <p className="mt-2 text-sm text-ink-2">{t('login.changeWhy')}</p>
        <label className="mt-4 block text-sm">
          <span className="text-ink-2">{t('login.current')}</span>
          <input className="input mt-1" type="password" autoComplete="current-password" autoFocus value={current} onChange={(e) => setCurrent(e.target.value)} required />
        </label>
        <label className="mt-3 block text-sm">
          <span className="text-ink-2">{t('login.new')}</span>
          <input className="input mt-1" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required aria-describedby="new-hint" />
          <span id="new-hint" className="text-xs text-ink-2">
            {tooShort ? t('login.tooShort') : ' '}
          </span>
        </label>
        <label className="mt-1 block text-sm">
          <span className="text-ink-2">{t('login.confirm')}</span>
          <input className="input mt-1" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
          <span className="text-xs text-ink-2">{mismatch ? t('login.mismatch') : ' '}</span>
        </label>
        <div className="mt-2 min-h-6" aria-live="polite">
          <ProblemBanner problem={problem} />
        </div>
        <button type="submit" className="btn btn-primary mt-2 w-full justify-center" disabled={change.isPending || mismatch || tooShort}>
          {t('login.setNew')}
        </button>
      </form>
    </main>
  );
}
