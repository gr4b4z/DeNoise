import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';

export function ForbiddenPage() {
  const { t } = useTranslation();
  return (
    <main className="p-8">
      <h1 className="text-lg">403</h1>
      <p className="text-sm text-ink-2">{t('errors.forbidden')}</p>
      <Link to="/queue" search={{ view: 'needsAttention' }}>
        {t('detail.back')}
      </Link>
    </main>
  );
}

export function NotFoundPage() {
  const { t } = useTranslation();
  return (
    <main className="p-8">
      <h1 className="text-lg">404</h1>
      <p className="text-sm text-ink-2">{t('errors.notFound')}</p>
      <Link to="/queue" search={{ view: 'needsAttention' }}>
        {t('detail.back')}
      </Link>
    </main>
  );
}
