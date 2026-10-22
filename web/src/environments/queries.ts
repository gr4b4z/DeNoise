import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type EnvironmentDto = Schemas['EnvironmentDto'];
export type CreateEnvironmentRequest = Schemas['CreateEnvironmentRequest'];
export type UpdateEnvironmentRequest = Schemas['UpdateEnvironmentRequest'];

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

/** The environment dictionary (ADR-17). Changes rarely, so it is cached for minutes rather than refetched per screen. */
export const environmentsQuery = () =>
  queryOptions({
    queryKey: ['environments'],
    queryFn: () => unwrap(api.GET('/api/v1/environments')),
    staleTime: 5 * 60_000,
  });

export function useEnvironments() {
  return useQuery(environmentsQuery());
}

function useInvalidate() {
  const qc = useQueryClient();
  return () => void qc.invalidateQueries({ queryKey: ['environments'] });
}

export function useCreateEnvironment() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (body: CreateEnvironmentRequest) => unwrap(api.POST('/api/v1/environments', { body })),
    onSuccess: invalidate,
  });
}

export function useUpdateEnvironment() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: ({ key, version, body }: { key: string; version: number; body: UpdateEnvironmentRequest }) =>
      unwrap(api.PUT('/api/v1/environments/{key}', { params: { path: { key } }, headers: { 'If-Match': `"${version}"` }, body })),
    onSuccess: invalidate,
  });
}

export function useDeleteEnvironment() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async ({ key, version }: { key: string; version: number }) => {
      const { error, response } = await api.DELETE('/api/v1/environments/{key}', { params: { path: { key } }, headers: { 'If-Match': `"${version}"` } });
      if (response.status === 401) throw new UnauthorizedError();
      if (!response.ok) throw asProblem(error, response.status);
    },
    onSuccess: invalidate,
  });
}

/** Same rule as the server and the database check constraint (ADR-17). */
export const ENVIRONMENT_KEY_PATTERN = /^[a-z][a-z0-9_-]{0,31}$/;

export type EnvironmentFormError = 'key' | 'label' | 'sortOrder' | null;

/** Client-side check before the request; the server repeats it. `key` is skipped when editing (keys are immutable). */
export function validateEnvironment(form: { key?: string; label: string; sortOrder: string }): EnvironmentFormError {
  if (form.key !== undefined && !ENVIRONMENT_KEY_PATTERN.test(form.key)) return 'key';
  const label = form.label.trim();
  if (label.length === 0 || label.length > 64) return 'label';
  if (form.sortOrder.trim() !== '' && !/^-?\d+$/.test(form.sortOrder.trim())) return 'sortOrder';
  return null;
}
