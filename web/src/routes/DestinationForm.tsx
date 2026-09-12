import * as Dialog from '@radix-ui/react-dialog';
import * as Tabs from '@radix-ui/react-tabs';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { OneTimeSecretDialog } from '@/components/OneTimeSecretDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { teamsQuery } from '@/episodes/queries';
import { utc } from '@/lib/time';
import {
  BUILTIN,
  DEFAULT_EVENT_TYPES,
  EVENT_TYPES,
  deliveriesQuery,
  destinationQuery,
  destinationsQuery,
  headersToObject,
  prettyBody,
  templatesQuery,
  useCreateDestination,
  useCreateDestinationPair,
  useRevealDestination,
  useTestDestination,
  useUpdateDestination,
  type CreateDestinationRequest,
  type DestinationSummary,
  type TestSendResponse,
  type UpdateDestinationRequest,
} from '@/notifications/queries';
import { SampleSourcePicker, StoredTemplatePreview } from '@/notifications/TemplatePreview';
import { ChannelBadge } from '@/routes/Destinations';

interface HeaderRow {
  name: string;
  value: string;
}

interface FormState {
  name: string;
  teamId: string;
  channelType: 'webhook' | 'smtp_email';
  url: string;
  method: string;
  replaceHeaders: boolean;
  headers: HeaderRow[];
  templateId: string;
  timeoutSeconds: number;
  eventTypes: string[];
  emailTo: string;
  fallbackId: string;
  newFallback: boolean;
  fallbackName: string;
  fallbackUrl: string;
  rotateSecret: boolean;
  active: boolean;
}

function timeoutSeconds(value: string | null | undefined): number {
  const m = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})/.exec(value ?? '');
  return m ? Number(m[1] ?? 0) * 86_400 + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]) : 10;
}

