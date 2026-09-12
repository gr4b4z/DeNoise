import * as Tabs from '@radix-ui/react-tabs';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { OneTimeSecretDialog } from '@/components/OneTimeSecretDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { YamlEditor } from '@/components/YamlEditor';
import { teamsQuery } from '@/episodes/queries';
import {
  failuresQuery,
  integrationDivergenceQuery,
  integrationHealthQuery,
  integrationQuery,
  mappingVersionsQuery,
  replayQuery,
  safeJson,
  useActivateMapping,
  useCreateMappingVersion,
  useDismissFailure,
  usePreviewMapping,
  useRotateIngestToken,
  useRunDivergence,
  useStartReplay,
  useUpdateIntegration,
  type IntegrationSummary,
  type MappingFailure,
  type MappingPreviewItem,
  type MappingVersion,
} from '@/integrations/queries';
import { diffLines } from '@/lib/diff';
import { utc } from '@/lib/time';
import { IntegrationHealthBadge, typeLabel } from '@/routes/Integrations';
import { PreviewCard } from '@/routes/IntegrationWizard';

function Tab({ value, children }: { value: string; children: string }) {
  return (
    <Tabs.Trigger value={value} className="h-9 border-b-2 border-transparent text-sm text-ink-2 data-[state=active]:border-accent data-[state=active]:text-ink">
      {children}
    </Tabs.Trigger>
  );
}

/** `/integrations/$id`: health, mapping editor with preview/diff/activate, failures with preview/retry/dismiss, settings (08 §3.5). */
export function IntegrationDetailPage({ id }: { id: string }) {
  const { t } = useTranslation();
  const me = useMe();
  const detail = useQuery(integrationQuery(id));
  if (detail.isPending) return <p className="p-4 text-ink-2">{t('detail.loading')}</p>;
  if (detail.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={detail.error as unknown as Problem} />
      </div>
    );
  }
  const i = detail.data;
  const canManage = can(me.data, P.integrationManage, i.accessScope);
  const canMap = can(me.data, P.mappingManage, i.accessScope);
  return (
    <div className="flex h-full min-h-0 flex-col bg-surface">
      <header className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
        <Link to="/integrations" className="text-xs text-ink-2">
          ← {t('integrations.title')}
        </Link>
        <h1 className="m-0 text-md" data-testid="integration-title">
          {i.name}
        </h1>
        <span className="badge">{typeLabel(i.type)}</span>
        <IntegrationHealthBadge id={i.id} />
        {!i.active && <span className="badge border-dashed">{t('integrations.inactive')}</span>}
        {i.shadow && <span className="badge border-dashed">{t('integrations.shadow')}</span>}
        <span className="mono ml-auto text-xs text-ink-2">
          {t('integrations.keyId')} {i.ingestKeyId} · v{i.version}
        </span>
      </header>
      <Tabs.Root defaultValue="health" className="flex min-h-0 flex-1 flex-col">
        <Tabs.List className="flex gap-4 border-b border-line px-4" aria-label={t('integrations.sections')}>
          <Tab value="health">{t('integrations.tabs.health')}</Tab>
          <Tab value="mappings">{t('integrations.tabs.mappings')}</Tab>
          <Tab value="failures">{t('integrations.tabs.failures')}</Tab>
          <Tab value="settings">{t('integrations.tabs.settings')}</Tab>
        </Tabs.List>
        <Tabs.Content value="health" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          <Health id={i.id} canManage={canManage} />
        </Tabs.Content>
        <Tabs.Content value="mappings" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          <Mappings integration={i} canMap={canMap} />
        </Tabs.Content>
        <Tabs.Content value="failures" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          <Failures integration={i} canMap={canMap} canReplay={can(me.data, P.replayRetry, i.accessScope)} />
        </Tabs.Content>
        <Tabs.Content value="settings" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          <Settings key={i.version} integration={i} readOnly={!canManage} />
        </Tabs.Content>
      </Tabs.Root>
    </div>
  );
}

