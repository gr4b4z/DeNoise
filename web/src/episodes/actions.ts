import { useMutation, useQueryClient } from '@tanstack/react-query';
import { actionHeaders, api, withIdempotentRetry } from '@/api/client';
import type { EpisodeDetail, Problem } from '@/api/types';
import { asProblem } from '@/lib/problems';

export type EpisodeAction =
  | { kind: 'ack'; force?: boolean }
  | { kind: 'assignToMe'; userId: string }
  | { kind: 'assign'; userId?: string | null; teamId?: string | null }
  | { kind: 'note'; text: string }
  | { kind: 'close'; reason: string }
  | { kind: 'restore'; reason?: string }
  | { kind: 'silence'; until: string; reason: string };

export interface ActionInput {
  id: string;
  version: number;
  action: EpisodeAction;
}

/** Every mutation carries If-Match and a fresh Idempotency-Key; 409 surfaces the server's `current` so the UI refreshes, never overwrites. */
async function perform({ id, version, action }: ActionInput): Promise<EpisodeDetail> {
  const key = crypto.randomUUID();
  const params = { path: { id } };
  const headers = actionHeaders(version, key);
  const result = await withIdempotentRetry(() => {
    switch (action.kind) {
      case 'ack':
        return api.POST('/api/v1/episodes/{id}/ack', { params, headers, body: { force: action.force ?? false } });
      case 'assignToMe':
        return api.POST('/api/v1/episodes/{id}/assign', { params, headers, body: { userId: action.userId, teamId: null } });
      case 'assign':
        return api.POST('/api/v1/episodes/{id}/assign', { params, headers, body: { userId: action.userId ?? null, teamId: action.teamId ?? null } });
      case 'note':
        return api.POST('/api/v1/episodes/{id}/note', { params, headers, body: { text: action.text } });
      case 'close':
        return api.POST('/api/v1/episodes/{id}/close', { params, headers, body: { reason: action.reason } });
      case 'restore':
        return api.POST('/api/v1/episodes/{id}/restore', { params, headers, body: { reason: action.reason ?? null } });
      case 'silence':
        return api.POST('/api/v1/episodes/{id}/silence', { params, headers, body: { until: action.until, reason: action.reason } });
    }
  });
  const { data, error, response } = result;
  if (!data) throw asProblem(error, response.status);
  return data;
}

export function useEpisodeAction() {
  const qc = useQueryClient();
  return useMutation<EpisodeDetail, Problem, ActionInput>({
    mutationFn: perform,
    onSuccess: (detail) => {
      qc.setQueryData(['episode', detail.item.id], detail);
      void qc.invalidateQueries({ queryKey: ['episodes'] });
      void qc.invalidateQueries({ queryKey: ['episode-counts'] });
      void qc.invalidateQueries({ queryKey: ['episode', detail.item.id, 'timeline'] });
    },
    onError: (problem, input) => {
      if (problem.status === 409) {
        if (problem.current) qc.setQueryData(['episode', input.id], problem.current);
        void qc.invalidateQueries({ queryKey: ['episode', input.id] });
        void qc.invalidateQueries({ queryKey: ['episodes'] });
      }
    },
  });
}

/** 08 §1: exactly one primary action per row/header, from handling state and permissions. */
export function primaryAction(item: { handlingState: string; assignee: { id: string } | null }, me: { id: string } | undefined, allowed: (p: string) => boolean): EpisodeAction | null {
  if (item.handlingState === 'new' && allowed('episode.ack')) return { kind: 'ack' };
  if (item.handlingState === 'acknowledged') {
    if (me && item.assignee?.id !== me.id && allowed('episode.assign')) return item.assignee ? { kind: 'ack', force: true } : { kind: 'assignToMe', userId: me.id };
    if (allowed('episode.close')) return { kind: 'close', reason: '' };
  }
  if (item.handlingState === 'closed' && allowed('episode.restore')) return { kind: 'restore' };
  return null;
}