function toTimeSpan(seconds: number): string {
  const s = Math.max(1, Math.min(120, Math.round(seconds)));
  return `00:${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
}

function fromSummary(d: DestinationSummary | undefined): FormState {
  return {
    name: d?.name ?? '',
    teamId: d?.teamId ?? '',
    channelType: (d?.channelType as 'webhook' | 'smtp_email' | undefined) ?? 'webhook',
    url: '',
    method: d?.method ?? 'POST',
    replaceHeaders: !d,
    headers: [{ name: '', value: '' }],
    templateId: d?.bodyTemplateId ?? '',
    timeoutSeconds: timeoutSeconds(d?.timeout),
    eventTypes: d ? [...d.eventTypes] : [...DEFAULT_EVENT_TYPES],
    emailTo: d?.emailTo?.join(', ') ?? '',
    fallbackId: d?.fallbackDestinationId ?? '',
    newFallback: false,
    fallbackName: '',
    fallbackUrl: '',
    rotateSecret: false,
    active: d?.active ?? true,
  };
}

function emails(value: string): string[] | null {
  const list = value
    .split(/[,\s;]+/)
    .map((x) => x.trim())
    .filter(Boolean);
  return list.length === 0 ? null : list;
}

function toCreate(f: FormState, fallbackDestinationId: string | null): CreateDestinationRequest {
  return {
    name: f.name.trim(),
    channelType: f.channelType,
    teamId: f.teamId || null,
    fallbackDestinationId,
    url: f.channelType === 'webhook' ? f.url.trim() : null,
    method: f.method,
    headers: f.channelType === 'webhook' ? headersToObject(f.headers) : null,
    signingSecret: null,
    timeout: toTimeSpan(f.timeoutSeconds),
    eventTypes: f.eventTypes,
    emailTo: f.channelType === 'smtp_email' ? emails(f.emailTo) : null,
  };
}

function toUpdate(f: FormState, existing: DestinationSummary): UpdateDestinationRequest {
  const webhook = existing.channelType === 'webhook';
  return {
    name: f.name.trim() !== existing.name ? f.name.trim() : null,
    teamId: f.teamId && f.teamId !== existing.teamId ? f.teamId : null,
    fallbackDestinationId: f.fallbackId !== existing.fallbackDestinationId ? f.fallbackId : null,
    url: webhook && f.url.trim() ? f.url.trim() : null,
    method: webhook && f.method !== existing.method ? f.method : null,
    headers: webhook && f.replaceHeaders ? (headersToObject(f.headers) ?? {}) : null,
    rotateSigningSecret: webhook && f.rotateSecret,
    timeout: toTimeSpan(f.timeoutSeconds),
    eventTypes: f.eventTypes,
    emailTo: !webhook ? emails(f.emailTo) : null,
    bodyTemplateId: webhook && f.templateId && f.templateId !== existing.bodyTemplateId ? f.templateId : null,
    clearBodyTemplate: webhook && !f.templateId && !!existing.bodyTemplateId,
    active: f.active !== existing.active ? f.active : null,
  };
}

/** Create page (`/destinations/new`) and edit/detail page (`/destinations/$id`) — 08 §3.7b. */
export function DestinationPage({ id }: { id?: string }) {
  const { t } = useTranslation();
  const me = useMe();
  const canManage = can(me.data, P.destinationManage);
  const detail = useQuery({ ...destinationQuery(id ?? ''), enabled: !!id });
  if (id && detail.isPending) return <p className="p-4 text-ink-2">{t('detail.loading')}</p>;
  if (id && detail.isError) {
    return (
      <div className="p-4">
        <ProblemBanner problem={detail.error as unknown as Problem} />
      </div>
    );
  }
  const existing = id ? detail.data : undefined;
  return (
    <div className="flex h-full min-h-0 flex-col bg-surface">
      <header className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
        <Link to="/destinations" className="text-xs text-ink-2">
          ← {t('destinations.title')}
        </Link>
        <h1 className="m-0 text-md" data-testid="destination-title">
          {existing ? existing.name : t('destinations.new')}
        </h1>
        {existing && <ChannelBadge channelType={existing.channelType} />}
        {existing && !existing.active && <span className="badge border-dashed">{t('destinations.inactive')}</span>}
        {existing && existing.consecutiveFailures > 0 && (
          <span className="badge" style={{ color: 'var(--sev-high)', borderColor: 'var(--sev-high)' }}>
            <span aria-hidden="true">!</span>
            {t('destinations.consecutiveFailures', { count: existing.consecutiveFailures })}
          </span>
        )}
        {existing && (
          <span className="ml-auto text-xs text-ink-2">
            {t('destinations.columns.lastSuccess')} <RelativeTime iso={existing.lastSuccessAt} mode="sentence" /> · {t('destinations.columns.lastFailure')} <RelativeTime iso={existing.lastFailureAt} mode="sentence" />
          </span>
        )}
      </header>
      {existing ? (
        <Tabs.Root defaultValue="settings" className="flex min-h-0 flex-1 flex-col">
          <Tabs.List className="flex gap-4 border-b border-line px-4" aria-label={t('destinations.sections')}>
            <Tab value="settings">{t('destinations.settings')}</Tab>
            <Tab value="deliveries">{t('destinations.deliveries')}</Tab>
          </Tabs.List>
          <Tabs.Content value="settings" className="min-h-0 flex-1 overflow-y-auto">
            <DestinationForm key={existing.version} existing={existing} readOnly={!canManage} />
          </Tabs.Content>
          <Tabs.Content value="deliveries" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
            <Deliveries id={existing.id} />
          </Tabs.Content>
        </Tabs.Root>
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto">
          <DestinationForm readOnly={!canManage} />
        </div>
      )}
    </div>
  );
}

function Tab({ value, children }: { value: string; children: string }) {
  return (
    <Tabs.Trigger value={value} className="h-9 border-b-2 border-transparent text-sm text-ink-2 data-[state=active]:border-accent data-[state=active]:text-ink">
      {children}
    </Tabs.Trigger>
  );
}

function DestinationForm({ existing, readOnly }: { existing?: DestinationSummary; readOnly: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const teams = useQuery(teamsQuery());
  const all = useQuery(destinationsQuery());
  const templates = useQuery(templatesQuery());
  const [form, setForm] = useState<FormState>(() => fromSummary(existing));
  const [problem, setProblem] = useState<Problem | null>(null);
  const [secret, setSecret] = useState<{ title: string; value: string; then?: () => void } | null>(null);
  const [sampleEpisode, setSampleEpisode] = useState('');
  const create = useCreateDestination();
  const createPair = useCreateDestinationPair();
  const update = useUpdateDestination(existing?.id ?? '');
  const busy = create.isPending || createPair.isPending || update.isPending;

  const others = (all.data ?? []).filter((d) => d.id !== existing?.id && d.active);
  const needsPair = !existing && others.length === 0;
  const pairMode = needsPair || form.newFallback;
  const webhook = form.channelType === 'webhook';
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }));
  const toggleEvent = (type: string) => set('eventTypes', form.eventTypes.includes(type) ? form.eventTypes.filter((x) => x !== type) : [...form.eventTypes, type]);
  const activeTemplates = (templates.data ?? []).filter((x) => x.active);
  const pickedTemplate = activeTemplates.find((x) => x.id === (form.templateId || BUILTIN.genericJson));

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setProblem(null);
    const fail = (err: unknown) => setProblem(err as Problem);
    if (existing) {
      update.mutate(
        { version: existing.version, body: toUpdate(form, existing) },
        {
          onSuccess: (r) => {
            if (r.signingSecret) setSecret({ title: t('destinations.signingSecretTitle'), value: r.signingSecret });
            else void navigate({ to: '/destinations' });
          },
          onError: fail,
        },
      );
      return;
    }
    if (pairMode) {
      const fallback: CreateDestinationRequest = { ...toCreate(form, null), name: form.fallbackName.trim(), channelType: 'webhook', url: form.fallbackUrl.trim(), headers: null, emailTo: null, eventTypes: DEFAULT_EVENT_TYPES };
      createPair.mutate(
        { primary: toCreate(form, null), fallback },
        {
          onSuccess: (r) => {
            const primaryId = r.primary.destination.id;
            const parts = [r.primary.signingSecret ? `${r.primary.destination.name}: ${r.primary.signingSecret}` : null, r.fallback.signingSecret ? `${r.fallback.destination.name}: ${r.fallback.signingSecret}` : null].filter(Boolean);
            if (parts.length > 0) setSecret({ title: t('destinations.signingSecretTitle'), value: parts.join('\n'), then: () => void navigate({ to: '/destinations/$id', params: { id: primaryId } }) });
            else void navigate({ to: '/destinations/$id', params: { id: primaryId } });
          },
          onError: fail,
        },
      );
      return;
    }
    create.mutate(toCreate(form, form.fallbackId), {
      onSuccess: (r) => {
        const go = () => void navigate({ to: '/destinations/$id', params: { id: r.destination.id } });
        if (r.signingSecret) setSecret({ title: t('destinations.signingSecretTitle'), value: r.signingSecret, then: go });
        else go();
      },
      onError: fail,
    });
  };

  return (
    <form onSubmit={submit} className="mx-auto grid max-w-5xl gap-4 px-4 py-4 text-sm lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]" aria-label={existing ? t('destinations.settings') : t('destinations.new')}>
      <fieldset className="contents" disabled={readOnly}>
        <div className="grid gap-4">
          <label className="block">
            <span className="text-ink-2">{t('destinations.form.name')}</span>
            <input className="input mt-1" required value={form.name} onChange={(e) => set('name', e.target.value)} autoFocus={!existing} />
          </label>
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="text-ink-2">{t('destinations.form.team')}</span>
              <select className="input mt-1" value={form.teamId} onChange={(e) => set('teamId', e.target.value)}>
                <option value="">{t('destinations.global')}</option>
                {teams.data?.map((team) => (
                  <option key={team.id} value={team.id}>
                    {team.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="block">
              <span className="text-ink-2">{t('destinations.form.channel')}</span>
              <select className="input mt-1" value={form.channelType} disabled={!!existing} onChange={(e) => set('channelType', e.target.value as FormState['channelType'])}>
                <option value="webhook">Webhook</option>
                <option value="smtp_email">Email</option>
              </select>
            </label>
          </div>

          {webhook ? (
            <>
              <label className="block">
                <span className="text-ink-2">{t('destinations.form.url')}</span>
                <input
                  className="input mono mt-1"
                  type="url"
                  required={!existing}
                  value={form.url}
                  onChange={(e) => set('url', e.target.value)}
                  placeholder={existing?.urlMasked ?? 'https://…'}
                  aria-describedby="url-hint"
                />
                <span id="url-hint" className="text-xs text-ink-2">
                  {existing ? t('destinations.form.urlMaskedHint') : t('destinations.form.urlHint')}
                </span>
              </label>
              <div className="grid gap-3 sm:grid-cols-2">
                <label className="block">
                  <span className="text-ink-2">{t('destinations.form.method')}</span>
                  <select className="input mt-1" value={form.method} onChange={(e) => set('method', e.target.value)}>
                    <option>POST</option>
                    <option>PUT</option>
                  </select>
                </label>
                <label className="block">
                  <span className="text-ink-2">{t('destinations.form.timeout')}</span>
                  <input className="input mt-1 w-28" type="number" min={1} max={120} value={form.timeoutSeconds} onChange={(e) => set('timeoutSeconds', Number(e.target.value))} />
                </label>
              </div>

              <fieldset className="rounded-md border border-line p-3">
                <legend className="px-1 text-ink-2">{t('destinations.form.headers')}</legend>
                {existing?.hasHeaders && (
                  <label className="mb-2 flex items-center gap-2 text-xs">
                    <input type="checkbox" checked={form.replaceHeaders} onChange={(e) => set('replaceHeaders', e.target.checked)} />
                    {t('destinations.form.replaceHeaders')}
                  </label>
                )}
                {(form.replaceHeaders || !existing?.hasHeaders) && (
                  <div className="grid gap-1">
                    {form.headers.map((row, i) => (
                      <div key={i} className="grid grid-cols-[1fr_1fr_auto] gap-1">
                        <input className="input mono" placeholder="X-Api-Key" aria-label={t('destinations.form.headerName')} value={row.name} onChange={(e) => set('headers', form.headers.map((r, j) => (j === i ? { ...r, name: e.target.value } : r)))} />
                        <input className="input mono" type="password" autoComplete="off" placeholder="value" aria-label={t('destinations.form.headerValue')} value={row.value} onChange={(e) => set('headers', form.headers.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))} />
                        <button type="button" className="btn" aria-label={t('destinations.form.removeHeader')} onClick={() => set('headers', form.headers.filter((_, j) => j !== i))}>
                          ×
                        </button>
                      </div>
                    ))}
                    <button type="button" className="btn btn-sm justify-self-start" onClick={() => set('headers', [...form.headers, { name: '', value: '' }])}>
                      {t('destinations.form.addHeader')}
                    </button>
                  </div>
                )}
                {existing?.hasHeaders && !form.replaceHeaders && <p className="m-0 text-xs text-ink-2">{t('destinations.form.headersSet')}</p>}
              </fieldset>

              <label className="block">
                <span className="text-ink-2">{t('destinations.form.template')}</span>
                <select className="input mt-1" value={form.templateId} onChange={(e) => set('templateId', e.target.value)} data-testid="template-picker">
                  <option value="">generic-json ({t('destinations.form.templateDefault')})</option>
                  {activeTemplates
                    .filter((x) => x.id !== BUILTIN.genericJson)
                    .map((x) => (
                      <option key={x.id} value={x.id}>
                        {x.name}
                        {x.builtin ? ` · ${t('templates.builtin')}` : ''} · v{x.version}
                      </option>
                    ))}
                </select>
              </label>

              {existing ? (
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={form.rotateSecret} onChange={(e) => set('rotateSecret', e.target.checked)} />
                  <span>
                    {t('destinations.form.rotateSecret')} <span className="text-xs text-ink-2">{existing.hasSigningSecret ? t('destinations.form.secretSet') : t('destinations.form.noSecret')}</span>
                  </span>
                </label>
              ) : (
                <p className="m-0 text-xs text-ink-2">{t('destinations.form.secretGenerated')}</p>
              )}
            </>
          ) : (
            <label className="block">
              <span className="text-ink-2">{t('destinations.form.emailTo')}</span>
              <textarea className="input mt-1 h-20 py-1" required value={form.emailTo} onChange={(e) => set('emailTo', e.target.value)} placeholder="oncall@example.com, noc@example.com" />
            </label>
          )}

          <fieldset className="rounded-md border border-line p-3">
            <legend className="px-1 text-ink-2">{t('destinations.form.events')}</legend>
            <div className="grid gap-1 sm:grid-cols-2">
              {EVENT_TYPES.map((type) => (
                <label key={type} className="flex items-center gap-2 text-xs">
                  <input type="checkbox" checked={form.eventTypes.includes(type)} onChange={() => toggleEvent(type)} />
                  <span className="mono">{type}</span>
                </label>
              ))}
            </div>
          </fieldset>

          <fieldset className="rounded-md border border-line p-3">
            <legend className="px-1 text-ink-2">{t('destinations.form.fallback')}</legend>
            {needsPair ? (
              <p className="m-0 mb-2 text-xs text-ink-2">{t('destinations.form.firstPair')}</p>
            ) : (
              <>
                <select className="input" required={!pairMode} disabled={pairMode} value={form.fallbackId} onChange={(e) => set('fallbackId', e.target.value)} aria-label={t('destinations.form.fallback')}>
                  <option value="">{t('destinations.form.pickFallback')}</option>
                  {others.map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.name}
                    </option>
                  ))}
                </select>
                {!existing && (
                  <label className="mt-2 flex items-center gap-2 text-xs">
                    <input type="checkbox" checked={form.newFallback} onChange={(e) => set('newFallback', e.target.checked)} />
                    {t('destinations.form.createFallback')}
                  </label>
                )}
              </>
            )}
            {pairMode && (
              <div className="mt-2 grid gap-2">
                <label className="block">
                  <span className="text-ink-2">{t('destinations.form.fallbackName')}</span>
                  <input className="input mt-1" required value={form.fallbackName} onChange={(e) => set('fallbackName', e.target.value)} />
                </label>
                <label className="block">
                  <span className="text-ink-2">{t('destinations.form.fallbackUrl')}</span>
                  <input className="input mono mt-1" type="url" required value={form.fallbackUrl} onChange={(e) => set('fallbackUrl', e.target.value)} placeholder="https://…" />
                  <span className="text-xs text-ink-2">{t('destinations.form.fallbackHint')}</span>
                </label>
              </div>
            )}
          </fieldset>

          {existing && (
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={form.active} onChange={(e) => set('active', e.target.checked)} />
              {t('destinations.form.active')}
            </label>
          )}

          {problem && (
            <div aria-live="polite">
              <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
            </div>
          )}
          {!readOnly && (
            <div className="flex flex-wrap items-center gap-2">
              <button type="submit" className="btn btn-primary" disabled={busy}>
                {existing ? t('destinations.save') : t('destinations.create')}
              </button>
              {existing && <TestSend id={existing.id} />}
              {existing?.channelType === 'webhook' && <Reveal id={existing.id} />}
            </div>
          )}
        </div>

        <aside className="grid content-start gap-2">
          {webhook && (
            <>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="text-ink-2">{pickedTemplate ? `${pickedTemplate.name} · v${pickedTemplate.version}` : t('templates.preview')}</span>
                <SampleSourcePicker value={sampleEpisode} onChange={setSampleEpisode} />
              </div>
              <StoredTemplatePreview templateId={form.templateId || BUILTIN.genericJson} version={pickedTemplate?.version ?? 1} episodeId={sampleEpisode || undefined} />
              <p className="m-0 text-xs text-ink-2">
                {t('destinations.form.previewHint')}{' '}
                <Link to="/templates" className="text-ink-2 underline">
                  {t('templates.title')}
                </Link>
              </p>
            </>
          )}
        </aside>
      </fieldset>

      <OneTimeSecretDialog
        open={secret !== null}
        title={secret?.title ?? ''}
        secret={secret?.value ?? ''}
        secretLabel={t('destinations.signingSecret')}
        onClose={() => {
          const then = secret?.then;
          setSecret(null);
          if (then) then();
          else void navigate({ to: '/destinations' });
        }}
      />
    </form>
  );
}

function TestSend({ id }: { id: string }) {
  const { t } = useTranslation();
  const test = useTestDestination(id);
  const [result, setResult] = useState<TestSendResponse | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  return (
    <div className="contents">
      <button
        type="button"
        className="btn"
        disabled={test.isPending}
        onClick={() => {
          setProblem(null);
          test.mutate(undefined, { onSuccess: setResult, onError: (e) => setProblem(e as unknown as Problem) });
        }}
      >
        {test.isPending ? t('destinations.testing') : t('destinations.test')}
      </button>
      <div className="basis-full" aria-live="polite" data-testid="test-result">
        {problem && <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />}
        {result && (
          <div className="rounded-md border border-line bg-surface-2 p-2 text-xs">
            <div className="flex flex-wrap items-center gap-2">
              <span className="badge" style={result.outcome === 'success' ? { color: 'var(--sev-low)', borderColor: 'var(--sev-low)' } : { color: 'var(--sev-critical)', borderColor: 'var(--sev-critical)' }}>
                <span aria-hidden="true">{result.outcome === 'success' ? '✓' : '✕'}</span>
                {result.outcome}
              </span>
              {result.httpStatus !== null && <span className="mono">HTTP {result.httpStatus}</span>}
              <span>{t('destinations.latency', { ms: result.latencyMs })}</span>
              {result.error && <span style={{ color: 'var(--sev-critical)' }}>{result.error}</span>}
            </div>
            {result.responseExcerpt && (
              <details className="mt-1">
                <summary className="cursor-pointer text-ink-2">{t('destinations.responseExcerpt')}</summary>
                <pre tabIndex={0} className="mono m-0 mt-1 max-h-40 overflow-auto whitespace-pre-wrap">{result.responseExcerpt}</pre>
              </details>
            )}
            <details className="mt-1">
              <summary className="cursor-pointer text-ink-2">
                {t('destinations.sentBody')} <span className="mono">{result.contentType}</span>
              </summary>
              <pre tabIndex={0} className="mono m-0 mt-1 max-h-60 overflow-auto whitespace-pre-wrap">{prettyBody(result.renderedBody, result.contentType)}</pre>
            </details>
          </div>
        )}
      </div>
    </div>
  );
}

/** URL and header values are masked after save; revealing them needs the password again and is never allowed for PATs (08 §3.7b). */
function Reveal({ id }: { id: string }) {
  const { t } = useTranslation();
  const reveal = useRevealDestination(id);
  const [open, setOpen] = useState(false);
  const [password, setPassword] = useState('');
  const close = () => {
    setOpen(false);
    setPassword('');
    reveal.reset();
  };
  return (
    <Dialog.Root open={open} onOpenChange={(o) => (o ? setOpen(true) : close())}>
      <Dialog.Trigger asChild>
        <button type="button" className="btn">
          {t('destinations.reveal')}
        </button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/30" />
        <Dialog.Content className="fixed top-1/2 left-1/2 w-[min(560px,calc(100vw-32px))] -translate-x-1/2 -translate-y-1/2 rounded-md border border-line bg-surface p-4 text-sm" style={{ boxShadow: 'var(--shadow-menu)' }}>
          <Dialog.Title className="m-0 text-md">{t('destinations.revealTitle')}</Dialog.Title>
          <Dialog.Description className="mt-1 text-xs text-ink-2">{t('destinations.revealWhy')}</Dialog.Description>
          {reveal.data ? (
            <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
              <dt className="text-ink-2">URL</dt>
              <dd className="mono m-0 break-all" data-testid="revealed-url">
                {reveal.data.url ?? '—'}
              </dd>
              {Object.entries(reveal.data.headers).map(([k, v]) => (
                <div key={k} className="contents">
                  <dt className="mono text-ink-2">{k}</dt>
                  <dd className="mono m-0 break-all">{v}</dd>
                </div>
              ))}
            </dl>
          ) : (
            <form
              className="mt-3 grid gap-2"
              onSubmit={(e) => {
                // The dialog is portalled, but React bubbles submit through the tree: keep it away from the destination form.
                e.preventDefault();
                e.stopPropagation();
                reveal.mutate(password);
              }}
            >
              <label className="block">
                <span className="text-ink-2">{t('destinations.password')}</span>
                <input className="input mt-1" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} autoFocus />
              </label>
              {reveal.isError && <ProblemBanner problem={reveal.error as unknown as Problem} />}
              <div className="flex justify-end gap-2">
                <button type="button" className="btn" onClick={close}>
                  {t('actions.cancel')}
                </button>
                <button type="submit" className="btn btn-primary" disabled={reveal.isPending}>
                  {t('destinations.reveal')}
                </button>
              </div>
            </form>
          )}
          {reveal.data && (
            <div className="mt-3 flex justify-end">
              <button type="button" className="btn" onClick={close}>
                {t('actions.close')}
              </button>
            </div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function Deliveries({ id }: { id: string }) {
  const { t } = useTranslation();
  const list = useQuery(deliveriesQuery(id));
  if (list.isError) return <ProblemBanner problem={list.error as unknown as Problem} />;
  if (!list.data) return <p className="m-0 text-ink-2">{t('detail.loading')}</p>;
  if (list.data.length === 0) return <p className="m-0 text-ink-2">{t('destinations.noDeliveries')}</p>;
  return (
    <table className="w-full border-collapse text-sm">
      <thead className="text-left text-xs text-ink-2">
        <tr className="h-8 border-b border-line">
          <th className="font-normal">{t('destinations.deliveryColumns.at')}</th>
          <th className="font-normal">{t('destinations.deliveryColumns.outcome')}</th>
          <th className="font-normal">{t('destinations.deliveryColumns.status')}</th>
          <th className="font-normal">{t('destinations.deliveryColumns.latency')}</th>
          <th className="font-normal">{t('destinations.deliveryColumns.fallback')}</th>
          <th className="font-normal">{t('destinations.deliveryColumns.detail')}</th>
        </tr>
      </thead>
      <tbody>
        {list.data.map((a) => (
          <tr key={a.id} className="border-b border-line align-top" data-outcome={a.outcome}>
            <td className="mono py-1 text-xs whitespace-nowrap">{utc(a.attemptedAt)}</td>
            <td className="py-1">
              <span className="badge" style={a.outcome === 'success' ? undefined : { color: 'var(--sev-high)', borderColor: 'var(--sev-high)' }}>
                {a.outcome}
              </span>
            </td>
            <td className="mono py-1">{a.httpStatus ?? '—'}</td>
            <td className="py-1">{a.latencyMs} ms</td>
            <td className="py-1">{a.usedFallback ? t('destinations.usedFallback') : ''}</td>
            <td className="py-1 text-xs text-ink-2">
              {a.error && <div style={{ color: 'var(--sev-critical)' }}>{a.error}</div>}
              {a.responseExcerpt && <pre tabIndex={0} className="mono m-0 max-h-24 overflow-auto whitespace-pre-wrap">{a.responseExcerpt}</pre>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
