import type { QueryClient } from '@tanstack/react-query';
import type { EpisodeListItem, PagedEpisodes } from '@/api/types';

/** `data:` of an `episode.changed` event (Application/Realtime/ChangeEvent.ForEpisode). */
export interface EpisodeChanged {
  id: string;
  version: number;
  handling: string;
  condition: string;
  severity: string;
  teamId: string | null;
  closed: boolean;
}

export type PatchOutcome = 'patched' | 'ignored' | 'unknown';

export function parseEpisodeChanged(json: string): EpisodeChanged | null {
  try {
    const value = JSON.parse(json) as Partial<EpisodeChanged>;
    if (typeof value.id !== 'string' || typeof value.version !== 'number') return null;
    return {
      id: value.id,
      version: value.version,
      handling: value.handling ?? 'new',
      condition: value.condition ?? 'unknown',
      severity: value.severity ?? 'unknown',
      teamId: value.teamId ?? null,
      closed: value.closed ?? false,
    };
  } catch {
    return null;
  }
}

/**
 * 08 §5: patch every list cache in place for the row (ignored when the cache already has an equal or newer version),
 * then invalidate the detail and the counts. Returns what happened so the caller can decide whether to pulse the row.
 */
export function applyEpisodeChanged(qc: QueryClient, change: EpisodeChanged): PatchOutcome {
  let outcome: PatchOutcome = 'unknown';
  qc.setQueriesData<PagedEpisodes>({ queryKey: ['episodes'] }, (old) => {
    if (!old) return old;
    const index = old.items.findIndex((i) => i.id === change.id);
    if (index < 0) return old;
    const current = old.items[index];
    if (!current || current.version >= change.version) {
      if (outcome === 'unknown') outcome = 'ignored';
      return old;
    }
    outcome = 'patched';
    const patched: EpisodeListItem = {
      ...current,
      version: change.version,
      handlingState: change.handling,
      conditionState: change.condition,
      severity: change.severity,
      owningTeam: change.teamId && current.owningTeam?.id === change.teamId ? current.owningTeam : change.teamId ? { id: change.teamId, name: current.owningTeam?.name ?? '' } : null,
    };
    const items = old.items.slice();
    items[index] = patched;
    return { ...old, items };
  });
  void qc.invalidateQueries({ queryKey: ['episode', change.id] });
  void qc.invalidateQueries({ queryKey: ['episodes'], refetchType: 'active' });
  void qc.invalidateQueries({ queryKey: ['episode-counts'] });
  return outcome;
}
