import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import {
  useCreateEnvironment,
  useDeleteEnvironment,
  useEnvironments,
  useUpdateEnvironment,
  validateEnvironment,
  type EnvironmentDto,
  type EnvironmentFormError,
} from '@/environments/queries';

type Panel = { mode: 'create' } | { mode: 'edit'; env: EnvironmentDto } | null;

/**
 * Environment dictionary (ADR-17). Everyone who can see episodes reads it; changing it needs `policy.manage`, because
 * environment classes are inputs to routing and lifecycle policy. Keys are immutable, so editing changes the label and order only.
 */
export function EnvironmentsPage() {
  const { t } = useTranslation();
  const me = useMe();
  const list = useEnvironments();
  const remove = useDeleteEnvironment();
  const [panel, setPanel] = useState<Panel>(null);
  const [deleting, setDeleting] = useState<EnvironmentDto | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const canManage = can(me.data, P.policyManage);

  const confirmDelete = () => {
    if (!deleting) return;
    setProblem(null);
    remove.mutate(
      { key: deleting.key, version: deleting.version },
      {
        onSuccess: () => setDeleting(null),
        onError: (err) => {
          setDeleting(null);
          setProblem(err as unknown as Problem);
        },
      },
    );
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('environmentsAdmin.title')}</h1>
        <span className="text-xs text-ink-2">{t('environmentsAdmin.subtitle')}</span>
        {canManage && (
          <button type="button" className="btn btn-primary ml-auto" onClick={() => setPanel({ mode: 'create' })} data-testid="environment-new">
            {t('environmentsAdmin.new')}
          </button>
        )}
      </div>
      {(list.isError || problem) && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={(problem ?? list.error) as unknown as Problem} onDismiss={problem ? () => setProblem(null) : undefined} />
        </div>
      )}
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_auto]">
        <div className="min-h-0 overflow-auto bg-surface">
          <table className="w-full border-collapse text-sm">
            <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
              <tr className="h-8 border-b border-line">
                <th className="pl-3 font-normal">{t('environmentsAdmin.columns.key')}</th>
                <th className="font-normal">{t('environmentsAdmin.columns.label')}</th>
                <th className="font-normal">{t('environmentsAdmin.columns.order')}</th>
                <th className="font-normal">{t('environmentsAdmin.columns.defaultView')}</th>
                <th className="font-normal">{t('environmentsAdmin.columns.type')}</th>
                <th className="pr-3" />
              </tr>
            </thead>
            <tbody>
              {list.data?.map((env) => (
                <tr key={env.key} className="border-b border-line hover:bg-surface-2" data-environment-key={env.key}>
                  <td className="py-2 pl-3 font-mono text-xs">{env.key}</td>
                  <td className="py-2">{env.label}</td>
                  <td className="py-2 text-ink-2 tabular-nums">{env.sortOrder}</td>
                  <td className="py-2">{env.inDefaultView ? <span className="badge">{t('environmentsAdmin.inDefaultView')}</span> : <span className="text-ink-2">—</span>}</td>
                  <td className="py-2">{env.builtIn ? <span className="badge font-semibold">{t('environmentsAdmin.builtIn')}</span> : <span className="text-ink-2">{t('environmentsAdmin.configured')}</span>}</td>
                  <td className="py-2 pr-3 text-right whitespace-nowrap">
                    {canManage && (
                      <>
                        <button type="button" className="btn btn-sm" onClick={() => setPanel({ mode: 'edit', env })} data-testid={`environment-edit-${env.key}`}>
                          {t('environmentsAdmin.edit')}
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm ml-1"
                          onClick={() => setDeleting(env)}
                          disabled={env.builtIn}
                          title={env.builtIn ? t('environmentsAdmin.builtInHint') : undefined}
                          data-testid={`environment-delete-${env.key}`}
                        >
                          {t('environmentsAdmin.delete')}
                        </button>
                      </>
                    )}
                    <span className="ml-2 text-xs text-ink-2">v{env.version}</span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="px-3 py-3 text-xs text-ink-2">{t('environmentsAdmin.help')}</p>
        </div>
        {panel && <EnvironmentForm key={panel.mode === 'edit' ? panel.env.key : 'new'} panel={panel} onDone={() => setPanel(null)} />}
      </div>
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t('environmentsAdmin.deleteTitle', { key: deleting?.key ?? '' })}
        description={t('environmentsAdmin.deleteWhy')}
        verb={t('environmentsAdmin.delete')}
        busy={remove.isPending}
        onConfirm={confirmDelete}
      />
    </div>
  );
}

