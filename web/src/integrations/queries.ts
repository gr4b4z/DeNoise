import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type IntegrationSummary = Schemas['IntegrationSummary'];
export type IntegrationHealth = Schemas['IntegrationHealth'];
export type CreateIntegrationRequest = Schemas['CreateIntegrationRequest'];
export type UpdateIntegrationRequest = Schemas['UpdateIntegrationRequest'];
export type IntegrationCreatedResponse = Schemas['IntegrationCreatedResponse'];
export type MappingVersion = Schemas['MappingVersionDto'];
export type CreateMappingRequest = Schemas['CreateMappingRequest'];
export type MappingPreviewRequest = Schemas['MappingPreviewRequest'];
export type MappingPreviewResponse = Schemas['MappingPreviewResponse'];
export type MappingPreviewItem = Schemas['MappingPreviewItemDto'];
export type MappingFailure = Schemas['MappingFailureDto'];
export type ReferenceMapping = Schemas['ReferenceMappingDto'];
export type StartReplayRequest = Schemas['StartReplayRequest'];
export type ReplayStatus = Schemas['ReplayStatus'];
export type HmacSettings = Schemas['HmacSettings'];

export const INTEGRATION_TYPES = [
  { value: 'azure_monitor', label: 'Azure Monitor (Common Alert Schema)', profile: 'explicit_recovery' },
  { value: 'atlas', label: 'MongoDB Atlas', profile: 'queryable_state' },
  { value: 'generic_webhook', label: 'Generic JSON webhook', profile: 'unknown' },
] as const;

export type IntegrationType = (typeof INTEGRATION_TYPES)[number]['value'];

export const LIFECYCLE_PROFILES = [
  { value: 'explicit_recovery', label: 'Explicit recovery', closeAfterMinutes: 60, verify: true, text: 'The source sends a recovery. As a backstop, episodes close automatically after 60 minutes without a signal; where the source has a state API the Hub asks it first.' },
  { value: 'queryable_state', label: 'Queryable state', closeAfterMinutes: 30, verify: true, text: 'The source can be asked whether the condition is still active. Episodes close after 30 minutes of silence once the API confirms recovery, or unverified when it cannot be asked.' },
  { value: 'repeating_while_active', label: 'Repeating while active', closeAfterMinutes: 15, verify: false, text: 'The source repeats while the condition holds. Episodes close after 3 × the repeat interval + delivery grace (at least 15 minutes) without a signal.' },
  { value: 'one_shot', label: 'One-shot / informational', closeAfterMinutes: null, verify: false, text: 'Events are notices without a recovery. Episodes expire as informational after 24 hours and are never auto-resolved.' },
] as const;

async function unwrap<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (data === undefined) throw asProblem(error, response.status);
  return data;
}

async function unwrapEmpty(promise: Promise<{ error?: unknown; response: Response }>): Promise<null> {
  const { error, response } = await promise;
  if (response.status === 401) throw new UnauthorizedError();
  if (!response.ok) throw asProblem(error, response.status);
  return null;
}

export const integrationsQuery = () =>
  queryOptions({
    queryKey: ['integration', 'list'],
    queryFn: () => unwrap(api.GET('/api/v1/integrations')),
    staleTime: 10_000,
  });

export const integrationQuery = (id: string) =>
  queryOptions({
    queryKey: ['integration', id],
    queryFn: () => unwrap(api.GET('/api/v1/integrations/{id}', { params: { path: { id } } })),
    staleTime: 5_000,
  });

/** Query keys start with `coverage` too so the SSE `coverage.changed` event refreshes health. */
export const integrationHealthQuery = (id: string) =>
  queryOptions({
    queryKey: ['coverage', 'integration-health', id],
    queryFn: () => unwrap(api.GET('/api/v1/integrations/{id}/health', { params: { path: { id } } })),
    staleTime: 10_000,
    refetchInterval: 30_000,
  });

export const mappingVersionsQuery = (id: string) =>
  queryOptions({
    queryKey: ['integration', id, 'mappings'],
    queryFn: () => unwrap(api.GET('/api/v1/integrations/{id}/mappings', { params: { path: { id } } })),
    staleTime: 5_000,
  });

