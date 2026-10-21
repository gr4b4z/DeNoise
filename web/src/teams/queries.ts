import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type TeamSummary = Schemas['TeamSummary'];
export type CreateTeamRequest = Schemas['CreateTeamRequest'];

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

export const teamsListQuery = () =>
  queryOptions({
    queryKey: ['teams', 'list'],
    queryFn: () => unwrap(api.GET('/api/v1/teams')),
    staleTime: 10_000,
  });

export function useCreateTeam() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateTeamRequest) => unwrap(api.POST('/api/v1/teams', { body })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['teams'] });
      void qc.invalidateQueries({ queryKey: ['episodes'] }); // the rail and the team pickers read teams through the episode queries
    },
  });
}

/** Access scopes are free text here (19.2: the vocabulary is server-side); split on comma or whitespace, de-duplicate, keep order. */
export function parseScopes(input: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of input.split(/[,\s]+/)) {
    const scope = raw.trim();
    if (scope.length === 0 || seen.has(scope)) continue;
    seen.add(scope);
    out.push(scope);
  }
  return out;
}

/** A team needs a name and at least one access scope; everything else the server defaults. */
export function validateTeam(name: string, scopes: string[]): 'name' | 'scopes' | null {
  if (name.trim().length === 0) return 'name';
  if (scopes.length === 0) return 'scopes';
  return null;
}
