import * as Tabs from '@radix-ui/react-tabs';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { EpisodeDetail, Problem, TimelineEntry } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { ConditionBadge, CoverageBadge, EvidenceBadge, HandlingBadge, SeverityBadge } from '@/components/Badges';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ExplanationPanel } from '@/components/ExplanationPanel';
import { ProblemBanner } from '@/components/ProblemBanner';
import { RelativeTime } from '@/components/RelativeTime';
import { episodeQuery, timelineQuery } from '@/episodes/queries';
import { primaryAction, useEpisodeAction, type EpisodeAction } from '@/episodes/actions';
import { problemCode } from '@/lib/problems';
import { local, utc } from '@/lib/time';

const VERB: Record<EpisodeAction['kind'], string> = {
  ack: 'Acknowledge',
  assignToMe: 'Assign to me',
  assign: 'Assign',
  note: 'Add note',
  close: 'Close',
  restore: 'Restore',
  silence: 'Silence',
};

type Pending = { action: EpisodeAction; title: string; withReason: boolean; reasonRequired: boolean } | null;

/** Shared by the drawer and the full page (08 §0): header, primary action, overview + timeline tabs. */
export function EpisodeView({ id, inDrawer = false, onClose }: { id: string; inDrawer?: boolean; onClose?: () => void }) {
  const { t } = useTranslation();
  const me = useMe();
  const detail = useQuery(episodeQuery(id));
  const act = useEpisodeAction();
  const [pending, setPending] = useState<Pending>(null);
  const [problem, setProblem] = useState<Problem | null>(null);

  if (detail.isPending) return <div className="p-4 text-sm text-ink-2">Loading…</div>;
  if (detail.isError) {
    const p = detail.error as unknown as Problem;
    return (
      <div className="p-4">
        <ProblemBanner problem={p.status ? p : { title: 'Not found, or not visible to you', status: 404 }} />
      </div>
    );
  }
  const d: EpisodeDetail = detail.data;
  const item = d.item;
  const allowed = (perm: string) => can(me.data, perm, item.accessScope);
  const primary = primaryAction(item, me.data, allowed);
  const isOpen = item.handlingState !== 'closed';

  const run = (action: EpisodeAction) => {
    setProblem(null);
    act.mutate(
      { id, version: item.version, action },
      {
        onError: (p) => {
          setProblem(p);
        },
        onSuccess: () => setPending(null),
      },
    );
  };

  const request = (action: EpisodeAction) => {
    switch (action.kind) {
      case 'close':
        setPending({ action, title: `Close “${item.summary ?? item.resourceName ?? 'episode'}”`, withReason: true, reasonRequired: true });
        break;
      case 'note':
        setPending({ action, title: 'Add a note', withReason: true, reasonRequired: true });
        break;
      case 'restore':
        setPending({ action, title: 'Restore for review', withReason: true, reasonRequired: false });
        break;
      case 'silence':
        setPending({ action, title: 'Silence for 1 hour', withReason: true, reasonRequired: true });
        break;
      case 'ack':
        if (action.force) setPending({ action, title: `Take over from ${item.assignee?.displayName ?? 'the current assignee'}`, withReason: false, reasonRequired: false });
        else run(action);
        break;
      default:
        run(action);
    }
  };

  const confirmPending = (reason: string) => {
    if (!pending) return;
    const a = pending.action;
    if (a.kind === 'close') run({ kind: 'close', reason });
    else if (a.kind === 'note') run({ kind: 'note', text: reason });
    else if (a.kind === 'restore') run({ kind: 'restore', reason: reason || undefined });
    else if (a.kind === 'silence') run({ kind: 'silence', until: new Date(Date.now() + 3_600_000).toISOString(), reason });
    else run(a);
  };

  const conflict = problem && problemCode(problem) === 'version-conflict';

  return (
    <article className="flex h-full min-h-0 flex-col" data-episode-id={id} data-version={item.version} aria-labelledby={`ep-${id}-title`}>
      <header className="border-b border-line px-4 py-3" style={{ borderLeft: `4px solid var(--sev-${item.severity in { critical: 1, high: 1, medium: 1, low: 1, info: 1 } ? item.severity : 'high'})` }}>
        <div className="flex items-start gap-2">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <SeverityBadge severity={item.severity} />
              <ConditionBadge state={item.conditionState} />
              <HandlingBadge state={item.handlingState} />
              <CoverageBadge state={item.coverageState} />
              {item.suppressedUntil && <span className="badge underline decoration-dotted">Silenced</span>}
              {item.deliveryFailure && <span className="badge" style={{ color: 'var(--sev-high)' }}>Delivery failure</span>}
            </div>
            <h2 id={`ep-${id}-title`} className="mt-1 truncate text-md" title={item.summary ?? undefined}>
              {item.summary ?? '(no summary)'}
            </h2>
            <div className="mt-0.5 text-sm text-ink-2">
              <span className="mono">{item.resourceName ?? '—'}</span>
              {item.service && <> · {item.service}</>}
              {item.environment && <> · {item.environment}</>}
              {' · '}
              {item.integrationName}
            </div>
            <div className="mt-1 text-sm text-ink-2">
              Owner: {item.owningTeam?.name ?? 'unassigned team'}
              {item.assignee ? ` · ${item.assignee.displayName}` : ' · nobody assigned'}
              {item.ackDeadlineAt && isOpen && item.handlingState === 'new' && (
                <>
                  {' · '}
                  <span style={item.ackOverdue ? { color: 'var(--sev-critical)' } : undefined}>
                    ack due <RelativeTime iso={item.ackDeadlineAt} mode="sentence" />
                  </span>
                </>
              )}
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-1">
            {primary && (
              <button type="button" className="btn btn-primary" onClick={() => request(primary)} disabled={act.isPending} data-primary-action={primary.kind}>
                {primary.kind === 'ack' && primary.force ? t('actions.takeOver') : VERB[primary.kind]}
              </button>
            )}
            <DropdownMenu.Root>
              <DropdownMenu.Trigger asChild>
                <button type="button" className="btn" aria-label="More actions">
                  ⋯
                </button>
              </DropdownMenu.Trigger>
              <DropdownMenu.Portal>
                <DropdownMenu.Content align="end" className="min-w-44 rounded-md border border-line bg-surface p-1 text-sm" style={{ boxShadow: 'var(--shadow-menu)' }}>
                  {isOpen && allowed(P.episodeAssign) && me.data && item.assignee?.id !== me.data.id && (
                    <MenuItem onSelect={() => request({ kind: 'assignToMe', userId: me.data.id })}>{t('actions.assignToMe')}</MenuItem>
                  )}
                  {allowed(P.episodeNote) && <MenuItem onSelect={() => request({ kind: 'note', text: '' })}>{t('actions.note')}</MenuItem>}
                  {isOpen && allowed(P.episodeSilence) && <MenuItem onSelect={() => request({ kind: 'silence', until: '', reason: '' })}>{t('actions.silence')}</MenuItem>}
                  {isOpen && allowed(P.episodeClose) && <MenuItem onSelect={() => request({ kind: 'close', reason: '' })}>{t('actions.close')}</MenuItem>}
                  {!isOpen && allowed(P.episodeRestore) && <MenuItem onSelect={() => request({ kind: 'restore' })}>{t('actions.restore')}</MenuItem>}
                  {!inDrawer && (
                    <DropdownMenu.Item asChild>
                      <Link to="/queue" search={{ view: 'needsAttention' }} className="block rounded-sm px-2 py-1 text-ink hover:bg-surface-2 hover:no-underline">
                        {t('detail.back')}
                      </Link>
                    </DropdownMenu.Item>
                  )}
                </DropdownMenu.Content>
              </DropdownMenu.Portal>
            </DropdownMenu.Root>
            {inDrawer && onClose && (
              <button type="button" className="btn" onClick={onClose} aria-label="Close drawer">
                ×
              </button>
            )}
          </div>
        </div>
        {problem && (
          <div className="mt-2" aria-live="polite">
            <ProblemBanner problem={problem} tone={conflict ? 'info' : 'error'} onDismiss={() => setProblem(null)} />
          </div>
        )}
      </header>

      <Tabs.Root defaultValue="overview" className="flex min-h-0 flex-1 flex-col">
        <Tabs.List className="flex gap-4 border-b border-line px-4" aria-label="Episode sections">
          <Tab value="overview">{t('detail.overview')}</Tab>
          <Tab value="timeline">{t('detail.timeline')}</Tab>
        </Tabs.List>
        <Tabs.Content value="overview" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          <Overview d={d} />
        </Tabs.Content>
        <Tabs.Content value="timeline" className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          <Timeline id={id} />
        </Tabs.Content>
      </Tabs.Root>

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => {
          if (!open) setPending(null);
        }}
        title={pending?.title ?? ''}
        verb={pending ? VERB[pending.action.kind] : ''}
        withReason={pending?.withReason}
        reasonRequired={pending?.reasonRequired}
        reasonLabel={pending?.action.kind === 'note' ? 'Note' : undefined}
        busy={act.isPending}
        onConfirm={confirmPending}
      />
    </article>
  );
}

