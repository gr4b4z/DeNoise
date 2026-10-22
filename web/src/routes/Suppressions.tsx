import { useQuery } from '@tanstack/react-query';
import { useEffect, useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { teamsQuery } from '@/episodes/queries';
import { browserZone, localInputToWallClock, rowsToPredicate, SCOPE_FIELDS, suppressionsQuery, useCancelSuppression, useCreateSuppression, usePreviewScope, type ScopeRow, type SuppressionDto } from '@/suppressions/queries';

const COMMON_ZONES = ['UTC', 'Europe/Warsaw', 'Europe/London', 'Europe/Berlin', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Asia/Kolkata', 'Asia/Singapore', 'Australia/Sydney'];

function zones(): string[] {
  try {
    const all = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.('timeZone');
    return all && all.length > 0 ? all : COMMON_ZONES;
  } catch {
    return COMMON_ZONES;
  }
}

function defaultLocal(offsetMinutes: number): string {
  const d = new Date(Date.now() + offsetMinutes * 60_000);
  d.setSeconds(0, 0);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** 08 §3.7: calendar-ish list of maintenance windows and silences with the scope rendered as a sentence, zone, actor, reason; create form requires reason and end. */
export function SuppressionsPage() {
  const { t } = useTranslation();
  const me = useMe();
  const [all, setAll] = useState(false);
  const list = useQuery(suppressionsQuery(all));
  const cancel = useCancelSuppression();
  const [creating, setCreating] = useState(false);
  const [cancelling, setCancelling] = useState<SuppressionDto | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const canCreate = can(me.data, P.suppressionCreate);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('suppressions.title')}</h1>
        <label className="flex items-center gap-1 text-xs text-ink-2">
          <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} />
          {t('suppressions.includeEnded')}
        </label>
        {canCreate && (
          <button type="button" className="btn btn-primary ml-auto" onClick={() => setCreating(true)} data-testid="suppression-new">
            {t('suppressions.new')}
          </button>
        )}
      </div>
      {problem && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
        </div>
      )}
      {list.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={list.error as unknown as Problem} />
        </div>
      )}
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_auto]">
        {list.data?.length === 0 ? (
          <EmptyState variant="healthy" title={t('suppressions.empty')} detail={t('suppressions.emptyDetail')} />
        ) : (
          <div className="min-h-0 overflow-auto bg-surface">
            <table className="w-full border-collapse text-sm">
              <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
                <tr className="h-8 border-b border-line">
                  <th className="pl-3 font-normal">{t('suppressions.columns.window')}</th>
                  <th className="font-normal">{t('suppressions.columns.kind')}</th>
                  <th className="font-normal">{t('suppressions.columns.scope')}</th>
                  <th className="font-normal">{t('suppressions.columns.reason')}</th>
                  <th className="font-normal max-lg:hidden">{t('suppressions.columns.by')}</th>
                  <th className="font-normal">{t('suppressions.columns.state')}</th>
                  <th className="pr-3" />
                </tr>
              </thead>
              <tbody>
                {list.data?.map((s) => {
                  const ended = !!s.cancelledAt || (!s.active && !!s.summarySentAt);
                  return (
                    <tr key={s.id} className="border-b border-line align-top hover:bg-surface-2" data-suppression-id={s.id} data-active={s.active}>
                      <td className="mono py-2 pl-3 text-xs whitespace-nowrap" data-testid="suppression-window">
                        {s.window}
                        {s.name && <span className="block font-sans text-ink-2">{s.name}</span>}
                      </td>
                      <td className="py-2">
                        <span className="badge">
                          <span aria-hidden="true">{s.kind === 'maintenance' ? '🛠' : '⏸'}</span>
                          {t(`suppressions.kind.${s.kind}`)}
                        </span>
                      </td>
                      <td className="py-2 text-ink-2" data-testid="suppression-scope">
                        {s.scopeText}
                      </td>
                      <td className="py-2">{s.reason}</td>
                      <td className="py-2 text-xs text-ink-2 max-lg:hidden">
                        {s.createdBy?.slice(0, 8) ?? '—'} · <RelativeTime iso={s.createdAt} mode="sentence" />
                      </td>
                      <td className="py-2">
                        {s.cancelledAt ? (
                          <span className="badge border-dashed">{t('suppressions.state.cancelled')}</span>
                        ) : s.active ? (
                          <span className="badge font-semibold">
                            <span aria-hidden="true">●</span>
                            {t('suppressions.state.active')}
                          </span>
                        ) : ended ? (
                          <span className="badge border-dashed">{t('suppressions.state.ended')}</span>
                        ) : (
                          <span className="badge">{t('suppressions.state.scheduled')}</span>
                        )}
                        {s.summarySentAt && <span className="ml-2 text-xs text-ink-2">{t('suppressions.state.summarySent')}</span>}
                      </td>
                      <td className="py-2 pr-3 text-right">
                        {canCreate && !ended && (
                          <button type="button" className="btn btn-sm" onClick={() => setCancelling(s)}>
                            {t('suppressions.endNow')}
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        {creating && <CreateForm onDone={() => setCreating(false)} />}
      </div>
      <ConfirmDialog
        open={cancelling !== null}
        onOpenChange={(o) => {
          if (!o) setCancelling(null);
        }}
        title={t('suppressions.endTitle')}
        description={t('suppressions.endWhy')}
        verb={t('suppressions.endNow')}
        busy={cancel.isPending}
        onConfirm={() => {
          if (!cancelling) return;
          cancel.mutate(cancelling.id, { onSuccess: () => setCancelling(null), onError: (e) => setProblem(e as unknown as Problem) });
        }}
      />
    </div>
  );
}

function CreateForm({ onDone }: { onDone: () => void }) {
  const { t } = useTranslation();
  const teams = useQuery(teamsQuery());
  const create = useCreateSuppression();
  const preview = usePreviewScope();
  const [kind, setKind] = useState<'maintenance' | 'silence'>('maintenance');
  const [name, setName] = useState('');
  const [reason, setReason] = useState('');
  const [zone, setZone] = useState(browserZone());
  const [starts, setStarts] = useState(() => defaultLocal(0));
  const [ends, setEnds] = useState(() => defaultLocal(120));
  const [autoPause, setAutoPause] = useState(true);
  const [rows, setRows] = useState<ScopeRow[]>([{ field: 'service', op: 'eq', value: '' }]);
  const [problem, setProblem] = useState<Problem | null>(null);
  const zoneList = zones();
  const predicate = rowsToPredicate(rows);

  useEffect(() => {
    const handle = setTimeout(() => preview.mutate(predicate), 300);
    return () => clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `preview` is a stable mutation handle
  }, [JSON.stringify(predicate)]);

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setProblem(null);
    const startsLocal = localInputToWallClock(starts);
    const endsLocal = localInputToWallClock(ends);
    if (!startsLocal || !endsLocal) {
      setProblem({ title: t('suppressions.form.invalidTimes'), status: 400 });
      return;
    }
    create.mutate(
      { kind, scope: predicate, timeZone: zone, reason: reason.trim(), startsLocal, endsLocal, autoPauseHeartbeats: autoPause, name: name.trim() || null },
      { onSuccess: onDone, onError: (err) => setProblem(err as unknown as Problem) },
    );
  };

  return (
    <form onSubmit={submit} className="w-[min(520px,100vw)] overflow-y-auto border-l border-line bg-surface p-4 text-sm" aria-label={t('suppressions.new')}>
      <h2 className="m-0 text-base">{t('suppressions.new')}</h2>
      <div role="radiogroup" aria-label={t('suppressions.form.kind')} className="mt-3 flex gap-1">
        {(['maintenance', 'silence'] as const).map((k) => (
          <button key={k} type="button" role="radio" aria-checked={kind === k} className={`btn btn-sm ${kind === k ? 'bg-surface-2 font-semibold' : ''}`} onClick={() => setKind(k)}>
            {t(`suppressions.kind.${k}`)}
          </button>
        ))}
      </div>
      <p className="m-0 mt-1 text-xs text-ink-2">{t(`suppressions.form.${kind}Hint`)}</p>
      <label className="mt-3 block">
        <span className="text-ink-2">{t('suppressions.form.name')}</span>
        <input className="input mt-1" value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <label className="mt-3 block">
        <span className="text-ink-2">{t('suppressions.form.reason')}</span>
        <input className="input mt-1" required value={reason} onChange={(e) => setReason(e.target.value)} />
      </label>

      <fieldset className="mt-3 rounded-md border border-line p-3">
        <legend className="px-1 text-ink-2">{t('suppressions.form.scope')}</legend>
        <div className="grid gap-1">
          {rows.map((row, i) => (
            <div key={i} className="grid grid-cols-[1fr_auto_1fr_auto] gap-1">
              <select className="input" aria-label={t('suppressions.form.field')} value={row.field} onChange={(e) => setRows(rows.map((r, j) => (j === i ? { ...r, field: e.target.value } : r)))}>
                {SCOPE_FIELDS.map((f) => (
                  <option key={f} value={f}>
                    {f}
                  </option>
                ))}
              </select>
              <select className="input w-24" aria-label={t('suppressions.form.operator')} value={row.op} onChange={(e) => setRows(rows.map((r, j) => (j === i ? { ...r, op: e.target.value as ScopeRow['op'] } : r)))}>
                <option value="eq">is</option>
                <option value="neq">is not</option>
                <option value="in">is one of</option>
                <option value="regex">matches</option>
              </select>
              {row.field === 'owning_team_id' ? (
                <select className="input" aria-label={t('suppressions.form.value')} value={row.value} onChange={(e) => setRows(rows.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))}>
                  <option value="">—</option>
                  {teams.data?.map((team) => (
                    <option key={team.id} value={team.id}>
                      {team.name}
                    </option>
                  ))}
                </select>
              ) : (
                <input className="input mono" aria-label={t('suppressions.form.value')} value={row.value} placeholder={row.op === 'in' ? 'a, b' : ''} onChange={(e) => setRows(rows.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))} />
              )}
              <button type="button" className="btn" aria-label={t('suppressions.form.removeRow')} onClick={() => setRows(rows.filter((_, j) => j !== i))}>
                ×
              </button>
            </div>
          ))}
          <button type="button" className="btn btn-sm justify-self-start" onClick={() => setRows([...rows, { field: 'environment', op: 'eq', value: '' }])}>
            {t('suppressions.form.addRow')}
          </button>
        </div>
        <p className="m-0 mt-2 text-xs" aria-live="polite" data-testid="scope-preview">
          {preview.data ? t('suppressions.form.scopeReads', { text: preview.data.text }) : preview.isError ? <span style={{ color: 'var(--sev-critical)' }}>{(preview.error as unknown as Problem).detail}</span> : t('suppressions.form.scopeEverything')}
        </p>
      </fieldset>

      <div className="mt-3 grid gap-3 sm:grid-cols-2">
        <label className="block">
          <span className="text-ink-2">{t('suppressions.form.starts')}</span>
          <input className="input mt-1" type="datetime-local" required value={starts} onChange={(e) => setStarts(e.target.value)} />
        </label>
        <label className="block">
          <span className="text-ink-2">{t('suppressions.form.ends')}</span>
          <input className="input mt-1" type="datetime-local" required value={ends} onChange={(e) => setEnds(e.target.value)} />
        </label>
      </div>
      <label className="mt-3 block">
        <span className="text-ink-2">{t('suppressions.form.timeZone')}</span>
        <input className="input mt-1" list="sup-tz" required value={zone} onChange={(e) => setZone(e.target.value)} />
        <datalist id="sup-tz">
          {zoneList.map((z) => (
            <option key={z} value={z} />
          ))}
        </datalist>
        <span className="text-xs text-ink-2">{t('suppressions.form.timeZoneHint')}</span>
      </label>
      {kind === 'maintenance' && (
        <label className="mt-3 flex items-center gap-2">
          <input type="checkbox" checked={autoPause} onChange={(e) => setAutoPause(e.target.checked)} />
          {t('suppressions.form.autoPause')}
        </label>
      )}
      {problem && (
        <div className="mt-3" aria-live="polite">
          <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
        </div>
      )}
      <div className="mt-4 flex justify-end gap-2">
        <button type="button" className="btn" onClick={onDone}>
          {t('actions.cancel')}
        </button>
        <button type="submit" className="btn btn-primary" disabled={create.isPending} data-testid="suppression-create">
          {t('suppressions.create')}
        </button>
      </div>
    </form>
  );
}
