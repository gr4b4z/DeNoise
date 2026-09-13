import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { EpisodeDetail, PagedEpisodes, PagedTimeline, QueueView } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export interface QueueFilters {
  view: QueueView;
  severity?: string[];
  team?: string;
  environment?: string[];
  service?: string;
  integration?: string;
  q?: string;
  sort?: string;
}

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

export const episodesQuery = (filters: QueueFilters, limit = 100) =>
  infiniteQueryOptions({
    queryKey: ['episodes', filters, limit],
    queryFn: ({ pageParam }): Promise<PagedEpisodes> =>
      unwrap(
        api.GET('/api/v1/episodes', {
          params: {
            query: {
              view: filters.view,
              severity: filters.severity?.length ? filters.severity : undefined,
              team: filters.team || undefined,
              environment: filters.environment?.length ? filters.environment : undefined,
              service: filters.service || undefined,
              integration: filters.integration || undefined,
              q: filters.q || undefined,
              sort: filters.sort || undefined,
              limit,
              cursor: pageParam || undefined,
              includeTotal: !pageParam,
            },
          },
        }),
      ),
    initialPageParam: '',
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: 10_000,
  });

export const countsQuery = () =>
  queryOptions({
    queryKey: ['episode-counts'],
    queryFn: (): Promise<Record<string, number>> => unwrap(api.GET('/api/v1/episodes/counts')),
    staleTime: 10_000,
  });

export const episodeQuery = (id: string) =>
  queryOptions({
    queryKey: ['episode', id],
    queryFn: (): Promise<EpisodeDetail> => unwrap(api.GET('/api/v1/episodes/{id}', { params: { path: { id } } })),
    staleTime: 5_000,
  });

export const timelineQuery = (id: string, kind?: string) =>
  queryOptions({
    queryKey: ['episode', id, 'timeline', kind ?? ''],
    queryFn: (): Promise<PagedTimeline> => unwrap(api.GET('/api/v1/episodes/{id}/timeline', { params: { path: { id }, query: { limit: 200, kind: kind || undefined } } })),
    staleTime: 5_000,
  });

export const teamsQuery = () =>
  queryOptions({
    queryKey: ['teams'],
    queryFn: () => unwrap(api.GET('/api/v1/teams')),
    staleTime: 60_000,
  });