export const failuresQuery = (id: string, all = false) =>
  queryOptions({
    queryKey: ['integration', id, 'failures', all],
    queryFn: () => unwrap(api.GET('/api/v1/integrations/{id}/failures', { params: { path: { id }, query: { all: all || undefined, limit: 200 } } })),
    staleTime: 5_000,
  });

export const referenceMappingsQuery = (type: string) =>
  queryOptions({
    queryKey: ['integration', 'reference-mappings', type],
    queryFn: () => unwrap(api.GET('/api/v1/integrations/reference-mappings/{type}', { params: { path: { type } } })),
    staleTime: Infinity,
  });

export const replayQuery = (jobId: string, enabled: boolean) =>
  queryOptions({
    queryKey: ['replay', jobId],
    queryFn: () => unwrap(api.GET('/api/v1/replay/{jobId}', { params: { path: { jobId } } })),
    enabled,
    refetchInterval: (q) => (q.state.data && (q.state.data.status === 'done' || q.state.data.status === 'failed') ? false : 2_000),
  });

export function useCreateIntegration() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateIntegrationRequest) => unwrap(api.POST('/api/v1/integrations', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration'] }),
  });
}

export function useUpdateIntegration(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ version, body }: { version: number; body: UpdateIntegrationRequest }) =>
      unwrap(api.PUT('/api/v1/integrations/{id}', { params: { path: { id } }, headers: { 'If-Match': `"${version}"` }, body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration'] }),
  });
}

export function useRotateIngestToken(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/integrations/{id}/rotate-ingest-token', { params: { path: { id } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration'] }),
  });
}

export function usePreviewMapping(id: string) {
  return useMutation({
    mutationFn: ({ mappingId, version, body }: { mappingId?: string; version?: number; body: MappingPreviewRequest }) =>
      mappingId && version
        ? unwrap(api.POST('/api/v1/integrations/{id}/mappings/{mappingId}/{version}/preview', { params: { path: { id, mappingId, version } }, body }))
        : unwrap(api.POST('/api/v1/integrations/{id}/mappings/preview', { params: { path: { id } }, body })),
  });
}

export function useCreateMappingVersion(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateMappingRequest) => unwrap(api.POST('/api/v1/integrations/{id}/mappings', { params: { path: { id } }, body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration', id, 'mappings'] }),
  });
}

export function useActivateMapping(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ mappingId, version }: { mappingId: string; version: number }) =>
      unwrapEmpty(api.POST('/api/v1/integrations/{id}/mappings/{mappingId}/{version}/activate', { params: { path: { id, mappingId, version } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration', id, 'mappings'] }),
  });
}

export function useDismissFailure(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (failureId: string) => unwrapEmpty(api.POST('/api/v1/integrations/{id}/failures/{failureId}/dismiss', { params: { path: { id, failureId } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration', id, 'failures'] }),
  });
}

export function useStartReplay() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: StartReplayRequest) => unwrap(api.POST('/api/v1/replay', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['integration'] }),
  });
}

/** Health badge state from the read model (08 §3.5 list): coverage state first, then the pipeline symptoms. */
export function healthTone(h: IntegrationHealth | undefined): { state: string; tone: 'ok' | 'warn' | 'bad' | 'muted' } {
  if (!h) return { state: 'loading', tone: 'muted' };
  if (h.coverageState === 'unavailable') return { state: 'coverage unavailable', tone: 'bad' };
  if (h.coverageState === 'degraded' || h.coverageState === 'delayed') return { state: `coverage ${h.coverageState}`, tone: 'warn' };
  if (h.mappingFailuresLast15m > 0) return { state: `${h.mappingFailuresLast15m} mapping failures`, tone: 'warn' };
  if (h.pendingNormalise > 25) return { state: `backlog ${h.pendingNormalise}`, tone: 'warn' };
  if (h.coverageState === 'healthy') return { state: 'healthy', tone: 'ok' };
  if (h.coverageState === 'not_configured') return { state: 'no coverage method', tone: 'muted' };
  return { state: h.coverageState, tone: 'muted' };
}

/** `HH:MM:SS` for the coverage / timeout inputs; the API stores .NET TimeSpans. */
export function minutesToTimeSpan(minutes: number): string {
  const total = Math.max(1, Math.round(minutes)) * 60;
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:00`;
}

export function safeJson(text: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(text);
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}
