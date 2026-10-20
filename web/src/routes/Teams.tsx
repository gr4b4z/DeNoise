import { useQuery } from '@tanstack/react-query';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from '@tanstack/react-router';
import type { Problem } from '@/api/types';
import { useMe } from '@/auth/queries';
import { can, P } from '@/auth/permissions';
import { EmptyState } from '@/components/EmptyState';
import { ProblemBanner } from '@/components/ProblemBanner';
import { parseScopes, teamsListQuery, useCreateTeam, validateTeam, type TeamSummary } from '@/teams/queries';

/**
 * Team administration (06 §3 "Teams"). The list is readable by anyone who can see episodes; creating a team needs
 * `team.manage`. Membership is managed from the team's own page — this screen creates the team and shows what exists.
 */
export function TeamsPage() {
  const { t } = useTranslation();
  const me = useMe();
  const list = useQuery(teamsListQuery());
  const [creating, setCreating] = useState(false);
  const canManage = can(me.data, P.teamManage);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h1 className="mr-2 text-md">{t('teamsAdmin.title')}</h1>
        {canManage && (
          <button type="button" className="btn btn-primary ml-auto" onClick={() => setCreating(true)} data-testid="team-new">
            {t('teamsAdmin.new')}
          </button>
        )}
      </div>
      {list.isError && (
        <div className="px-3 pt-2">
          <ProblemBanner problem={list.error as unknown as Problem} />
        </div>
      )}
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_auto]">
        {list.data?.length === 0 ? (
          <EmptyState variant="healthy" title={t('teamsAdmin.empty')} detail={t('teamsAdmin.emptyDetail')} />
        ) : (
          <div className="min-h-0 overflow-auto bg-surface">
            <table className="w-full border-collapse text-sm">
              <thead className="sticky top-0 bg-surface text-left text-xs text-ink-2">
                <tr className="h-8 border-b border-line">
                  <th className="pl-3 font-normal">{t('teamsAdmin.columns.name')}</th>
                  <th className="font-normal">{t('teamsAdmin.columns.scopes')}</th>
                  <th className="font-normal">{t('teamsAdmin.columns.members')}</th>
                  <th className="font-normal">{t('teamsAdmin.columns.role')}</th>
                  <th className="pr-3" />
                </tr>
              </thead>
              <tbody>
                {list.data?.map((team: TeamSummary) => (
                  <tr key={team.id} className="border-b border-line hover:bg-surface-2" data-team-id={team.id}>
                    <td className="py-2 pl-3">
                      <Link to="/teams/$id" params={{ id: team.id }}>
                        {team.name}
                      </Link>
                    </td>
                    <td className="py-2">
                      {team.accessScopes.length === 0 ? (
                        <span className="text-ink-2">—</span>
                      ) : (
                        team.accessScopes.map((s) => (
                          <span key={s} className="badge mr-1">
                            {s}
                          </span>
                        ))
                      )}
                    </td>
                    <td className="py-2 text-ink-2">{t('teams.members', { count: team.memberCount })}</td>
                    <td className="py-2">{team.isTriage ? <span className="badge font-semibold">{t('teams.triage')}</span> : <span className="text-ink-2">—</span>}</td>
                    <td className="py-2 pr-3 text-right text-xs text-ink-2">v{team.version}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {creating && <CreateTeamForm onDone={() => setCreating(false)} />}
      </div>
    </div>
  );
}

function CreateTeamForm({ onDone }: { onDone: () => void }) {
  const { t } = useTranslation();
  const create = useCreateTeam();
  const existing = useQuery(teamsListQuery());
  const [name, setName] = useState('');
  const [scopeText, setScopeText] = useState('');
  const [isTriage, setIsTriage] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [invalid, setInvalid] = useState<'name' | 'scopes' | null>(null);
  const scopes = parseScopes(scopeText);
  const currentTriage = existing.data?.find((team) => team.isTriage);

  const submit = (e: SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setProblem(null);
    const bad = validateTeam(name, scopes);
    setInvalid(bad);
    if (bad) return;
    create.mutate(
      { name: name.trim(), accessScopes: scopes, isTriage },
      { onSuccess: onDone, onError: (err) => setProblem(err as unknown as Problem) },
    );
  };

  return (
    <form onSubmit={submit} className="w-[min(420px,100vw)] overflow-y-auto border-l border-line bg-surface p-4 text-sm" aria-label={t('teamsAdmin.new')}>
      <h2 className="m-0 text-base">{t('teamsAdmin.new')}</h2>
      {problem && (
        <div className="mt-3">
          <ProblemBanner problem={problem} onDismiss={() => setProblem(null)} />
        </div>
      )}
      <label className="mt-3 block">
        <span className="block text-xs text-ink-2">{t('teamsAdmin.form.name')}</span>
        <input
          className="input mt-1 w-full"
          value={name}
          onChange={(e) => setName(e.target.value)}
          aria-invalid={invalid === 'name'}
          data-testid="team-name"
          autoFocus
        />
      </label>
      <label className="mt-3 block">
        <span className="block text-xs text-ink-2">{t('teamsAdmin.form.scopes')}</span>
        <input
          className="input mt-1 w-full"
          value={scopeText}
          onChange={(e) => setScopeText(e.target.value)}
          aria-invalid={invalid === 'scopes'}
          placeholder={t('teamsAdmin.form.scopesPlaceholder')}
          data-testid="team-scopes"
        />
        <span className="mt-1 block text-xs text-ink-2">{t('teamsAdmin.form.scopesHelp')}</span>
        {scopes.length > 0 && (
          <span className="mt-1 block" data-testid="team-scopes-preview">
            {scopes.map((s) => (
              <span key={s} className="badge mr-1">
                {s}
              </span>
            ))}
          </span>
        )}
      </label>
      <label className="mt-3 flex items-start gap-2">
        <input type="checkbox" className="mt-1" checked={isTriage} onChange={(e) => setIsTriage(e.target.checked)} data-testid="team-triage" />
        <span>
          <span className="block">{t('teamsAdmin.form.triage')}</span>
          <span className="block text-xs text-ink-2">{t('teamsAdmin.form.triageHelp')}</span>
          {isTriage && currentTriage && <span className="mt-1 block text-xs text-ink-2">{t('teamsAdmin.form.triageReplaces', { name: currentTriage.name })}</span>}
        </span>
      </label>
      <p className="mt-3 text-xs text-ink-2">{t('teamsAdmin.form.afterCreate')}</p>
      <div className="mt-4 flex gap-2">
        <button type="submit" className="btn btn-primary" disabled={create.isPending} data-testid="team-save">
          {create.isPending ? t('teamsAdmin.form.creating') : t('teamsAdmin.form.create')}
        </button>
        <button type="button" className="btn" onClick={onDone}>
          {t('actions.cancel')}
        </button>
      </div>
    </form>
  );
}
