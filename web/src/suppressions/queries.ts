import { infiniteQueryOptions, queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { PagedEpisodes, Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type SuppressionDto = Schemas['SuppressionDto'];
export type CreateSuppressionRequest = Schemas['CreateSuppressionRequest'];
export type SuppressionEnded = Schemas['SuppressionEndedResponse'];
export type TeamOverview = Schemas['TeamOverview'];
export type SavedFilter = Schemas['SavedFilterDto'];
export type RelatedEpisodes = Schemas['RelatedEpisodes'];

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

async function unwrapAllowEmpty<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T | null> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (!response.ok) throw asProblem(error, response.status);
  return data ?? null;
}

// ---- suppressions ---------------------------------------------------------------------------------------------------

export const suppressionsQuery = (all: boolean) =>
  queryOptions({
    queryKey: ['suppression', 'list', all],
    queryFn: () => unwrap(api.GET('/api/v1/suppressions', { params: { query: { all: all || undefined } } })),
    staleTime: 10_000,
  });

export function useCreateSuppression() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateSuppressionRequest) => unwrap(api.POST('/api/v1/suppressions', { body })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['suppression'] });
      void qc.invalidateQueries({ queryKey: ['episodes'] });
    },
  });
}

export function useCancelSuppression() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrapAllowEmpty(api.DELETE('/api/v1/suppressions/{id}', { params: { path: { id } } })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['suppression'] });
      void qc.invalidateQueries({ queryKey: ['episodes'] });
    },
  });
}

export function usePreviewScope() {
  return useMutation({
    mutationFn: (scope: unknown) => unwrap(api.POST('/api/v1/suppressions/preview-scope', { body: scope as Record<string, never> })) as Promise<{ text: string }>,
  });
}

/** Scope builder rows → 07 §5 predicate (`all` of `eq` / `in` / `regex`); an empty builder means "everything" (`{ all: [] }`). */
export interface ScopeRow {
  field: string;
  op: 'eq' | 'in' | 'regex' | 'neq';
  value: string;
}

export const SCOPE_FIELDS = ['access_scope', 'service', 'environment', 'integration.id', 'integration.type', 'owning_team_id', 'severity', 'resource_name', 'rule_name', 'summary'] as const;

export function rowsToPredicate(rows: ScopeRow[]): Record<string, unknown> {
  const items = rows
    .filter((r) => r.field && r.value.trim())
    .map((r) => {
      switch (r.op) {
        case 'in':
          return { in: [r.field, r.value.split(',').map((v) => v.trim()).filter(Boolean)] };
        case 'regex':
          return { regex: [r.field, r.value.trim()] };
        case 'neq':
          return { neq: [r.field, r.value.trim()] };
        default:
          return { eq: [r.field, r.value.trim()] };
      }
    });
  const single = items.length === 1 ? items[0] : undefined;
  return single ?? { all: items };
}

/** `YYYY-MM-DDTHH:MM` from a datetime-local input → the wall-clock string the API accepts (no offset). */
export function localInputToWallClock(value: string): string | null {
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value) ? `${value.slice(0, 16)}:00` : null;
}

export function browserZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

// ---- team overview --------------------------------------------------------------------------------------------------

export const teamOverviewQuery = (id: string) =>
  queryOptions({
    queryKey: ['episodes', 'team-overview', id],
    queryFn: () => unwrap(api.GET('/api/v1/teams/{id}/overview', { params: { path: { id } } })),
    staleTime: 10_000,
  });

// ---- history --------------------------------------------------------------------------------------------------------

export interface HistoryFilters {
  severity?: string[];
  team?: string;
  q?: string;
  closureReason?: string;
  evidence?: string;
}

export const historyQuery = (filters: HistoryFilters, limit = 100) =>
  infiniteQueryOptions({
    queryKey: ['episodes', 'history', filters, limit],
    queryFn: ({ pageParam }): Promise<PagedEpisodes> =>
      unwrap(
        api.GET('/api/v1/history', {
          params: {
            query: {
              severity: filters.severity?.length ? filters.severity : undefined,
              team: filters.team || undefined,
              q: filters.q || undefined,
              closureReason: filters.closureReason || undefined,
              evidence: filters.evidence || undefined,
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

export const CLOSURE_REASONS = ['source_resolved', 'source_cancelled', 'verified_resolved', 'inactivity_timeout', 'manual_close', 'administrative_expiry', 'informational_expiry'] as const;
export const EVIDENCE = ['source', 'api_verification', 'heartbeat_and_inactivity', 'inactivity_unverified', 'none'] as const;

// ---- saved filters --------------------------------------------------------------------------------------------------

export const savedFiltersQuery = () =>
  queryOptions({
    queryKey: ['saved-filters'],
    queryFn: () => unwrap(api.GET('/api/v1/me/filters')),
    staleTime: 60_000,
  });

export function useSaveFilter() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { name: string; query: string }) => unwrap(api.POST('/api/v1/me/filters', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['saved-filters'] }),
  });
}

export function useDeleteFilter() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrapAllowEmpty(api.DELETE('/api/v1/me/filters/{id}', { params: { path: { id } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['saved-filters'] }),
  });
}

// ---- related --------------------------------------------------------------------------------------------------------

export const relatedQuery = (id: string) =>
  queryOptions({
    queryKey: ['episode', id, 'related'],
    queryFn: () => unwrap(api.GET('/api/v1/episodes/{id}/related', { params: { path: { id } } })),
    staleTime: 10_000,
  });
