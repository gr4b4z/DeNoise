import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { OneTimeSecretDialog } from '@/components/OneTimeSecretDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { YamlEditor } from '@/components/YamlEditor';
import { teamsQuery } from '@/episodes/queries';
import { heartbeatsQuery } from '@/heartbeats/queries';
import {
  INTEGRATION_TYPES,
  LIFECYCLE_PROFILES,
  minutesToTimeSpan,
  referenceMappingsQuery,
  safeJson,
  useActivateMapping,
  useCreateIntegration,
  useCreateMappingVersion,
  usePreviewMapping,
  useUpdateIntegration,
  type IntegrationCreatedResponse,
  type IntegrationType,
  type MappingPreviewItem,
  type MappingVersion,
} from '@/integrations/queries';
import { mappingVersionsQuery } from '@/integrations/queries';
import { destinationsQuery } from '@/notifications/queries';

const STEPS = ['source', 'credentials', 'mapping', 'identity', 'lifecycle', 'severity', 'routing', 'inactivity', 'coverage', 'dryRun'] as const;
type Step = (typeof STEPS)[number];

type CoverageKind = 'none' | 'managed_canary' | 'registered_heartbeat' | 'api_probe';

interface WizardState {
  type: IntegrationType;
  name: string;
  ownerTeamId: string;
  accessScope: string;
  hmacSecret: string;
  hmacRequired: boolean;
  hmacAlgorithm: 'sha1' | 'sha256';
  atlasGroupId: string;
  atlasPublicKey: string;
  atlasPrivateKey: string;
  yaml: string | null;
  mappingName: string | null;
  sample: string | null;
  profile: string;
  repeatIntervalMinutes: number;
  coverageKind: CoverageKind;
  canaryIntervalMinutes: number;
  canaryDelayedMinutes: number;
  canaryAlertMinutes: number;
  canaryUnavailableMinutes: number;
  heartbeatId: string;
  probeIntervalMinutes: number;
}

const INITIAL: WizardState = {
  type: 'azure_monitor',
  name: '',
  ownerTeamId: '',
  accessScope: '',
  hmacSecret: '',
  hmacRequired: false,
  hmacAlgorithm: 'sha1',
  atlasGroupId: '',
  atlasPublicKey: '',
  atlasPrivateKey: '',
  yaml: null,
  mappingName: null,
  sample: null,
  profile: 'explicit_recovery',
  repeatIntervalMinutes: 5,
  coverageKind: 'none',
  canaryIntervalMinutes: 5,
  canaryDelayedMinutes: 10,
  canaryAlertMinutes: 15,
  canaryUnavailableMinutes: 60,
  heartbeatId: '',
  probeIntervalMinutes: 5,
};

function coverageJson(s: WizardState): string {
  switch (s.coverageKind) {
    case 'managed_canary':
      return JSON.stringify({
        methods: [{ type: 'managed_canary', expected_interval: minutesToTimeSpan(s.canaryIntervalMinutes), delayed_after: minutesToTimeSpan(s.canaryDelayedMinutes), alert_after: minutesToTimeSpan(s.canaryAlertMinutes), unavailable_after: minutesToTimeSpan(s.canaryUnavailableMinutes), recovery_successes_required: 2 }],
      });
    case 'registered_heartbeat':
      return JSON.stringify({ methods: [{ type: 'registered_heartbeat', heartbeat_id: s.heartbeatId }] });
    case 'api_probe':
      return JSON.stringify({ methods: [{ type: 'api_probe', interval: minutesToTimeSpan(s.probeIntervalMinutes) }] });
    default:
      return '{}';
  }
}

function profileDefaultsJson(s: WizardState): string {
  const lifecycle: Record<string, unknown> = { profile: s.profile };
  if (s.profile === 'repeating_while_active') lifecycle.expected_repeat_interval = minutesToTimeSpan(s.repeatIntervalMinutes);
  return JSON.stringify({ lifecycle });
}