function EnvironmentForm({ panel, onDone }: { panel: NonNullable<Panel>; onDone: () => void }) {
  const { t } = useTranslation();
  const create = useCreateEnvironment();
  const update = useUpdateEnvironment();
  const editing = panel.mode === 'edit' ? panel.env : null;
  const [key, setKey] = useState('');
  const [label, setLabel] = useState(editing?.label ?? '');
  const [sortOrder, setSortOrder] = useState(editing ? String(editing.sortOrder) : '');
  const [invalid, setInvalid] = useState<EnvironmentFormError>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const pending = create.isPending || update.isPending;
  const title = editing ? t('environmentsAdmin.editTitle', { key: editing.key }) : t('environmentsAdmin.new');

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setProblem(null);
    const bad = validateEnvironment({ key: editing ? undefined : key, label, sortOrder });
    setInvalid(bad);
    if (bad) return;
    const order = sortOrder.trim() === '' ? undefined : Number(sortOrder.trim());
    const onError = (err: unknown) => setProblem(err as Problem);
    if (editing) {
      update.mutate({ key: editing.key, version: editing.version, body: { label: label.trim(), sortOrder: order } }, { onSuccess: onDone, onError });
    } else {
      create.mutate({ key, label: label.trim(), sortOrder: order }, { onSuccess: onDone, onError });
    }
  };

  return (
    <form onSubmit={submit} className="w-[min(420px,100vw)] overflow-y-auto border-l border-line bg-surface p-4 text-sm" aria-label={title}>
      <h2 className="m-0 text-base">{title}</h2>
      {problem && (
        <div className="mt-3">
          <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
        </div>
      )}
      {!editing && (
        <label className="mt-3 block">
          <span className="block text-xs text-ink-2">{t('environmentsAdmin.form.key')}</span>
          <input
            className="input mt-1 w-full font-mono"
            value={key}
            onChange={(e) => setKey(e.target.value.trim().toLowerCase())}
            aria-invalid={invalid === 'key'}
            placeholder="uat"
            data-testid="environment-key"
            autoFocus
          />
          <span className="mt-1 block text-xs text-ink-2">{t('environmentsAdmin.form.keyHelp')}</span>
        </label>
      )}
      <label className="mt-3 block">
        <span className="block text-xs text-ink-2">{t('environmentsAdmin.form.label')}</span>
        <input
          className="input mt-1 w-full"
          value={label}
          onChange={(e) => setLabel(e.target.value)}
          aria-invalid={invalid === 'label'}
          placeholder="UAT"
          data-testid="environment-label"
          autoFocus={Boolean(editing)}
        />
      </label>
      <label className="mt-3 block">
        <span className="block text-xs text-ink-2">{t('environmentsAdmin.form.order')}</span>
        <input
          className="input mt-1 w-32"
          inputMode="numeric"
          value={sortOrder}
          onChange={(e) => setSortOrder(e.target.value)}
          aria-invalid={invalid === 'sortOrder'}
          data-testid="environment-order"
        />
        <span className="mt-1 block text-xs text-ink-2">{t('environmentsAdmin.form.orderHelp')}</span>
      </label>
      {invalid && <p className="mt-3 text-xs text-sev-critical" role="alert">{t(`environmentsAdmin.form.invalid.${invalid}`)}</p>}
      {!editing && <p className="mt-3 text-xs text-ink-2">{t('environmentsAdmin.form.afterCreate')}</p>}
      <div className="mt-4 flex gap-2">
        <button type="submit" className="btn btn-primary" disabled={pending} data-testid="environment-save">
          {pending ? t('environmentsAdmin.form.saving') : editing ? t('environmentsAdmin.form.save') : t('environmentsAdmin.form.create')}
        </button>
        <button type="button" className="btn" onClick={onDone}>
          {t('actions.cancel')}
        </button>
      </div>
    </form>
  );
}