function Health({ id, canManage }: { id: string; canManage: boolean }) {
  const { t } = useTranslation();
  const health = useQuery(integrationHealthQuery(id));
  const h = health.data;
  if (!h) return <p className="m-0 text-ink-2">{t('detail.loading')}</p>;
  const rows: [string, React.ReactNode][] = [
    [t('integrations.health.coverage'), `${h.coverageState}${h.coverageConfigured ? '' : ` (${t('integrations.health.noMethod')})`}`],
    [t('integrations.health.coverageSince'), <RelativeTime key="cs" iso={h.coverageSince} mode="sentence" />],
    [t('integrations.health.lastSignal'), <RelativeTime key="ls" iso={h.lastSignalAt} mode="sentence" />],
    [t('integrations.health.lastProcessed'), <RelativeTime key="lp" iso={h.lastProcessedAlertAt} mode="sentence" />],
    [t('integrations.health.accepted'), h.acceptedLast15m],
    [t('integrations.health.failures'), h.mappingFailuresLast15m],
    [t('integrations.health.backlog'), h.pendingNormalise > 0 ? <span key="b">{h.pendingNormalise} · <RelativeTime iso={h.oldestPendingSince} mode="sentence" /></span> : '0'],
    [t('integrations.health.suspended'), h.suspendedAutoResolve],
    [t('integrations.health.open'), h.openEpisodes],
  ];
  return (
    <div className="grid gap-4">
      <dl className="m-0 grid max-w-xl grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm" data-testid="integration-health-panel">
        {rows.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-ink-2">{k}</dt>
            <dd className="m-0">{v}</dd>
          </div>
        ))}
        {h.coverageEpisodeId && (
          <>
            <dt className="text-ink-2">{t('integrations.health.coverageEpisode')}</dt>
            <dd className="m-0">
              <Link to="/episodes/$id" params={{ id: h.coverageEpisodeId }}>
                {t('heartbeats.openEpisode')}
              </Link>
            </dd>
          </>
        )}
      </dl>
      <Divergence id={id} canManage={canManage} />
    </div>
  );
}

