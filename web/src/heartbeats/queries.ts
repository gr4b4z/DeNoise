import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type HeartbeatSummary = Schemas['HeartbeatSummary'];
export type HeartbeatDetail = Schemas['HeartbeatDetail'];
export type HeartbeatRequest = Schemas['HeartbeatRequest'];
export type HeartbeatRun = Schemas['HeartbeatRunDto'];

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

export interface HeartbeatFilters {
  state?: string;
  team?: string;
  q?: string;
}

/** Query keys start with `heartbeat` so the SSE `heartbeat.changed` event invalidates them (realtime/sse.ts). */
export const heartbeatsQuery = (filters: HeartbeatFilters = {}) =>
  queryOptions({
    queryKey: ['heartbeat', 'list', filters],
    queryFn: () => unwrap(api.GET('/api/v1/heartbeats', { params: { query: { state: filters.state || undefined, team: filters.team || undefined, q: filters.q || undefined } } })),
    staleTime: 10_000,
  });

export const heartbeatQuery = (id: string) =>
  queryOptions({
    queryKey: ['heartbeat', id],
    queryFn: () => unwrap(api.GET('/api/v1/heartbeats/{id}', { params: { path: { id } } })),
    staleTime: 5_000,
  });

export const schedulePreviewQuery = (schedule: Schemas['HeartbeatScheduleRequest'], enabled: boolean) =>
  queryOptions({
    queryKey: ['heartbeat', 'preview', schedule],
    queryFn: () => unwrap(api.POST('/api/v1/heartbeats/preview', { body: { schedule, count: 5 } })),
    enabled,
    retry: false,
    staleTime: 60_000,
  });

export function useCreateHeartbeat() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: HeartbeatRequest) => unwrap(api.POST('/api/v1/heartbeats', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['heartbeat'] }),
  });
}

export function useUpdateHeartbeat(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ version, body }: { version: number; body: HeartbeatRequest }) =>
      unwrap(api.PUT('/api/v1/heartbeats/{id}', { params: { path: { id } }, headers: { 'If-Match': `"${version}"` }, body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['heartbeat'] }),
  });
}

export function useHeartbeatAction(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ action, version, reason }: { action: 'pause' | 'resume' | 'rotate' | 'delete'; version: number; reason?: string }) => {
      const params = { path: { id } };
      const headers = { 'If-Match': `"${version}"` };
      switch (action) {
        case 'pause':
          return unwrap(api.POST('/api/v1/heartbeats/{id}/pause', { params, headers, body: { reason: reason ?? '' } }));
        case 'resume':
          return unwrap(api.POST('/api/v1/heartbeats/{id}/resume', { params, headers }));
        case 'rotate':
          return unwrap(api.POST('/api/v1/heartbeats/{id}/rotate-token', { params, headers }));
        case 'delete': {
          const { response, error } = await api.DELETE('/api/v1/heartbeats/{id}', { params, headers });
          if (!response.ok) throw asProblem(error, response.status);
          return null;
        }
      }
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['heartbeat'] }),
  });
}

/** Copyable snippets for the one-time dialog (08 §3.4): curl, PowerShell, GitHub Actions, cron line. */
export function pingSnippets(pingUrl: string): { label: string; code: string }[] {
  return [
    { label: 'Shell (end of the job)', code: `curl -fsS --retry 3 ${pingUrl}` },
    { label: 'Shell with start / exit code', code: `curl -fsS -X POST ${pingUrl}/start\n# … your job …\ncurl -fsS -X POST --data-binary @job.log ${pingUrl}/exit/$?` },
    { label: 'PowerShell', code: `Invoke-RestMethod -Method Get -Uri '${pingUrl}' | Out-Null` },
    {
      label: 'GitHub Actions step',
      code: `- name: Alert Hub heartbeat\n  if: success()\n  run: curl -fsS --retry 3 "\${{ secrets.ALERTHUB_PING_URL }}"\n  # store ${pingUrl.slice(0, pingUrl.lastIndexOf('.'))}.<token> as ALERTHUB_PING_URL`,
    },
    { label: 'cron line', code: `*/5 * * * * /usr/local/bin/backup.sh && curl -fsS --retry 3 ${pingUrl} > /dev/null` },
  ];
}

/** Seconds in a .NET TimeSpan string (`d.hh:mm:ss` or `hh:mm:ss`), or null when it is not one. */
export function parseTimeSpan(value: string): number | null {
  const m = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(value.trim());
  if (!m) return null;
  return Number(m[1] ?? 0) * 86_400 + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]);
}

/** "00:05:00" → "5m": the short form the API accepts; anything that is not a TimeSpan is kept as typed. */
export function toShort(value: string): string {
  const total = parseTimeSpan(value);
  if (total === null) return value;
  if (total % 86_400 === 0) return `${total / 86_400}d`;
  if (total % 3600 === 0) return `${total / 3600}h`;
  if (total % 60 === 0) return `${total / 60}m`;
  return `${total}s`;
}

/** "every 5 min" for interval strings like "00:05:00" when the server description is not at hand. */
export function describeInterval(interval: string | null | undefined): string {
  if (!interval) return '';
  const total = parseTimeSpan(interval);
  if (total === null) return interval;
  if (total < 60) return `every ${total} s`;
  if (total < 3600) return `every ${Math.round(total / 60)} min`;
  if (total < 86_400) return total === 3600 ? 'hourly' : `every ${Math.round(total / 3600)} h`;
  return total === 86_400 ? 'daily' : `every ${Math.round(total / 86_400)} d`;
}
