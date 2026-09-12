import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type HubHealth = Schemas['HubHealth'];
export type HubFailure = Schemas['HubFailure'];
export type RetentionStatus = Schemas['RetentionStatusDto'];
export type RetentionReport = Schemas['RetentionReportDto'];

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

async function unwrapEmpty(promise: Promise<{ error?: unknown; response: Response }>): Promise<void> {
  const { error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (!response.ok) throw asProblem(error, response.status);
}

export const hubHealthQuery = () =>
  queryOptions({
    queryKey: ['hub', 'health'],
    queryFn: () => unwrap(api.GET('/api/v1/hub/health')),
    refetchInterval: 15_000,
    staleTime: 5_000,
  });

export const hubFailuresQuery = (limit = 100) =>
  queryOptions({
    queryKey: ['hub', 'failures', limit],
    queryFn: () => unwrap(api.GET('/api/v1/hub/failures', { params: { query: { limit } } })),
    refetchInterval: 15_000,
    staleTime: 5_000,
  });

export const retentionStatusQuery = () =>
  queryOptions({
    queryKey: ['hub', 'retention'],
    queryFn: () => unwrap(api.GET('/api/v1/hub/retention')),
    staleTime: 30_000,
  });

export function useRetryFailure() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrapEmpty(api.POST('/api/v1/hub/failures/{id}/retry', { params: { path: { id } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['hub'] }),
  });
}

export function useRunRetention() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/hub/retention/run')),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['hub'] });
      void qc.invalidateQueries({ queryKey: ['audit'] });
    },
  });
}

/** Age of an instant in whole seconds, clamped at zero (for "oldest pending" ages). */
export function ageSeconds(iso: string | null | undefined, now: number): number | null {
  if (!iso) return null;
  return Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
}
