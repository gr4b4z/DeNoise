import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useMemo, useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { OneTimeSecretDialog } from '@/components/OneTimeSecretDialog';
import { ProblemBanner } from '@/components/ProblemBanner';
import { teamsQuery } from '@/episodes/queries';
import { pingSnippets, schedulePreviewQuery, toShort, useCreateHeartbeat, useUpdateHeartbeat, type HeartbeatRequest, type HeartbeatSummary } from '@/heartbeats/queries';
import { local, utc } from '@/lib/time';

const SEVERITIES = ['critical', 'high', 'medium', 'low'] as const;
const COMMON_ZONES = ['UTC', 'Europe/Warsaw', 'Europe/London', 'Europe/Berlin', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Asia/Kolkata', 'Asia/Singapore', 'Australia/Sydney'];

function zones(): string[] {
  try {
    const all = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.('timeZone');
    return all && all.length > 0 ? all : COMMON_ZONES;
  } catch {
    return COMMON_ZONES;
  }
}

interface FormState {
  name: string;
  description: string;
  owningTeamId: string;
  kind: 'interval' | 'cron';
  interval: string;
  cron: string;
  timezone: string;
  grace: string;
  severityOnMiss: string;
  bindsToIntegrationId: string;
  recoverySuccessesRequired: number;
  autoPauseDuringMaintenance: boolean;
}

function fromSummary(h: HeartbeatSummary | undefined, defaultTeam: string): FormState {
  return {
    name: h?.name ?? '',
    description: h?.description ?? '',
    owningTeamId: h?.owningTeam?.id ?? defaultTeam,
    kind: (h?.schedule.kind as 'interval' | 'cron' | undefined) ?? 'interval',
    interval: h?.schedule.interval ? toShort(h.schedule.interval) : '5m',
    cron: h?.schedule.cron ?? '30 2 * * *',
    timezone: h?.schedule.timezone ?? (Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'),
    grace: h ? toShort(h.grace) : '5m',
    severityOnMiss: h?.severityOnMiss ?? 'high',
    bindsToIntegrationId: h?.bindsToIntegrationId ?? '',
    recoverySuccessesRequired: h?.recoverySuccessesRequired ?? 1,
    autoPauseDuringMaintenance: h?.autoPauseDuringMaintenance ?? true,
  };
}

function toRequest(f: FormState): HeartbeatRequest {
  return {
    name: f.name.trim(),
    description: f.description.trim() || null,
    owningTeamId: f.owningTeamId,
    assigneeId: null,
    schedule: { kind: f.kind, interval: f.kind === 'interval' ? f.interval.trim() : null, cron: f.kind === 'cron' ? f.cron.trim() : null, timezone: f.kind === 'cron' ? f.timezone : null },
    grace: f.grace.trim(),
    severityOnMiss: f.severityOnMiss,
    routingPolicyId: null,
    bindsToIntegrationId: f.bindsToIntegrationId || null,
    recoverySuccessesRequired: f.recoverySuccessesRequired,
    autoPauseDuringMaintenance: f.autoPauseDuringMaintenance,
    accessScope: null,
  };
}

/** Create/edit form (08 §3.4) with the schedule kind toggle, tz picker, "next 5 runs" preview and the one-time ping URL dialog. */
export function HeartbeatFormPage({ existing }: { existing?: HeartbeatSummary }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const teams = useQuery(teamsQuery());
  const [form, setForm] = useState<FormState>(() => fromSummary(existing, ''));
  const [problem, setProblem] = useState<Problem | null>(null);
  const [secret, setSecret] = useState<{ id: string; url: string } | null>(null);
  const create = useCreateHeartbeat();
  const update = useUpdateHeartbeat(existing?.id ?? '');
  const zoneList = useMemo(() => zones(), []);

  const teamOptions = useMemo(() => teams.data ?? [], [teams.data]);
  const effectiveTeam = form.owningTeamId || teamOptions[0]?.id || '';
  const scheduleReq = { kind: form.kind, interval: form.kind === 'interval' ? form.interval : null, cron: form.kind === 'cron' ? form.cron : null, timezone: form.kind === 'cron' ? form.timezone : null };
  const preview = useQuery(schedulePreviewQuery(scheduleReq, (form.kind === 'interval' ? form.interval.length > 1 : form.cron.length > 8) && !!form.timezone));

  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }));

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setProblem(null);
    const body = toRequest({ ...form, owningTeamId: effectiveTeam });
    if (existing) {
      update.mutate(
        { version: existing.version, body },
        { onSuccess: () => void navigate({ to: '/heartbeats/$id', params: { id: existing.id } }), onError: (err) => setProblem(err as unknown as Problem) },
      );
    } else {
      create.mutate(body, {
        onSuccess: (created) => setSecret({ id: created.heartbeat.id, url: created.pingUrl }),
        onError: (err) => setProblem(err as unknown as Problem),
      });
    }
  };

  return (
    <div className="h-full overflow-y-auto bg-surface">
      <form onSubmit={submit} className="mx-auto grid max-w-2xl gap-4 px-4 py-4 text-sm" aria-labelledby="hb-form-title">
        <h1 id="hb-form-title" className="text-md">
          {existing ? t('heartbeats.edit') : t('heartbeats.new')}
        </h1>
        <label className="block">
          <span className="text-ink-2">{t('heartbeats.form.name')}</span>
          <input className="input mt-1" required value={form.name} onChange={(e) => set('name', e.target.value)} autoFocus={!existing} />
        </label>
        <label className="block">
          <span className="text-ink-2">{t('heartbeats.form.description')}</span>
          <input className="input mt-1" value={form.description} onChange={(e) => set('description', e.target.value)} />
        </label>
        <label className="block">
          <span className="text-ink-2">{t('heartbeats.form.team')}</span>
          <select className="input mt-1" required value={effectiveTeam} onChange={(e) => set('owningTeamId', e.target.value)} aria-describedby="team-hint">
            {teamOptions.map((team) => (
              <option key={team.id} value={team.id}>
                {team.name}
              </option>
            ))}
          </select>
          {teams.data?.length === 0 && (
            <span id="team-hint" className="text-xs" style={{ color: 'var(--sev-high)' }}>
              {t('heartbeats.form.noTeams')}
            </span>
          )}
        </label>

        <fieldset className="rounded-md border border-line p-3">
          <legend className="px-1 text-ink-2">{t('heartbeats.form.schedule')}</legend>
          <div role="radiogroup" aria-label={t('heartbeats.form.scheduleKind')} className="flex gap-1">
            {(['interval', 'cron'] as const).map((kind) => (
              <button key={kind} type="button" role="radio" aria-checked={form.kind === kind} className={`btn btn-sm ${form.kind === kind ? 'bg-surface-2 font-semibold' : ''}`} onClick={() => set('kind', kind)}>
                {t(`heartbeats.form.${kind}`)}
              </button>
            ))}
          </div>
          {form.kind === 'interval' ? (
            <label className="mt-3 block">
              <span className="text-ink-2">{t('heartbeats.form.intervalLabel')}</span>
              <input className="input mt-1 mono w-40" value={form.interval} onChange={(e) => set('interval', e.target.value)} placeholder="5m" aria-describedby="interval-hint" />
              <span id="interval-hint" className="ml-2 text-xs text-ink-2">30s, 5m, 2h, 1d</span>
            </label>
          ) : (
            <div className="mt-3 grid gap-3 sm:grid-cols-2">
              <label className="block">
                <span className="text-ink-2">{t('heartbeats.form.cronLabel')}</span>
                <input className="input mt-1 mono" value={form.cron} onChange={(e) => set('cron', e.target.value)} placeholder="30 2 * * *" />
              </label>
              <label className="block">
                <span className="text-ink-2">{t('heartbeats.form.timezone')}</span>
                <input className="input mt-1" list="tz-list" value={form.timezone} onChange={(e) => set('timezone', e.target.value)} required />
                <datalist id="tz-list">
                  {zoneList.map((z) => (
                    <option key={z} value={z} />
                  ))}
                </datalist>
              </label>
            </div>
          )}
          <div className="mt-3 text-xs text-ink-2" aria-live="polite">
            {preview.data ? (
              <>
                <div>
                  {preview.data.description} · {t('heartbeats.form.nextRuns')}
                </div>
                <ol className="m-0 mt-1 list-none p-0">
                  {preview.data.nextRuns.map((r) => (
                    <li key={r} className="mono">
                      {utc(r)} <span className="text-ink-2">· local {local(r)}</span>
                    </li>
                  ))}
                </ol>
              </>
            ) : preview.isError ? (
              <span style={{ color: 'var(--sev-high)' }}>{(preview.error as unknown as Problem).detail ?? t('heartbeats.form.invalidSchedule')}</span>
            ) : null}
          </div>
        </fieldset>

        <div className="grid gap-3 sm:grid-cols-3">
          <label className="block">
            <span className="text-ink-2">{t('heartbeats.form.grace')}</span>
            <input className="input mt-1 mono" value={form.grace} onChange={(e) => set('grace', e.target.value)} placeholder="5m" />
          </label>
          <label className="block">
            <span className="text-ink-2">{t('heartbeats.form.severity')}</span>
            <select className="input mt-1" value={form.severityOnMiss} onChange={(e) => set('severityOnMiss', e.target.value)}>
              {SEVERITIES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </label>
          <label className="block">
            <span className="text-ink-2">{t('heartbeats.form.recovery')}</span>
            <input className="input mt-1" type="number" min={1} max={10} value={form.recoverySuccessesRequired} onChange={(e) => set('recoverySuccessesRequired', Math.max(1, Number(e.target.value) || 1))} />
          </label>
        </div>
        <label className="block">
          <span className="text-ink-2">{t('heartbeats.form.bind')}</span>
          <input className="input mt-1 mono" value={form.bindsToIntegrationId} onChange={(e) => set('bindsToIntegrationId', e.target.value)} placeholder={t('heartbeats.form.bindHint')} />
        </label>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={form.autoPauseDuringMaintenance} onChange={(e) => set('autoPauseDuringMaintenance', e.target.checked)} />
          {t('heartbeats.form.autoPause')}
        </label>

        <div aria-live="polite">
          <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
        </div>
        <div className="flex justify-end gap-2">
          <button type="button" className="btn" onClick={() => void navigate({ to: '/heartbeats', search: {} as never })}>
            {t('actions.cancel')}
          </button>
          <button type="submit" className="btn btn-primary" disabled={create.isPending || update.isPending}>
            {existing ? t('heartbeats.save') : t('heartbeats.create')}
          </button>
        </div>
      </form>
      {secret && (
        <OneTimeSecretDialog
          open
          title={t('heartbeats.pingUrlTitle')}
          secret={secret.url}
          secretLabel={t('heartbeats.pingUrl')}
          snippets={pingSnippets(secret.url)}
          onClose={() => {
            const id = secret.id;
            setSecret(null);
            void navigate({ to: '/heartbeats/$id', params: { id } });
          }}
        />
      )}
    </div>
  );
}
