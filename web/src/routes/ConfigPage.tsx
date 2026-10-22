import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { P, can } from '@/auth/permissions';
import { useMe } from '@/auth/queries';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { YamlEditor } from '@/components/YamlEditor';
import { configExportQuery, useImportConfig, type ConfigImportEntry, type ConfigImportResponse } from '@/policies/queries';

/** 06 §8 config-as-code: export every active policy as one YAML bundle; import with a dry-run diff before anything is activated. */
export function ConfigPage() {
  const { t } = useTranslation();
  const me = useMe();
  const canManage = can(me.data, P.policyManage);
  const exported = useQuery(configExportQuery());
  const [bundle, setBundle] = useState('');
  const [preview, setPreview] = useState<ConfigImportResponse | null>(null);
  const [applied, setApplied] = useState<ConfigImportResponse | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [copied, setCopied] = useState(false);
  const importConfig = useImportConfig();
  const canApply = !!preview && preview.hasChanges && preview.errors.length === 0 && bundle.trim().length > 0;

  const run = (dryRun: boolean) => {
    setProblem(null);
    importConfig.mutate(
      { yaml: bundle, dryRun },
      {
        onSuccess: (result) => {
          if (dryRun) {
            setPreview(result);
            setApplied(null);
          } else {
            setApplied(result);
            setPreview(null);
            setConfirm(false);
            void exported.refetch();
          }
        },
        onError: (e) => {
          setConfirm(false);
          setProblem(e as unknown as Problem);
        },
      },
    );
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(exported.data ?? '');
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  };

  const loadFile = (file: File | undefined) => {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      setBundle(typeof reader.result === 'string' ? reader.result : '');
      setPreview(null);
      setApplied(null);
    };
    reader.readAsText(file);
  };

  return (
    <div className="h-full overflow-y-auto bg-surface">
      <div className="mx-auto grid max-w-6xl gap-4 px-4 py-4 text-sm">
        <header className="flex flex-wrap items-center gap-2">
          <h1 className="m-0 text-md">{t('config.title')}</h1>
          <p className="m-0 text-xs text-ink-2">{t('config.about')}</p>
          <Link to="/policies/$kind" params={{ kind: 'routing' }} className="ml-auto text-xs underline">
            {t('policies.title')}
          </Link>
        </header>

        <div className="grid gap-4 lg:grid-cols-2">
          <section className="grid gap-2" data-testid="config-export">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="m-0 text-xs text-ink-2">{t('config.export')}</h2>
              <span className="ml-auto flex gap-1">
                <button type="button" className="btn btn-sm" onClick={() => void exported.refetch()} disabled={exported.isFetching}>
                  {t('config.refresh')}
                </button>
                <button type="button" className="btn btn-sm" onClick={() => void copy()} disabled={!exported.data} data-testid="config-copy">
                  {copied ? t('config.copied') : t('config.copy')}
                </button>
                {canManage && (
                  <button
                    type="button"
                    className="btn btn-sm"
                    onClick={() => {
                      setBundle(exported.data ?? '');
                      setPreview(null);
                      setApplied(null);
                    }}
                    disabled={!exported.data}
                    data-testid="config-use-export"
                  >
                    {t('config.useAsImport')}
                  </button>
                )}
              </span>
            </div>
            {exported.isError && <ProblemBanner problem={exported.error as unknown as Problem} />}
            <YamlEditor value={exported.data ?? ''} readOnly height="520px" label={t('config.export')} testId="config-export-editor" />
            <p className="m-0 text-xs text-ink-2">{t('config.exportHint')}</p>
          </section>

          <section className="grid gap-2" data-testid="config-import">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="m-0 text-xs text-ink-2">{t('config.import')}</h2>
              {canManage && (
                <span className="ml-auto flex flex-wrap items-center gap-1">
                  <label className="btn btn-sm cursor-pointer">
                    {t('config.chooseFile')}
                    <input type="file" accept=".yaml,.yml,text/yaml,application/yaml" className="sr-only" onChange={(e) => loadFile(e.target.files?.[0])} />
                  </label>
                  <button type="button" className="btn btn-sm" onClick={() => run(true)} disabled={importConfig.isPending || bundle.trim().length === 0} data-testid="config-dry-run">
                    {t('config.dryRun')}
                  </button>
                  <button type="button" className="btn btn-sm btn-primary" onClick={() => setConfirm(true)} disabled={!canApply || importConfig.isPending} data-testid="config-apply" title={canApply ? undefined : t('config.applyHint')}>
                    {t('config.apply')}
                  </button>
                </span>
              )}
            </div>
            {canManage ? (
              <YamlEditor
                value={bundle}
                onChange={(v) => {
                  setBundle(v);
                  setPreview(null);
                  setApplied(null);
                }}
                height="320px"
                label={t('config.import')}
                testId="config-import-editor"
              />
            ) : (
              <p className="m-0 text-ink-2">{t('config.importNeedsPermission')}</p>
            )}
            {problem && <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />}
            {preview && <ImportResult result={preview} title={t('config.previewTitle')} />}
            {applied && <ImportResult result={applied} title={t('config.appliedTitle')} />}
          </section>
        </div>
      </div>

      <ConfirmDialog
        open={confirm}
        onOpenChange={setConfirm}
        title={t('config.confirmTitle', { count: preview?.entries.filter((e) => e.action !== 'unchanged').length ?? 0 })}
        description={t('config.confirmWhy')}
        verb={t('config.apply')}
        busy={importConfig.isPending}
        onConfirm={() => run(false)}
      />
    </div>
  );
}