/** Spec §14.5 onboarding flow as ten steps with server-side validation where the server has a say (08 §3.5). */
export function IntegrationWizardPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const teams = useQuery(teamsQuery());
  const destinations = useQuery(destinationsQuery());
  const heartbeats = useQuery(heartbeatsQuery({}));
  const [step, setStep] = useState<Step>('source');
  const [state, setState] = useState<WizardState>(INITIAL);
  const [created, setCreated] = useState<IntegrationCreatedResponse | null>(null);
  const [secretOpen, setSecretOpen] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [preview, setPreview] = useState<MappingPreviewItem | null>(null);
  const reference = useQuery(referenceMappingsQuery(state.type));
  const versions = useQuery({ ...mappingVersionsQuery(created?.integration.id ?? ''), enabled: !!created });
  const create = useCreateIntegration();
  const update = useUpdateIntegration(created?.integration.id ?? '');
  const previewMutation = usePreviewMapping(created?.integration.id ?? '');
  const createVersion = useCreateMappingVersion(created?.integration.id ?? '');
  const activate = useActivateMapping(created?.integration.id ?? '');

  const set = <K extends keyof WizardState>(key: K, value: WizardState[K]) => setState((s) => ({ ...s, [key]: value }));
  const index = STEPS.indexOf(step);
  const teamOptions = teams.data ?? [];
  const profile = LIFECYCLE_PROFILES.find((p) => p.value === state.profile) ?? LIFECYCLE_PROFILES[0];

  // Defaults derived, never copied: the seeded reference mapping (highest order = the main one) and its first sample.
  const seeded: MappingVersion | undefined = versions.data ? [...versions.data].sort((a, b) => b.order - a.order)[0] : undefined;
  const referenceSample = (reference.data?.find((r) => r.name === seeded?.name)?.samples as { body?: unknown }[] | undefined)?.[0]?.body;
  const yaml = state.yaml ?? seeded?.yaml ?? '';
  const mappingName = state.mappingName ?? seeded?.name ?? '';
  const sample = state.sample ?? JSON.stringify(referenceSample ?? { eventType: 'firing', alertId: 'demo-1', eventId: 'e-1', severity: 'high', summary: 'demo', rule: { id: 'r1' }, resource: { id: 'res-1' } }, null, 2);

  const fail = (err: unknown) => setProblem(err as Problem);

  const createCredentials = () => {
    setProblem(null);
    const isAtlas = state.type === 'atlas';
    create.mutate(
      {
        name: state.name.trim(),
        type: state.type,
        accessScope: state.accessScope.trim(),
        ownerTeamId: state.ownerTeamId || null,
        hmacSecret: isAtlas && state.hmacSecret ? state.hmacSecret : null,
        hmac: isAtlas && state.hmacSecret ? { algorithm: state.hmacAlgorithm, header: 'X-MMS-Signature', encoding: 'base64', required: state.hmacRequired } : null,
      },
      {
        onSuccess: (r) => {
          setCreated(r);
          setSecretOpen(true);
          if (isAtlas && state.atlasGroupId && state.atlasPublicKey && state.atlasPrivateKey) {
            update.mutate({ version: r.integration.version, body: { atlasApi: { groupId: state.atlasGroupId, publicKey: state.atlasPublicKey, privateKey: state.atlasPrivateKey }, clearOwnerTeam: false, clearHmac: false } }, { onError: fail });
          }
        },
        onError: fail,
      },
    );
  };

  const runPreview = () => {
    setProblem(null);
    const body = safeJson(sample);
    if (!body) {
      setProblem({ title: t('integrations.wizard.sampleNotJson'), status: 400 });
      return;
    }
    previewMutation.mutate({ body: { yaml, body, includeRouting: true } }, { onSuccess: (r) => setPreview(r.items[0] ?? null), onError: fail });
  };

  const activateAll = async () => {
    if (!created) return;
    setProblem(null);
    try {
      const yamlChanged = seeded?.yaml !== yaml;
      if (yamlChanged) {
        const v = await createVersion.mutateAsync({ yaml, name: mappingName || null, order: seeded?.order ?? 100, mappingId: seeded?.mappingId ?? null, samples: null });
        await activate.mutateAsync({ mappingId: v.mappingId, version: v.version });
      }
      // The Atlas credentials update (step 2) may have produced configuration version 2 already.
      const latest = await update.mutateAsync({ version: update.data?.version ?? created.integration.version, body: { coverage: coverageJson(state), profileDefaults: profileDefaultsJson(state), active: true, clearOwnerTeam: false, clearHmac: false } });
      void navigate({ to: '/integrations/$id', params: { id: latest.id } });
    } catch (err) {
      fail(err);
    }
  };
  const canNext = (): boolean => {
    switch (step) {
      case 'source':
        return state.name.trim().length > 0 && state.accessScope.trim().length > 0;
      case 'credentials':
        return created !== null;
      case 'mapping':
        return preview?.ok === true;
      case 'coverage':
        return state.coverageKind !== 'registered_heartbeat' || state.heartbeatId.length > 0;
      default:
        return true;
    }
  };

  const closeAfter = profile.closeAfterMinutes === null ? null : state.profile === 'repeating_while_active' ? Math.max(15, state.repeatIntervalMinutes * 3 + 5) : profile.closeAfterMinutes;

  return (
    <div className="flex h-full min-h-0 flex-col bg-surface">
      <header className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
        <Link to="/integrations" className="text-xs text-ink-2">
          ← {t('integrations.title')}
        </Link>
        <h1 className="m-0 text-md">{t('integrations.wizard.title')}</h1>
        {created && <span className="badge">{created.integration.name}</span>}
      </header>
      <div className="grid min-h-0 flex-1 grid-cols-[220px_minmax(0,1fr)] max-md:grid-cols-1">
        <ol className="m-0 list-none border-r border-line p-3 text-sm max-md:hidden" aria-label={t('integrations.wizard.steps')}>
          {STEPS.map((s, i) => (
            <li key={s} className={`flex h-8 items-center gap-2 ${s === step ? 'font-semibold text-ink' : 'text-ink-2'}`} aria-current={s === step ? 'step' : undefined}>
              <span className={`badge ${i < index ? 'border-solid' : 'border-dashed'}`} aria-hidden="true">
                {i < index ? '✓' : i + 1}
              </span>
              {t(`integrations.wizard.step.${s}`)}
            </li>
          ))}
        </ol>
        <section className="flex min-h-0 flex-col overflow-y-auto p-4 text-sm" aria-labelledby="wizard-step-title">
          <h2 id="wizard-step-title" className="m-0 text-base">
            {index + 1}. {t(`integrations.wizard.step.${step}`)}
          </h2>
          <p className="m-0 mt-1 text-xs text-ink-2">{t(`integrations.wizard.hint.${step}`)}</p>
          <div className="mt-4 grid max-w-4xl gap-3">
            {step === 'source' && (
              <>
                <label className="block">
                  <span className="text-ink-2">{t('integrations.form.type')}</span>
                  <select
                    className="input mt-1"
                    value={state.type}
                    onChange={(e) => {
                      const type = e.target.value as IntegrationType;
                      const preset = INTEGRATION_TYPES.find((x) => x.value === type)?.profile ?? 'unknown';
                      setState((s) => ({ ...s, type, profile: preset === 'unknown' ? 'explicit_recovery' : preset }));
                    }}
                    data-testid="wizard-type"
                  >
                    {INTEGRATION_TYPES.map((x) => (
                      <option key={x.value} value={x.value}>
                        {x.label}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="block">
                  <span className="text-ink-2">{t('integrations.form.name')}</span>
                  <input className="input mt-1" required value={state.name} onChange={(e) => set('name', e.target.value)} autoFocus />
                </label>
                <div className="grid gap-3 sm:grid-cols-2">
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.owner')}</span>
                    <select
                      className="input mt-1"
                      value={state.ownerTeamId}
                      onChange={(e) => {
                        const team = teamOptions.find((x) => x.id === e.target.value);
                        setState((s) => ({ ...s, ownerTeamId: e.target.value, accessScope: s.accessScope || team?.accessScopes[0] || '' }));
                      }}
                    >
                      <option value="">{t('integrations.form.noOwner')}</option>
                      {teamOptions.map((team) => (
                        <option key={team.id} value={team.id}>
                          {team.name}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.scope')}</span>
                    <input className="input mono mt-1" required value={state.accessScope} onChange={(e) => set('accessScope', e.target.value)} placeholder="prod-azure" />
                  </label>
                </div>
              </>
            )}

            {step === 'credentials' && !created && (
              <>
                {state.type === 'atlas' && (
                  <fieldset className="rounded-md border border-line p-3">
                    <legend className="px-1 text-ink-2">{t('integrations.form.hmac')}</legend>
                    <label className="block">
                      <span className="text-ink-2">{t('integrations.form.hmacSecret')}</span>
                      <input className="input mono mt-1" type="password" autoComplete="off" value={state.hmacSecret} onChange={(e) => set('hmacSecret', e.target.value)} />
                    </label>
                    <div className="mt-2 flex flex-wrap items-center gap-4">
                      <label className="flex items-center gap-2">
                        <span className="text-ink-2">{t('integrations.form.hmacAlgorithm')}</span>
                        <select className="input h-7 w-auto" value={state.hmacAlgorithm} onChange={(e) => set('hmacAlgorithm', e.target.value as 'sha1' | 'sha256')}>
                          <option value="sha1">sha1 (X-MMS-Signature)</option>
                          <option value="sha256">sha256</option>
                        </select>
                      </label>
                      <label className="flex items-center gap-2 text-xs">
                        <input type="checkbox" checked={state.hmacRequired} onChange={(e) => set('hmacRequired', e.target.checked)} />
                        {t('integrations.form.hmacRequired')}
                      </label>
                    </div>
                    <p className="m-0 mt-2 text-xs text-ink-2">{t('integrations.form.hmacHint')}</p>
                  </fieldset>
                )}
                {state.type === 'atlas' && (
                  <fieldset className="rounded-md border border-line p-3">
                    <legend className="px-1 text-ink-2">{t('integrations.form.atlasApi')}</legend>
                    <div className="grid gap-2 sm:grid-cols-3">
                      <label className="block">
                        <span className="text-ink-2">{t('integrations.form.atlasGroup')}</span>
                        <input className="input mono mt-1" value={state.atlasGroupId} onChange={(e) => set('atlasGroupId', e.target.value)} />
                      </label>
                      <label className="block">
                        <span className="text-ink-2">{t('integrations.form.atlasPublic')}</span>
                        <input className="input mono mt-1" value={state.atlasPublicKey} onChange={(e) => set('atlasPublicKey', e.target.value)} autoComplete="off" />
                      </label>
                      <label className="block">
                        <span className="text-ink-2">{t('integrations.form.atlasPrivate')}</span>
                        <input className="input mono mt-1" type="password" value={state.atlasPrivateKey} onChange={(e) => set('atlasPrivateKey', e.target.value)} autoComplete="off" />
                      </label>
                    </div>
                    <p className="m-0 mt-2 text-xs text-ink-2">{t('integrations.form.atlasHint')}</p>
                  </fieldset>
                )}
                {state.type === 'azure_monitor' && <p className="m-0 text-ink-2">{t('integrations.wizard.azureCredentials')}</p>}
                {state.type === 'generic_webhook' && <p className="m-0 text-ink-2">{t('integrations.wizard.genericCredentials')}</p>}
                <button type="button" className="btn btn-primary justify-self-start" onClick={createCredentials} disabled={create.isPending} data-testid="wizard-create">
                  {t('integrations.wizard.create')}
                </button>
              </>
            )}
            {step === 'credentials' && created && (
              <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
                <dt className="text-ink-2">{t('integrations.ingestPath')}</dt>
                <dd className="mono m-0">{created.ingestPath}</dd>
                <dt className="text-ink-2">{t('integrations.keyId')}</dt>
                <dd className="mono m-0">{created.integration.ingestKeyId}</dd>
                <dt className="text-ink-2">{t('integrations.token')}</dt>
                <dd className="m-0 text-ink-2">{t('integrations.tokenShownOnce')}</dd>
              </dl>
            )}

            {step === 'mapping' && (
              <div className="grid gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
                <div className="grid gap-2">
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.mappingName')}</span>
                    <input className="input mt-1" value={mappingName} onChange={(e) => set('mappingName', e.target.value)} />
                  </label>
                  <YamlEditor value={yaml} onChange={(v) => set('yaml', v)} label={t('integrations.form.mappingYaml')} testId="wizard-yaml" />
                </div>
                <div className="grid content-start gap-2">
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.sample')}</span>
                    <textarea className="input mono mt-1 h-64 py-1 text-xs" value={sample} onChange={(e) => set('sample', e.target.value)} spellCheck={false} data-testid="wizard-sample" />
                  </label>
                  <button type="button" className="btn justify-self-start" onClick={runPreview} disabled={previewMutation.isPending} data-testid="wizard-preview">
                    {t('integrations.preview')}
                  </button>
                  {preview && <PreviewCard item={preview} />}
                </div>
              </div>
            )}

            {step === 'identity' && preview && (
              <>
                <p className="m-0 text-ink-2">{t('integrations.wizard.identityExplain')}</p>
                <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
                  {preview.identity.map((c) => (
                    <div key={c.name} className="contents">
                      <dt className="mono text-ink-2">{c.name}</dt>
                      <dd className="mono m-0 break-all">{c.value}</dd>
                    </div>
                  ))}
                  <dt className="text-ink-2">{t('integrations.fingerprint')}</dt>
                  <dd className="mono m-0 break-all" data-testid="wizard-fingerprint">
                    {preview.fingerprint}
                  </dd>
                  <dt className="text-ink-2">{t('integrations.deliveryKey')}</dt>
                  <dd className="mono m-0 break-all">{preview.deliveryKey}</dd>
                </dl>
              </>
            )}

            {step === 'lifecycle' && (
              <>
                <div role="radiogroup" aria-label={t('integrations.form.profile')} className="grid gap-2">
                  {LIFECYCLE_PROFILES.map((p) => (
                    <label key={p.value} className={`flex cursor-pointer gap-3 rounded-md border p-3 ${state.profile === p.value ? 'border-accent bg-surface-2' : 'border-line'}`}>
                      <input type="radio" name="profile" value={p.value} checked={state.profile === p.value} onChange={() => set('profile', p.value)} />
                      <span>
                        <span className="font-semibold">{p.label}</span>
                        {preview?.lifecycleProfileHint === p.value && <span className="badge ml-2">{t('integrations.wizard.fromMapping')}</span>}
                        <span className="block text-xs text-ink-2">{p.text}</span>
                      </span>
                    </label>
                  ))}
                </div>
                {state.profile === 'repeating_while_active' && (
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.repeatInterval')}</span>
                    <input className="input mt-1 w-32" type="number" min={1} max={1440} value={state.repeatIntervalMinutes} onChange={(e) => set('repeatIntervalMinutes', Number(e.target.value))} />
                  </label>
                )}
              </>
            )}

            {step === 'severity' && (
              <>
                <p className="m-0 text-ink-2">{t('integrations.wizard.severityExplain')}</p>
                {preview && (
                  <table className="w-full max-w-xl border-collapse text-sm">
                    <tbody>
                      {['severity', 'summary', 'service', 'environment', 'resource_name', 'rule_name', 'runbook_url'].map((f) => (
                        <tr key={f} className="border-b border-line">
                          <th className="py-1 pr-3 text-left font-normal text-ink-2">{f}</th>
                          <td className="mono py-1 break-all">{preview.fields[f] ?? <span className="text-ink-2">—</span>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
                <button type="button" className="btn justify-self-start" onClick={() => setStep('mapping')}>
                  {t('integrations.wizard.backToMapping')}
                </button>
              </>
            )}

            {step === 'routing' && (
              <>
                {preview?.routing ? (
                  <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
                    <dt className="text-ink-2">{t('integrations.routing.team')}</dt>
                    <dd className="m-0" data-testid="routing-team">
                      {preview.routing.teamName ?? t('integrations.routing.none')}
                    </dd>
                    <dt className="text-ink-2">{t('integrations.routing.rule')}</dt>
                    <dd className="m-0">{preview.routing.ruleName ?? t('integrations.routing.fallback')}</dd>
                    <dt className="text-ink-2">{t('integrations.routing.why')}</dt>
                    <dd className="m-0 text-ink-2">{preview.routing.why}</dd>
                  </dl>
                ) : (
                  <p className="m-0 text-ink-2">{t('integrations.routing.noPreview')}</p>
                )}
                <p className="m-0 text-ink-2">
                  {t('integrations.routing.destinations', { count: (destinations.data ?? []).filter((d) => d.active && (!state.ownerTeamId || d.teamId === state.ownerTeamId || d.teamId === null)).length })}{' '}
                  <Link to="/destinations">{t('destinations.title')}</Link>
                </p>
              </>
            )}

            {step === 'inactivity' && (
              <div className="rounded-md border border-line bg-surface-2 p-3" data-testid="wizard-inactivity">
                {closeAfter === null ? (
                  <p className="m-0">{t('integrations.wizard.noAutoResolve')}</p>
                ) : (
                  <p className="m-0">
                    {t('integrations.wizard.autoResolvePreview', { minutes: closeAfter })}{' '}
                    {state.coverageKind === 'none' ? t('integrations.wizard.unverifiedConsequence') : t('integrations.wizard.verifiedConsequence')}
                  </p>
                )}
                <p className="m-0 mt-2 text-xs text-ink-2">{t('integrations.wizard.expiryNote')}</p>
              </div>
            )}

            {step === 'coverage' && (
              <>
                <div role="radiogroup" aria-label={t('integrations.form.coverage')} className="grid gap-2">
                  {(['none', 'managed_canary', 'registered_heartbeat', ...(state.type === 'atlas' ? (['api_probe'] as const) : [])] as CoverageKind[]).map((k) => (
                    <label key={k} className={`flex cursor-pointer gap-3 rounded-md border p-3 ${state.coverageKind === k ? 'border-accent bg-surface-2' : 'border-line'}`}>
                      <input type="radio" name="coverage" value={k} checked={state.coverageKind === k} onChange={() => set('coverageKind', k)} />
                      <span>
                        <span className="font-semibold">{t(`integrations.coverage.${k}`)}</span>
                        <span className="block text-xs text-ink-2">{t(`integrations.coverage.${k}Hint`)}</span>
                      </span>
                    </label>
                  ))}
                </div>
                {state.coverageKind === 'managed_canary' && (
                  <div className="grid gap-2 sm:grid-cols-4">
                    {(['canaryIntervalMinutes', 'canaryDelayedMinutes', 'canaryAlertMinutes', 'canaryUnavailableMinutes'] as const).map((k) => (
                      <label key={k} className="block">
                        <span className="text-ink-2">{t(`integrations.form.${k}`)}</span>
                        <input className="input mt-1" type="number" min={1} value={state[k]} onChange={(e) => set(k, Number(e.target.value))} />
                      </label>
                    ))}
                  </div>
                )}
                {state.coverageKind === 'registered_heartbeat' && (
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.heartbeat')}</span>
                    <select className="input mt-1" value={state.heartbeatId} onChange={(e) => set('heartbeatId', e.target.value)}>
                      <option value="">—</option>
                      {heartbeats.data?.map((h) => (
                        <option key={h.id} value={h.id}>
                          {h.name}
                        </option>
                      ))}
                    </select>
                  </label>
                )}
                {state.coverageKind === 'api_probe' && (
                  <label className="block">
                    <span className="text-ink-2">{t('integrations.form.probeInterval')}</span>
                    <input className="input mt-1 w-32" type="number" min={1} value={state.probeIntervalMinutes} onChange={(e) => set('probeIntervalMinutes', Number(e.target.value))} />
                  </label>
                )}
                <p className="m-0 text-xs" style={{ color: state.coverageKind === 'none' ? 'var(--sev-high)' : undefined }}>
                  {state.coverageKind === 'none' ? t('integrations.wizard.coverageConsequence') : t('integrations.wizard.coverageOk')}
                </p>
              </>
            )}

            {step === 'dryRun' && (
              <>
                {preview && <PreviewCard item={preview} />}
                <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
                  <dt className="text-ink-2">{t('integrations.form.profile')}</dt>
                  <dd className="m-0">{profile.label}</dd>
                  <dt className="text-ink-2">{t('integrations.form.coverage')}</dt>
                  <dd className="m-0">{t(`integrations.coverage.${state.coverageKind}`)}</dd>
                  <dt className="text-ink-2">{t('integrations.form.mappingYaml')}</dt>
                  <dd className="m-0">{seeded?.yaml === yaml ? t('integrations.wizard.mappingUnchanged') : t('integrations.wizard.mappingNewVersion')}</dd>
                </dl>
                <button type="button" className="btn btn-primary justify-self-start" onClick={() => void activateAll()} disabled={update.isPending || createVersion.isPending} data-testid="wizard-activate">
                  {t('integrations.wizard.activate')}
                </button>
              </>
            )}

            {problem && (
              <div aria-live="polite">
                <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
              </div>
            )}
          </div>
          <div className="mt-6 flex gap-2">
            <button type="button" className="btn" disabled={index === 0} onClick={() => setStep(STEPS[index - 1] ?? step)}>
              {t('integrations.wizard.back')}
            </button>
            {index < STEPS.length - 1 && (
              <button type="button" className="btn btn-primary" disabled={!canNext()} onClick={() => setStep(STEPS[index + 1] ?? step)} data-testid="wizard-next">
                {t('integrations.wizard.next')}
              </button>
            )}
          </div>
        </section>
      </div>
      <OneTimeSecretDialog open={secretOpen && created !== null} title={t('integrations.tokenTitle')} secret={created?.ingestToken ?? ''} secretLabel={t('integrations.token')} snippets={created ? [{ label: 'curl', code: `curl -fsS -X POST -H 'Authorization: Bearer ${created.ingestToken}' -H 'Content-Type: application/json' --data @payload.json <ingest-host>${created.ingestPath}` }] : undefined} onClose={() => setSecretOpen(false)} />
    </div>
  );
}

export function PreviewCard({ item }: { item: MappingPreviewItem }) {
  const { t } = useTranslation();
  return (
    <div className="rounded-md border border-line bg-surface-2 p-2 text-xs" data-testid="preview-card" data-ok={item.ok}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="badge" style={item.ok ? undefined : { color: 'var(--sev-critical)', borderColor: 'var(--sev-critical)' }}>
          <span aria-hidden="true">{item.ok ? '✓' : '✕'}</span>
          {item.ok ? t('integrations.previewOk') : t('integrations.previewFailed')}
        </span>
        {!item.applies && <span className="badge border-dashed">{t('integrations.previewNotApplies')}</span>}
        <span className="mono text-ink-2">{item.source}</span>
      </div>
      {item.error && (
        <p className="m-0 mt-1" style={{ color: 'var(--sev-critical)' }}>
          {item.errorField ? `${item.errorField}: ` : ''}
          {item.error}
        </p>
      )}
      {item.ok && (
        <dl className="m-0 mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
          {['event_type', 'severity', 'summary', 'resource_id', 'rule_id', 'environment'].map((f) => (
            <div key={f} className="contents">
              <dt className="mono text-ink-2">{f}</dt>
              <dd className="mono m-0 break-all" data-field={f}>
                {item.fields[f] ?? '—'}
              </dd>
            </div>
          ))}
          {item.routing && (
            <>
              <dt className="text-ink-2">{t('integrations.routing.team')}</dt>
              <dd className="m-0">{item.routing.teamName ?? t('integrations.routing.none')}</dd>
            </>
          )}
        </dl>
      )}
    </div>
  );
}