function Tab({ value, children }: { value: string; children: string }) {
  return (
    <Tabs.Trigger value={value} className="h-9 border-b-2 border-transparent text-sm text-ink-2 data-[state=active]:border-accent data-[state=active]:text-ink">
      {children}
    </Tabs.Trigger>
  );
}

function MenuItem({ children, onSelect }: { children: string; onSelect: () => void }) {
  return (
    <DropdownMenu.Item onSelect={onSelect} className="cursor-default rounded-sm px-2 py-1 outline-none hover:bg-surface-2 data-[highlighted]:bg-surface-2">
      {children}
    </DropdownMenu.Item>
  );
}

function Overview({ d }: { d: EpisodeDetail }) {
  const { t } = useTranslation();
  const item = d.item;
  return (
    <div className="grid gap-4 text-sm">
      <ExplanationPanel explanation={d.explanation} why={d.routing.why} />

      <Section title={t('detail.identity')}>
        <table className="w-full border-collapse text-sm">
          <tbody>
            {d.identityComponents.map((c) => (
              <tr key={c.name} className="border-t border-line">
                <td className="py-1 pr-3 text-ink-2">{c.name}</td>
                <td className="py-1 mono break-all">{c.value}</td>
              </tr>
            ))}
            <tr className="border-t border-line">
              <td className="py-1 pr-3 text-ink-2">fingerprint</td>
              <td className="py-1 mono break-all text-xs">{d.fingerprint}</td>
            </tr>
            {d.sourceAlertId && (
              <tr className="border-t border-line">
                <td className="py-1 pr-3 text-ink-2">source alert id</td>
                <td className="py-1 mono break-all">{d.sourceAlertId}</td>
              </tr>
            )}
          </tbody>
        </table>
        <p className="m-0 mt-1 text-xs text-ink-2">
          Seen {item.occurrenceCount}
          {item.occurrenceCountExact ? '' : '+'} time{item.occurrenceCount === 1 ? '' : 's'} since {utc(item.firstSeen)} · identity confidence {d.identityConfidence ?? 'exact'}
        </p>
      </Section>

      <Section title={t('detail.lifecycle')}>
        <dl className="m-0 grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
          <dt className="text-ink-2">profile</dt>
          <dd className="m-0">{d.lifecycle.profile ?? 'none'}{d.lifecycle.policyVersion ? ` (policy v${d.lifecycle.policyVersion})` : ''}</dd>
          <dt className="text-ink-2">auto-close</dt>
          <dd className="m-0">
            {d.lifecycle.autoResolveAt ? (
              <>
                <RelativeTime iso={d.lifecycle.autoResolveAt} mode="sentence" />
                {d.lifecycle.suspended && <span className="badge ml-2 border-dashed">suspended</span>}
              </>
            ) : (
              'not scheduled'
            )}
          </dd>
          <dt className="text-ink-2">{t('detail.timers')}</dt>
          <dd className="m-0">
            {d.timers.length === 0 ? (
              t('detail.noTimers')
            ) : (
              <ul className="m-0 list-none p-0">
                {d.timers.map((timer) => (
                  <li key={`${timer.kind}-${timer.at}`}>
                    {timer.kind} · <RelativeTime iso={timer.at} mode="sentence" /> · {timer.status}
                  </li>
                ))}
              </ul>
            )}
          </dd>
        </dl>
      </Section>

      <Section title={t('detail.routing')}>
        <p className="m-0">
          {d.routing.ruleName ? `Rule “${d.routing.ruleName}”` : 'No rule matched'} → {item.owningTeam?.name ?? 'triage'}
          {item.routingCorrectionRequired && (
            <span className="badge ml-2" style={{ color: 'var(--sev-high)', borderColor: 'var(--sev-high)' }}>
              routing correction required
            </span>
          )}
        </p>
      </Section>

      {(d.sourceUrl ?? d.runbookUrl) && (
        <Section title={t('detail.links')}>
          <ul className="m-0 list-none p-0">
            {d.sourceUrl && (
              <li>
                <a href={d.sourceUrl} rel="noreferrer noopener" target="_blank">
                  {t('detail.source')}
                </a>
              </li>
            )}
            {d.runbookUrl && (
              <li>
                <a href={d.runbookUrl} rel="noreferrer noopener" target="_blank">
                  {t('detail.runbook')}
                </a>
              </li>
            )}
          </ul>
        </Section>
      )}

      {d.closure && (
        <Section title={t('detail.closure')}>
          <div className="flex flex-wrap items-center gap-2">
            <span>Closed as {d.closure.reason}</span>
            <EvidenceBadge evidence={d.closure.evidence} />
            {d.closure.at && <RelativeTime iso={d.closure.at} mode="sentence" className="text-ink-2" />}
            {d.closure.by && <span className="text-ink-2">by {d.closure.by.displayName}</span>}
          </div>
          {d.closure.note && <p className="m-0 mt-1 whitespace-pre-line">{d.closure.note}</p>}
          {d.closure.restoredFromReason && <p className="m-0 mt-1 text-ink-2">Previously restored from {d.closure.restoredFromReason}.</p>}
        </Section>
      )}

      <Section title={t('detail.coverage')}>
        <CoverageBadge state={item.coverageState} always />
        <span className="ml-2 text-ink-2">integration {item.integrationName}</span>
      </Section>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section>
      <h3 className="mb-1 text-xs text-ink-2">{title}</h3>
      {children}
    </section>
  );
}

