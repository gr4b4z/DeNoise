import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, resetCsrf } from '@/api/client';
import type { MeResponse } from '@/api/types';
import { asProblem } from '@/lib/problems';

export class UnauthorizedError extends Error {
  constructor() {
    super('unauthorized');
    this.name = 'UnauthorizedError';
  }
}

export const meQuery = () =>
  queryOptions({
    queryKey: ['me'],
    queryFn: async (): Promise<MeResponse> => {
      const { data, response } = await api.GET('/api/v1/me');
      if (response.status === 401) throw new UnauthorizedError();
      if (!data) throw new Error(`me failed: ${response.status}`);
      return data;
    },
    staleTime: 5 * 60_000,
    retry: false,
  });

export function useMe() {
  return useQuery(meQuery());
}

export const providersQuery = () =>
  queryOptions({
    queryKey: ['auth', 'providers'],
    queryFn: async () => {
      const { data } = await api.GET('/auth/providers');
      return data ?? { local: true, oidc: null, selfServiceReset: false };
    },
    staleTime: Infinity,
  });

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: { username: string; password: string; keepSignedIn: boolean }) => {
      const { response, error } = await api.POST('/auth/login', { body: input });
      if (!response.ok) throw asProblem(error, response.status);
      resetCsrf();
      await qc.invalidateQueries({ queryKey: ['me'] });
    },
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      await api.POST('/auth/logout');
      resetCsrf();
      qc.clear();
    },
  });
}

export function useChangePassword() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: { current: string; new: string }) => {
      const { response, error } = await api.POST('/auth/change-password', { body: input });
      if (!response.ok) throw asProblem(error, response.status);
      // Drop, don't just invalidate: the shell guard reads /me with a static stale time, and a stale
      // `mustChangePassword: true` would bounce the user straight back to this form.
      qc.removeQueries({ queryKey: ['me'] });
    },
  });
}
