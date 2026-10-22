import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { P, can } from '@/auth/permissions';
import { useMe } from '@/auth/queries';
import { SeverityBadge } from '@/components/Badges';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { YamlEditor } from '@/components/YamlEditor';
import { diffLines } from '@/lib/diff';
import { utc } from '@/lib/time';
import { IMPACT_KINDS, POLICY_SKELETONS, policyImpactQuery, policyVersionsQuery, SINGLE_DOCUMENT_KINDS, useActivatePolicy, useCreatePolicyVersion, useRollbackPolicy, type PolicyKind } from '@/policies/queries';

/**
 * 08 §3.6: version list, YAML editor, diff against any version, impact preview (lifecycle/routing) that must load before
 * Activate enables, rollback (= activating an earlier version, ADR-6). `id === 'new'` creates the first version.
 */
export function PolicyEditorPage({ kind, id }: { kind: PolicyKind; id: string }) {
  const { t } = useTranslation();
  const me = useMe();
  const navigate = useNavigate();
  const canManage = can(me.data, P.policyManage);
  const isNew = id === 'new';
  const versions = useQuery({ ...policyVersionsQuery(kind, id), enabled: !isNew });
  const sorted = useMemo(() => [...(versions.data ?? [])].sort((a, b) => b.version - a.version), [versions.data]);
  const active = sorted.find((v) => v.active);
  const [selected, setSelected] = useState<number | null>(null);
  const current = sorted.find((v) => v.version === (selected ?? active?.version ?? sorted[0]?.version));

  const [draft, setDraft] = useState<string | null>(null);
  const [name, setName] = useState('');
  const effective = draft ?? (isNew ? POLICY_SKELETONS[kind] : current?.yaml ?? '');
  const dirty = draft !== null && (isNew || draft !== current?.yaml);
  const [compareWith, setCompareWith] = useState<number | ''>('');
  const [problem, setProblem] = useState<Problem | null>(null);
  const [confirm, setConfirm] = useState<'activate' | 'rollback' | null>(null);

  const create = useCreatePolicyVersion();
  const activate = useActivatePolicy();
  const rollback = useRollbackPolicy();

  // Impact preview only for kinds that have one, only for a stored inactive version (08 §3.6: shown before Activate enables).
  const wantsImpact = !isNew && !!current && !current.active && IMPACT_KINDS.includes(kind);
  const impact = useQuery({ ...policyImpactQuery(kind, id, current?.version ?? 0), enabled: wantsImpact });
  const activateReady = !wantsImpact || impact.isSuccess;
  const isRollback = !!current && !!active && current.version < active.version;

  const save = () => {
    setProblem(null);
    create.mutate(
      { kind, yaml: effective, policyId: isNew ? null : id, name: isNew ? name.trim() || null : current?.name ?? null },
      {
        onSuccess: (v) => {
          setDraft(null);
          setSelected(v.version);
          setCompareWith('');
          if (isNew) void navigate({ to: '/policies/$kind/$id', params: { kind, id: v.id } });
        },
        onError: (e) => setProblem(e as unknown as Problem),
      },
    );
  };

  if (!isNew && versions.isPending) return <p className="p-4 text-ink-2">{t('detail.loading')}</p>;
  if (!isNew && versions.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={versions.error as unknown as Problem} />
      </div>
    );
  }
  const compare = compareWith === '' ? undefined : sorted.find((v) => v.version === compareWith);
  const inlineErrors = (problem?.errors ?? []).filter((e) => e.path.startsWith('yaml') || e.path.startsWith('$') || e.path === '');
  const kindLabel = t(`policies.kinds.${kind}`);

  return (
    <div className="flex h-full min-h-0 flex-col bg-surface">
      <header className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
        <Link to="/policies/$kind" params={{ kind }} className="text-xs text-ink-2">
          ← {t('policies.title')} · {kindLabel}
        </Link>
        <h1 className="m-0 text-md" data-testid="policy-title">
          {isNew ? t('policies.newOf', { kind: kindLabel }) : current?.name ?? (SINGLE_DOCUMENT_KINDS.includes(kind) ? kindLabel : t('policies.unnamed'))}
        </h1>
        {current && !isNew && (
          <span className={`badge ${current.active ? '' : 'border-dashed'}`} data-testid="version-state">
            v{current.version} · {current.active ? t('policies.active') : t('policies.inactive')}
          </span>
        )}
        <span className="ml-auto flex flex-wrap gap-1">
          {!isNew && current && !current.active && canManage && (
            <button type="button" className="btn" onClick={() => setConfirm(isRollback ? 'rollback' : 'activate')} disabled={!activateReady || activate.isPending || rollback.isPending} data-testid="policy-activate" title={activateReady ? undefined : t('policies.impactFirst')}>
              {isRollback ? t('policies.rollbackTo', { version: current.version }) : t('policies.activate')}
            </button>
          )}
          {canManage && (
            <button type="button" className="btn btn-primary" onClick={save} disabled={create.isPending || (!isNew && !dirty)} data-testid="policy-save">
              {isNew ? t('policies.create') : t('policies.saveVersion')}
            </button>
          )}
        </span>
      </header>

      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,3fr)_minmax(0,2fr)] max-lg:grid-cols-1 max-lg:overflow-y-auto">
        <section className="flex min-h-0 flex-col gap-3 overflow-y-auto border-r border-line p-4 text-sm max-lg:border-r-0">
          {!isNew && sorted.length > 0 && (
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="text-ink-2">{t('policies.versions')}</span>
              <div role="tablist" aria-label={t('policies.versions')} className="flex flex-wrap gap-1">
                {sorted.map((v) => (
                  <button
                    key={v.version}
                    type="button"
                    role="tab"
                    aria-selected={v.version === current?.version}
                    className={`btn btn-sm ${v.version === current?.version ? 'bg-surface-2 font-semibold' : ''}`}
                    onClick={() => {
                      setSelected(v.version);
                      setDraft(null);
                      setCompareWith('');
                      setProblem(null);
                    }}
                    title={`${utc(v.createdAt)} · ${v.createdBy ?? ''}`}
                    data-testid={`policy-version-${v.version}`}
                  >
                    v{v.version}
                    {v.active ? ' ●' : ''}
                  </button>
                ))}
              </div>
              {sorted.length > 1 && (
                <label className="ml-auto flex items-center gap-1 text-ink-2">
                  {t('policies.compareWith')}
                  <select className="input h-7 w-auto text-sm" value={compareWith} onChange={(e) => setCompareWith(e.target.value === '' ? '' : Number(e.target.value))} data-testid="policy-compare">
                    <option value="">—</option>
                    {sorted
                      .filter((v) => v.version !== current?.version)
                      .map((v) => (
                        <option key={v.version} value={v.version}>
                          v{v.version}
                        </option>
                      ))}
                  </select>
                </label>
              )}
            </div>
          )}
          {isNew && !SINGLE_DOCUMENT_KINDS.includes(kind) && (
            <label className="block">
              <span className="text-ink-2">{t('policies.form.name')}</span>
              <input className="input mt-1 max-w-md" value={name} onChange={(e) => setName(e.target.value)} autoFocus data-testid="policy-name" />
            </label>
          )}

          {compare ? (
            <div className="rounded-md border border-line" aria-label={t('policies.diffLabel', { from: compare.version, to: current?.version ?? 0 })}>
              <div className="flex h-7 items-center border-b border-line px-2 text-xs text-ink-2">{t('policies.diffLabel', { from: compare.version, to: current?.version ?? 0 })}</div>
              <pre tabIndex={0} className="mono m-0 max-h-[480px] overflow-auto p-2 text-xs leading-snug" data-testid="policy-diff">
                {diffLines(compare.yaml ?? '', effective).map((l, i) => (
                  <div key={i} className={l.op === 'add' ? 'bg-[color-mix(in_oklab,var(--sev-low)_18%,transparent)]' : l.op === 'del' ? 'bg-[color-mix(in_oklab,var(--sev-critical)_18%,transparent)] line-through' : ''}>
                    <span aria-hidden="true" className="inline-block w-4 text-ink-2">
                      {l.op === 'add' ? '+' : l.op === 'del' ? '−' : ' '}
                    </span>
                    {l.text}
                  </div>
                ))}
              </pre>
            </div>
          ) : (
            <div>
              <div className="flex h-7 items-center justify-between text-xs text-ink-2">
                <span>{t('policies.form.document')}</span>
                <span>{canManage ? (dirty ? t('policies.unsaved') : 'YAML') : t('policies.readOnly')}</span>
              </div>
              <YamlEditor value={effective} onChange={canManage ? setDraft : undefined} readOnly={!canManage} height="440px" label={t('policies.form.document')} testId="policy-editor" />
            </div>
          )}
          {inlineErrors.length > 0 && (
            <ul className="m-0 list-none p-0 text-xs" role="alert" style={{ color: 'var(--sev-critical)' }} data-testid="policy-errors">
              {inlineErrors.map((e) => (
                <li key={`${e.path}:${e.message}`} className="mono">
                  {e.path ? `${e.path}: ` : ''}
                  {e.message}
                </li>
              ))}
            </ul>
          )}
          {problem && inlineErrors.length === 0 && (
            <div aria-live="polite">
              <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
            </div>
          )}
        </section>

        <aside className="flex min-h-0 flex-col gap-3 overflow-y-auto p-4 text-sm">
          <details className="rounded-md border border-line" open>
            <summary className="cursor-pointer px-2 py-1 text-xs text-ink-2">{t('policies.hints.title')}</summary>
            <p className="m-0 p-2 text-xs text-ink-2">{t(`policies.hints.${kind}`)}</p>
          </details>
          {current && !isNew && (
            <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
              <dt className="text-ink-2">{t('policies.columns.id')}</dt>
              <dd className="mono m-0">{current.id}</dd>
              <dt className="text-ink-2">{t('policies.columns.createdBy')}</dt>
              <dd className="m-0">
                {current.createdBy ?? '—'} · <RelativeTime iso={current.createdAt} mode="sentence" />
              </dd>
              <dt className="text-ink-2">{t('policies.columns.activated')}</dt>
              <dd className="m-0">
                <RelativeTime iso={current.activatedAt} mode="sentence" />
                {current.deactivatedAt && (
                  <>
                    {' · '}
                    {t('policies.deactivated')} <RelativeTime iso={current.deactivatedAt} mode="sentence" />
                  </>
                )}
              </dd>
            </dl>
          )}
          {wantsImpact && (
            <section className="rounded-md border border-line" data-testid="policy-impact">
              <h2 className="m-0 flex h-7 items-center border-b border-line px-2 text-xs text-ink-2">{t('policies.impact.title', { version: current?.version ?? 0 })}</h2>
              <div className="p-2 text-xs">
                {impact.isPending && <p className="m-0 text-ink-2">{t('policies.impact.loading')}</p>}
                {impact.isError && <ProblemBanner problem={impact.error as unknown as Problem} />}
                {impact.data && (
                  <>
                    <p className="m-0">
                      <span className="text-xl font-semibold" data-testid="impact-count">
                        {impact.data.affectedOpenEpisodes}
                      </span>{' '}
                      {t('policies.impact.affected', { count: impact.data.affectedOpenEpisodes })}
                    </p>
                    <p className="m-0 mt-1 text-ink-2">{impact.data.explanation}</p>
                    {impact.data.sample.length > 0 && (
                      <ul className="m-0 mt-2 list-none p-0">
                        {impact.data.sample.map((s) => (
                          <li key={s.episodeId} className="flex flex-wrap items-center gap-2 border-t border-line py-1">
                            <SeverityBadge severity={s.severity} compact />
                            <Link to="/episodes/$id" params={{ id: s.episodeId }} className="min-w-0 flex-1 truncate text-ink hover:underline">
                              {s.summary ?? s.episodeId}
                            </Link>
                            {s.proposedAutoResolveAt && (
                              <span className="text-ink-2" title={utc(s.proposedAutoResolveAt)}>
                                {t('policies.impact.proposed')} <RelativeTime iso={s.proposedAutoResolveAt} />
                              </span>
                            )}
                            {s.note && <span className="text-ink-2">{s.note}</span>}
                          </li>
                        ))}
                      </ul>
                    )}
                  </>
                )}
              </div>
            </section>
          )}
        </aside>
      </div>

      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={confirm === 'rollback' ? t('policies.rollbackTitle', { version: current?.version ?? 0 }) : t('policies.activateTitle', { version: current?.version ?? 0 })}
        description={t(`policies.activateWhy.${kind}`)}
        verb={confirm === 'rollback' ? t('policies.rollback') : t('policies.activate')}
        busy={activate.isPending || rollback.isPending}
        onConfirm={() => {
          if (!current) return;
          const done = { onSuccess: () => setConfirm(null), onError: (e: unknown) => setProblem(e as Problem) };
          if (confirm === 'rollback') rollback.mutate({ kind, id, toVersion: current.version }, done);
          else activate.mutate({ kind, id, version: current.version }, done);
        }}
      />
    </div>
  );
}