const KINDS = ['', 'source_event', 'late_event', 'ack', 'assign', 'note', 'close', 'restore', 'notify', 'escalate', 'suppress', 'auto_resolve'];

function Timeline({ id }: { id: string }) {
  const [kind, setKind] = useState('');
  const timeline = useQuery(timelineQuery(id, kind || undefined));
  return (
    <div className="text-sm">
      <label className="mb-2 flex items-center gap-2 text-xs text-ink-2">
        Show
        <select className="h-6 rounded-sm border border-line bg-surface px-1 text-xs" value={kind} onChange={(e) => setKind(e.target.value)}>
          {KINDS.map((k) => (
            <option key={k} value={k}>
              {k === '' ? 'everything' : k.replace('_', ' ')}
            </option>
          ))}
        </select>
      </label>
      {timeline.isPending && <div className="text-ink-2">Loading…</div>}
      {timeline.data?.items.length === 0 && <div className="text-ink-2">Nothing recorded yet.</div>}
      <ol className="m-0 list-none p-0">
        {timeline.data?.items.map((entry) => <TimelineRow key={entry.id} entry={entry} />)}
      </ol>
    </div>
  );
}

function TimelineRow({ entry }: { entry: TimelineEntry }) {
  const deEmphasised = entry.kind === 'late_event' || entry.kind === 'replayed';
  const tooltip = deEmphasised ? 'Arrived late or replayed: recorded for the record, it did not change the episode state.' : undefined;
  return (
    <li className={`grid grid-cols-[max-content_max-content_1fr] gap-x-3 border-t border-line py-1.5 ${deEmphasised ? 'text-ink-2' : ''}`} title={tooltip} data-kind={entry.kind}>
      <time dateTime={entry.at} className="text-xs text-ink-2" title={`${utc(entry.at)} · local ${local(entry.at)}`}>
        {utc(entry.at).slice(5, 19)}
      </time>
      <span className={`text-xs ${deEmphasised ? 'italic' : ''}`}>{entry.kind.replace('_', ' ')}</span>
      <span className="min-w-0 break-words">
        {entry.actor && <span>{entry.actor.displayName} · </span>}
        {entry.destinationName && <span className="text-ink-2">{entry.destinationName} · </span>}
        {entry.deliveryStatus && <span className="badge mr-1">{entry.deliveryStatus}</span>}
        <span className="mono text-xs">{entry.detail}</span>
      </span>
    </li>
  );
}

export function EpisodePage({ id }: { id: string }) {
  return (
    <div className="h-full overflow-hidden bg-surface">
      <EpisodeView id={id} />
    </div>
  );
}