/** 09 M12 / spec §21: hub state vs source state for the shadow gate; the report is stored and the last one shown. */
function Divergence({ id, canManage }: { id: string; canManage: boolean }) {
  const { t } = useTranslation();
  const last = useQuery(integrationDivergenceQuery(id));
  const run = useRunDivergence(id);
  const [problem, setProblem] = useState<Problem | null>(null);
  const r = last.data;
  return (
    <section className="max-w-3xl rounded-md border border-line p-3 text-sm" data-testid="integration-divergence">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="m-0 text-xs text-ink-2">{t('integrations.divergence.title')}</h3>
        <span className="text-xs text-ink-2">{t('integrations.divergence.about')}</span>
        {canManage && (
          <button type="button" className="btn btn-sm ml-auto" onClick={() => run.mutate(undefined, { onError: (e) => setProblem(e as unknown as Problem) })} disabled={run.isPending} data-testid="divergence-run">
            {run.isPending ? t('integrations.divergence.running') : t('integrations.divergence.run')}
          </button>
        )}
      </div>
      {problem && <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />}
      {last.isPending ? (
        <p className="m-0 mt-2 text-ink-2">{t('detail.loading')}</p>
      ) : !r ? (
        <p className="m-0 mt-2 text-ink-2" data-testid="divergence-none">
          {t('integrations.divergence.never')}
        </p>
      ) : (
        <div className="mt-2 grid gap-2" data-testid="divergence-report" data-supported={r.supported} data-within={r.withinThreshold ?? 'unknown'}>
          <p className="m-0 text-xs text-ink-2">
            <RelativeTime iso={r.at} mode="sentence" /> · {t('integrations.divergence.open', { count: r.openEpisodes })} · {r.durationMs} ms
          </p>
          {!r.supported ? (
            <p className="m-0">
              <span className="badge border-dashed">{t('integrations.divergence.unsupported')}</span> <span className="text-ink-2">{r.detail}</span>
            </p>
          ) : (
            <>
              <div className="flex flex-wrap gap-2">
                <span className="badge">{t('integrations.divergence.agree', { count: r.agree })}</span>
                <span className="badge">{t('integrations.divergence.diverged', { count: r.diverged })}</span>
                <span className="badge border-dashed">{t('integrations.divergence.unknown', { count: r.unknown })}</span>
                <span className="badge" data-testid="divergence-verdict">
                  {r.divergenceShare === null
                    ? t('integrations.divergence.noVerdict')
                    : t(r.withinThreshold ? 'integrations.divergence.within' : 'integrations.divergence.above', { share: (r.divergenceShare * 100).toFixed(1), threshold: (r.threshold * 100).toFixed(0) })}
                </span>
              </div>
              {r.samples.length > 0 && (
                <ul className="m-0 list-none p-0 text-xs">
                  {r.samples.map((s) => (
                    <li key={s.episodeId} className="flex flex-wrap items-center gap-2 border-t border-line py-1">
                      <span className={`badge ${s.outcome === 'diverged' ? '' : 'border-dashed'}`}>{s.outcome}</span>
                      <Link to="/episodes/$id" params={{ id: s.episodeId }} className="min-w-0 flex-1 truncate text-ink hover:underline">
                        {s.summary ?? s.episodeId}
                      </Link>
                      <span className="text-ink-2">{s.detail}</span>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
        </div>
      )}
    </section>
  );
}

function Mappings({ integration, canMap }: { integration: IntegrationSummary; canMap: boolean }) {
  const { t } = useTranslation();
  const versions = useQuery(mappingVersionsQuery(integration.id));
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [draft, setDraft] = useState<string | null>(null);
  const [sampleDraft, setSample] = useState<string | null>(null);
  const [items, setItems] = useState<MappingPreviewItem[] | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [compare, setCompare] = useState(false);
  const preview = usePreviewMapping(integration.id);
  const createVersion = useCreateMappingVersion(integration.id);
  const activate = useActivateMapping(integration.id);

  const all = versions.data ?? [];
  const selected = all.find((v) => `${v.mappingId}:${v.version}` === selectedKey) ?? all.find((v) => v.active) ?? all[0];
  const yaml = draft ?? selected?.yaml ?? '';
  const dirty = draft !== null && draft !== selected?.yaml;
  const activeOfSame = selected ? all.find((v) => v.mappingId === selected.mappingId && v.active) : undefined;

  const sample = sampleDraft ?? defaultSample(selected);

  const runPreview = () => {
    setProblem(null);
    const body = safeJson(sample);
    if (!body) {
      setProblem({ title: t('integrations.wizard.sampleNotJson'), status: 400 });
      return;
    }
    const req = dirty || !selected ? { body: { yaml, body, includeRouting: true } } : { mappingId: selected.mappingId, version: selected.version, body: { body, includeRouting: true } };
    preview.mutate(req, { onSuccess: (r) => setItems(r.items), onError: (e) => setProblem(e as unknown as Problem) });
  };

  const save = () => {
    if (!selected) return;
    setProblem(null);
    createVersion.mutate(
      { yaml, name: selected.name, order: selected.order, mappingId: selected.mappingId, samples: selected.samples ?? null },
      {
        onSuccess: (v) => {
          setDraft(null);
          setSelectedKey(`${v.mappingId}:${v.version}`);
        },
        onError: (e) => setProblem(e as unknown as Problem),
      },
    );
  };

  if (!versions.data) return <p className="m-0 text-ink-2">{t('detail.loading')}</p>;
  if (all.length === 0) return <p className="m-0 text-ink-2">{t('integrations.mappings.none')}</p>;
  return (
    <div className="grid gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
      <div className="grid content-start gap-2">
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <label className="flex items-center gap-2 text-ink-2">
            {t('integrations.mappings.version')}
            <select
              className="input h-7 w-auto text-sm"
              value={selected ? `${selected.mappingId}:${selected.version}` : ''}
              onChange={(e) => {
                setSelectedKey(e.target.value);
                setDraft(null);
                setItems(null);
              }}
              data-testid="mapping-version-picker"
            >
              {all.map((v) => (
                <option key={`${v.mappingId}:${v.version}`} value={`${v.mappingId}:${v.version}`}>
                  {v.order} · {v.name ?? v.mappingId.slice(0, 8)} v{v.version}
                  {v.active ? ` · ${t('templates.active')}` : ''}
                </option>
              ))}
            </select>
          </label>
          {selected && !selected.active && activeOfSame && (
            <label className="flex items-center gap-1 text-ink-2">
              <input type="checkbox" checked={compare} onChange={(e) => setCompare(e.target.checked)} />
              {t('integrations.mappings.diffAgainstActive', { version: activeOfSame.version })}
            </label>
          )}
          <span className="ml-auto flex gap-1">
            {canMap && selected && (
              <button type="button" className="btn btn-sm" onClick={save} disabled={!dirty || createVersion.isPending} data-testid="mapping-save">
                {t('templates.saveVersion')}
              </button>
            )}
            {canMap && selected && !selected.active && (
              <button type="button" className="btn btn-sm btn-primary" onClick={() => activate.mutate({ mappingId: selected.mappingId, version: selected.version }, { onError: (e) => setProblem(e as unknown as Problem) })} disabled={dirty || activate.isPending || items?.some((x) => !x.ok) === true} data-testid="mapping-activate">
                {t('templates.activate')}
              </button>
            )}
          </span>
        </div>
        {compare && activeOfSame ? (
          <pre tabIndex={0} className="mono m-0 max-h-[480px] overflow-auto rounded-md border border-line p-2 text-xs leading-snug">
            {diffLines(activeOfSame.yaml ?? '', yaml).map((l, n) => (
              <div key={n} className={l.op === 'add' ? 'bg-[color-mix(in_oklab,var(--sev-low)_18%,transparent)]' : l.op === 'del' ? 'bg-[color-mix(in_oklab,var(--sev-critical)_18%,transparent)] line-through' : ''}>
                <span aria-hidden="true" className="inline-block w-4 text-ink-2">
                  {l.op === 'add' ? '+' : l.op === 'del' ? '−' : ' '}
                </span>
                {l.text}
              </div>
            ))}
          </pre>
        ) : (
          <YamlEditor value={yaml} onChange={canMap ? setDraft : undefined} readOnly={!canMap} height="480px" label={t('integrations.form.mappingYaml')} testId="mapping-editor" />
        )}
        {selected && (
          <p className="m-0 text-xs text-ink-2">
            {t('integrations.mappings.meta', { by: selected.createdBy ?? '—', at: utc(selected.createdAt), identity: selected.identityVersion })}
          </p>
        )}
        {problem && (
          <div aria-live="polite">
            <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
          </div>
        )}
      </div>
      <div className="grid content-start gap-2">
        <label className="block">
          <span className="text-ink-2">{t('integrations.form.sample')}</span>
          <textarea className="input mono mt-1 h-56 py-1 text-xs" value={sample} onChange={(e) => setSample(e.target.value)} spellCheck={false} data-testid="mapping-sample" />
        </label>
        <button type="button" className="btn justify-self-start" onClick={runPreview} disabled={preview.isPending} data-testid="mapping-preview">
          {dirty ? t('integrations.previewDraft') : t('integrations.preview')}
        </button>
        {items?.map((item) => <PreviewCard key={item.source} item={item} />)}
      </div>
    </div>
  );
}

function Failures({ integration, canMap, canReplay }: { integration: IntegrationSummary; canMap: boolean; canReplay: boolean }) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  const failures = useQuery(failuresQuery(integration.id, all));
  const versions = useQuery(mappingVersionsQuery(integration.id));
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [previewVersion, setPreviewVersion] = useState('');
  const [items, setItems] = useState<MappingPreviewItem[] | null>(null);
  const [jobId, setJobId] = useState<string | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const preview = usePreviewMapping(integration.id);
  const dismiss = useDismissFailure(integration.id);
  const replay = useStartReplay();
  const status = useQuery(replayQuery(jobId ?? '', jobId !== null));

  const rows = failures.data ?? [];
  const toggle = (eventId: string) => setSelected((s) => {
    const next = new Set(s);
    if (next.has(eventId)) next.delete(eventId);
    else next.add(eventId);
    return next;
  });
  const chosen = [...selected];
  const version = (versions.data ?? []).find((v) => `${v.mappingId}:${v.version}` === previewVersion);

  const runPreview = () => {
    setProblem(null);
    const req = version ? { mappingId: version.mappingId, version: version.version, body: { rawEventIds: chosen, includeRouting: true } } : null;
    if (!req) return;
    preview.mutate(req, { onSuccess: (r) => setItems(r.items), onError: (e) => setProblem(e as unknown as Problem) });
  };

  const retry = () => {
    setProblem(null);
    replay.mutate({ mode: 'retry_failed', integrationId: integration.id, eventIds: chosen.length > 0 ? chosen : null, mappingVersion: version?.version ?? null, limit: 5000 }, {
      onSuccess: (r) => {
        setJobId(r.jobId);
        setSelected(new Set());
      },
      onError: (e) => setProblem(e as unknown as Problem),
    });
  };

  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <label className="flex items-center gap-1 text-ink-2">
          <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} />
          {t('integrations.failures.showAll')}
        </label>
        <label className="flex items-center gap-1 text-ink-2">
          {t('integrations.failures.previewWith')}
          <select className="input h-7 w-auto text-sm" value={previewVersion} onChange={(e) => setPreviewVersion(e.target.value)}>
            <option value="">—</option>
            {(versions.data ?? []).map((v: MappingVersion) => (
              <option key={`${v.mappingId}:${v.version}`} value={`${v.mappingId}:${v.version}`}>
                {v.name ?? v.mappingId.slice(0, 8)} v{v.version}
                {v.active ? ` · ${t('templates.active')}` : ''}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className="btn btn-sm" disabled={chosen.length === 0 || !version || preview.isPending} onClick={runPreview}>
          {t('integrations.preview')} ({chosen.length})
        </button>
        {canReplay && (
          <button type="button" className="btn btn-sm btn-primary" disabled={replay.isPending || rows.length === 0} onClick={retry} data-testid="failures-retry">
            {chosen.length > 0 ? t('integrations.failures.retrySelected', { count: chosen.length }) : t('integrations.failures.retryAll')}
          </button>
        )}
      </div>
      {status.data && (
        <div className="rounded-md border border-line bg-surface-2 p-2 text-xs" aria-live="polite" data-testid="replay-status">
          <span className="badge">{status.data.status}</span> {t('integrations.failures.replay', { mode: status.data.mode })}
          {status.data.result !== null && status.data.result !== undefined && <pre className="mono m-0 mt-1 whitespace-pre-wrap">{JSON.stringify(status.data.result, null, 1)}</pre>}
          {status.data.lastError && <p className="m-0 mt-1" style={{ color: 'var(--sev-critical)' }}>{status.data.lastError}</p>}
        </div>
      )}
      {problem && <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />}
      {items?.map((item) => <PreviewCard key={item.source} item={item} />)}
      {failures.isPending ? (
        <p className="m-0 text-ink-2">{t('detail.loading')}</p>
      ) : rows.length === 0 ? (
        <p className="m-0 text-ink-2" data-testid="failures-empty">
          {t('integrations.failures.none')}
        </p>
      ) : (
        <table className="w-full border-collapse text-sm">
          <thead className="text-left text-xs text-ink-2">
            <tr className="h-8 border-b border-line">
              <th className="w-6" />
              <th className="font-normal">{t('integrations.failures.columns.at')}</th>
              <th className="font-normal">{t('integrations.failures.columns.error')}</th>
              <th className="font-normal">{t('integrations.failures.columns.field')}</th>
              <th className="font-normal">{t('integrations.failures.columns.version')}</th>
              <th className="font-normal">{t('integrations.failures.columns.raw')}</th>
              <th className="font-normal" />
            </tr>
          </thead>
          <tbody>
            {rows.map((f: MappingFailure) => (
              <tr key={f.id} className="border-b border-line align-top" data-failure-id={f.id} data-quarantined={f.quarantined}>
                <td className="py-1">
                  <input type="checkbox" aria-label={t('integrations.failures.select')} checked={selected.has(f.eventId)} onChange={() => toggle(f.eventId)} disabled={!f.quarantined} />
                </td>
                <td className="mono py-1 text-xs whitespace-nowrap">{utc(f.receivedAt)}</td>
                <td className="py-1 text-xs" style={{ color: 'var(--sev-critical)' }}>
                  {f.error}
                  {!f.quarantined && <span className="badge ml-2 border-dashed">{t('integrations.failures.resolved')}</span>}
                </td>
                <td className="mono py-1 text-xs">{f.field ?? '—'}</td>
                <td className="py-1 text-xs text-ink-2">{f.mappingVersion ?? '—'}</td>
                <td className="py-1 text-xs">{f.rawExcerpt ? <pre tabIndex={0} className="mono m-0 max-h-24 max-w-md overflow-auto whitespace-pre-wrap">{f.rawExcerpt}</pre> : <span className="text-ink-2">{t('integrations.failures.noRaw')}</span>}</td>
                <td className="py-1 text-right">
                  {canMap && f.quarantined && (
                    <button type="button" className="btn btn-sm" onClick={() => dismiss.mutate(f.id, { onError: (e) => setProblem(e as unknown as Problem) })}>
                      {t('integrations.failures.dismiss')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function Settings({ integration, readOnly }: { integration: IntegrationSummary; readOnly: boolean }) {
  const { t } = useTranslation();
  const teams = useQuery(teamsQuery());
  const update = useUpdateIntegration(integration.id);
  const rotate = useRotateIngestToken(integration.id);
  const [name, setName] = useState(integration.name);
  const [owner, setOwner] = useState(integration.ownerTeamId ?? '');
  const [scope, setScope] = useState(integration.accessScope);
  const [active, setActive] = useState(integration.active);
  const [shadow, setShadow] = useState(integration.shadow);
  const [coverage, setCoverage] = useState(pretty(integration.coverage));
  const [profileDefaults, setProfileDefaults] = useState(pretty(integration.profileDefaults));
  const [hmacSecret, setHmacSecret] = useState('');
  const [hmacRequired, setHmacRequired] = useState(integration.hmac?.required ?? false);
  const [hmacAlgorithm, setHmacAlgorithm] = useState(integration.hmac?.algorithm ?? 'sha1');
  const [clearHmac, setClearHmac] = useState(false);
  const [atlas, setAtlas] = useState({ groupId: integration.atlasGroupId ?? '', publicKey: '', privateKey: '' });
  const [problem, setProblem] = useState<Problem | null>(null);
  const [secret, setSecret] = useState<{ token: string; path: string } | null>(null);
  const isAtlas = integration.type === 'atlas';

  const save = () => {
    setProblem(null);
    if (!safeJson(coverage) || !safeJson(profileDefaults)) {
      setProblem({ title: t('integrations.settings.notJson'), status: 400 });
      return;
    }
    const hmacChanged = isAtlas && (hmacSecret.length > 0 || hmacRequired !== (integration.hmac?.required ?? false) || hmacAlgorithm !== (integration.hmac?.algorithm ?? 'sha1'));
    update.mutate(
      {
        version: integration.version,
        body: {
          name: name !== integration.name ? name : null,
          accessScope: scope !== integration.accessScope ? scope : null,
          ownerTeamId: owner && owner !== integration.ownerTeamId ? owner : null,
          clearOwnerTeam: !owner && !!integration.ownerTeamId,
          coverage: coverage.trim() !== pretty(integration.coverage) ? JSON.stringify(safeJson(coverage)) : null,
          profileDefaults: profileDefaults.trim() !== pretty(integration.profileDefaults) ? JSON.stringify(safeJson(profileDefaults)) : null,
          hmacSecret: isAtlas && hmacSecret ? hmacSecret : null,
          hmac: hmacChanged ? { algorithm: hmacAlgorithm, header: integration.hmac?.header ?? 'X-MMS-Signature', encoding: integration.hmac?.encoding ?? 'base64', required: hmacRequired } : null,
          clearHmac: isAtlas && clearHmac,
          atlasApi: isAtlas && atlas.groupId && atlas.publicKey && atlas.privateKey ? { groupId: atlas.groupId, publicKey: atlas.publicKey, privateKey: atlas.privateKey } : null,
          active: active !== integration.active ? active : null,
          shadow: shadow !== integration.shadow ? shadow : null,
        },
      },
      { onError: (e) => setProblem(e as unknown as Problem) },
    );
  };

  return (
    <form
      className="grid max-w-3xl gap-3 text-sm"
      onSubmit={(e) => {
        e.preventDefault();
        save();
      }}
    >
      <fieldset className="contents" disabled={readOnly}>
        <div className="grid gap-3 sm:grid-cols-3">
          <label className="block">
            <span className="text-ink-2">{t('integrations.form.name')}</span>
            <input className="input mt-1" value={name} onChange={(e) => setName(e.target.value)} />
          </label>
          <label className="block">
            <span className="text-ink-2">{t('integrations.form.owner')}</span>
            <select className="input mt-1" value={owner} onChange={(e) => setOwner(e.target.value)}>
              <option value="">{t('integrations.form.noOwner')}</option>
              {teams.data?.map((team) => (
                <option key={team.id} value={team.id}>
                  {team.name}
                </option>
              ))}
            </select>
          </label>
          <label className="block">
            <span className="text-ink-2">{t('integrations.form.scope')}</span>
            <input className="input mono mt-1" value={scope} onChange={(e) => setScope(e.target.value)} />
          </label>
        </div>
        <div className="flex flex-wrap gap-4">
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
            {t('integrations.settings.active')}
          </label>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={shadow} onChange={(e) => setShadow(e.target.checked)} />
            {t('integrations.settings.shadow')}
          </label>
        </div>
        {isAtlas && (
          <fieldset className="rounded-md border border-line p-3">
            <legend className="px-1 text-ink-2">{t('integrations.form.hmac')}</legend>
            <p className="m-0 text-xs text-ink-2">{integration.hmacConfigured ? t('integrations.settings.hmacSet') : t('integrations.settings.hmacUnset')}</p>
            <div className="mt-2 grid gap-2 sm:grid-cols-3">
              <label className="block">
                <span className="text-ink-2">{t('integrations.settings.newHmacSecret')}</span>
                <input className="input mono mt-1" type="password" autoComplete="off" value={hmacSecret} onChange={(e) => setHmacSecret(e.target.value)} disabled={clearHmac} />
              </label>
              <label className="block">
                <span className="text-ink-2">{t('integrations.form.hmacAlgorithm')}</span>
                <select className="input mt-1" value={hmacAlgorithm} onChange={(e) => setHmacAlgorithm(e.target.value)} disabled={clearHmac}>
                  <option value="sha1">sha1</option>
                  <option value="sha256">sha256</option>
                </select>
              </label>
              <div className="grid content-end gap-1 text-xs">
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={hmacRequired} onChange={(e) => setHmacRequired(e.target.checked)} disabled={clearHmac} />
                  {t('integrations.form.hmacRequired')}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={clearHmac} onChange={(e) => setClearHmac(e.target.checked)} />
                  {t('integrations.settings.clearHmac')}
                </label>
              </div>
            </div>
          </fieldset>
        )}
        {isAtlas && (
          <fieldset className="rounded-md border border-line p-3">
            <legend className="px-1 text-ink-2">{t('integrations.form.atlasApi')}</legend>
            <p className="m-0 text-xs text-ink-2">{integration.atlasCredentials ? t('integrations.settings.atlasSet', { group: integration.atlasGroupId ?? '' }) : t('integrations.settings.atlasUnset')}</p>
            <div className="mt-2 grid gap-2 sm:grid-cols-3">
              <label className="block">
                <span className="text-ink-2">{t('integrations.form.atlasGroup')}</span>
                <input className="input mono mt-1" value={atlas.groupId} onChange={(e) => setAtlas({ ...atlas, groupId: e.target.value })} />
              </label>
              <label className="block">
                <span className="text-ink-2">{t('integrations.form.atlasPublic')}</span>
                <input className="input mono mt-1" value={atlas.publicKey} onChange={(e) => setAtlas({ ...atlas, publicKey: e.target.value })} autoComplete="off" />
              </label>
              <label className="block">
                <span className="text-ink-2">{t('integrations.form.atlasPrivate')}</span>
                <input className="input mono mt-1" type="password" value={atlas.privateKey} onChange={(e) => setAtlas({ ...atlas, privateKey: e.target.value })} autoComplete="off" />
              </label>
            </div>
          </fieldset>
        )}
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block">
            <span className="text-ink-2">{t('integrations.settings.coverage')}</span>
            <textarea className="input mono mt-1 h-40 py-1 text-xs" value={coverage} onChange={(e) => setCoverage(e.target.value)} spellCheck={false} />
          </label>
          <label className="block">
            <span className="text-ink-2">{t('integrations.settings.profileDefaults')}</span>
            <textarea className="input mono mt-1 h-40 py-1 text-xs" value={profileDefaults} onChange={(e) => setProfileDefaults(e.target.value)} spellCheck={false} />
          </label>
        </div>
        {problem && (
          <div aria-live="polite">
            <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
          </div>
        )}
        {!readOnly && (
          <div className="flex flex-wrap gap-2">
            <button type="submit" className="btn btn-primary" disabled={update.isPending}>
              {t('destinations.save')}
            </button>
            <button type="button" className="btn" disabled={rotate.isPending} onClick={() => rotate.mutate(undefined, { onSuccess: (r) => setSecret({ token: r.ingestToken, path: r.ingestPath }), onError: (e) => setProblem(e as unknown as Problem) })}>
              {t('integrations.settings.rotate')}
            </button>
            {update.isSuccess && <span className="self-center text-xs text-ink-2">{t('integrations.settings.saved', { version: update.data.version })}</span>}
          </div>
        )}
      </fieldset>
      <OneTimeSecretDialog open={secret !== null} title={t('integrations.tokenTitle')} secret={secret?.token ?? ''} secretLabel={t('integrations.token')} snippets={secret ? [{ label: 'curl', code: `curl -fsS -X POST -H 'Authorization: Bearer ${secret.token}' -H 'Content-Type: application/json' --data @payload.json <ingest-host>${secret.path}` }] : undefined} onClose={() => setSecret(null)} />
    </form>
  );
}

function defaultSample(selected: MappingVersion | undefined): string {
  const samples = selected?.samples;
  if (Array.isArray(samples) && samples.length > 0) {
    const first = samples[0] as { body?: unknown };
    return JSON.stringify(first.body ?? {}, null, 2);
  }
  return '';
}

function pretty(json: string): string {
  const parsed = safeJson(json);
  return parsed ? JSON.stringify(parsed, null, 2) : json;
}