function ImportResult({ result, title }: { result: ConfigImportResponse; title: string }) {
  const { t } = useTranslation();
  const changed = result.entries.filter((e) => e.action !== 'unchanged');
  const unchanged = result.entries.length - changed.length;
  return (
    <div className="rounded-md border border-line" data-testid="config-result" data-dry-run={result.dryRun} data-has-changes={result.hasChanges}>
      <div className="flex h-7 items-center gap-2 border-b border-line px-2 text-xs text-ink-2">
        <span>{title}</span>
        <span className="ml-auto">{t('config.summary', { changed: changed.length, unchanged })}</span>
      </div>
      {result.errors.length > 0 && (
        <ul className="m-0 list-none p-2 text-xs" role="alert" style={{ color: 'var(--sev-critical)' }} data-testid="config-errors">
          {result.errors.map((e) => (
            <li key={e} className="mono">
              {e}
            </li>
          ))}
        </ul>
      )}
      {result.entries.length > 0 && (
        <table className="w-full border-collapse text-xs">
          <thead className="text-left text-ink-2">
            <tr className="h-7 border-b border-line">
              <th className="pl-2 font-normal">{t('policies.kind')}</th>
              <th className="font-normal">{t('policies.columns.name')}</th>
              <th className="font-normal">{t('config.action')}</th>
              <th className="pr-2 font-normal">{t('config.changes')}</th>
            </tr>
          </thead>
          <tbody>
            {result.entries.map((e) => (
              <ImportRow key={`${e.kind}:${e.policyId}`} entry={e} />
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function ImportRow({ entry }: { entry: ConfigImportEntry }) {
  const { t } = useTranslation();
  return (
    <tr className="border-b border-line align-top" data-action={entry.action}>
      <td className="py-1 pl-2">{t(`policies.kinds.${entry.kind}`)}</td>
      <td className="py-1">
        <Link to="/policies/$kind/$id" params={{ kind: entry.kind, id: entry.policyId }} className="text-ink hover:underline">
          {entry.name ?? t('policies.unnamed')}
        </Link>
        <span className="mono ml-2 text-ink-2">{entry.policyId.slice(0, 8)}</span>
      </td>
      <td className="py-1">
        <span className={`badge ${entry.action === 'unchanged' ? 'border-dashed' : ''}`}>
          {t(`config.actions.${entry.action}`)}
          {entry.version != null ? ` v${entry.version}` : ''}
        </span>
      </td>
      <td className="mono py-1 pr-2 text-ink-2">{entry.changes.join(', ') || '—'}</td>
    </tr>
  );
}
