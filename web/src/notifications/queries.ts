import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type DestinationSummary = Schemas['DestinationSummary'];
export type CreateDestinationRequest = Schemas['CreateDestinationRequest'];
export type UpdateDestinationRequest = Schemas['UpdateDestinationRequest'];
export type DestinationUpdatedResponse = Schemas['DestinationUpdatedResponse'];
export type DestinationPairResponse = Schemas['DestinationPairResponse'];
export type TestSendResponse = Schemas['TestSendResponse'];
export type RevealUrlResponse = Schemas['RevealUrlResponse'];
export type DeliveryAttempt = Schemas['DeliveryAttemptDto'];
export type WebhookTemplate = Schemas['WebhookTemplateDto'];
export type CreateTemplateRequest = Schemas['CreateTemplateRequest'];
export type RenderedTemplate = Schemas['RenderedTemplateResponse'];

/** Built-in template ids (Application `BuiltInTemplates`); `generic-json` is the model itself and never rendered. */
export const BUILTIN = {
  genericJson: '00000000-0000-0000-0000-00000000f001',
  teamsAdaptiveCard: '00000000-0000-0000-0000-00000000f002',
  slackBlocks: '00000000-0000-0000-0000-00000000f003',
  plainText: '00000000-0000-0000-0000-00000000f004',
} as const;

/** Outbox / notification event types a destination can subscribe to (04 §6), in the order the form lists them. */
export const EVENT_TYPES = [
  'episode.opened',
  'episode.escalated_severity',
  'episode.ack_overdue',
  'episode.follow_up_due',
  'episode.acknowledged',
  'episode.closed',
  'episode.stale_critical',
  'episode.routing_failure',
  'episode.summary_after_suppression',
  'hub.delivery_failure',
  'coverage.lost',
  'coverage.restored',
  'heartbeat.missed',
  'heartbeat.recovered',
] as const;

export const DEFAULT_EVENT_TYPES: string[] = EVENT_TYPES.filter((t) => t !== 'episode.acknowledged' && t !== 'episode.summary_after_suppression');

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

// ---- destinations -------------------------------------------------------------------------------------------------

export const destinationsQuery = () =>
  queryOptions({
    queryKey: ['destination', 'list'],
    queryFn: () => unwrap(api.GET('/api/v1/destinations')),
    staleTime: 10_000,
  });

export const destinationQuery = (id: string) =>
  queryOptions({
    queryKey: ['destination', id],
    queryFn: () => unwrap(api.GET('/api/v1/destinations/{id}', { params: { path: { id } } })),
    staleTime: 5_000,
  });

export const deliveriesQuery = (id: string, limit = 50) =>
  queryOptions({
    queryKey: ['destination', id, 'deliveries', limit],
    queryFn: () => unwrap(api.GET('/api/v1/destinations/{id}/deliveries', { params: { path: { id }, query: { limit } } })),
    staleTime: 5_000,
  });

export function useCreateDestination() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateDestinationRequest) => unwrap(api.POST('/api/v1/destinations', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['destination'] }),
  });
}

export function useCreateDestinationPair() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { primary: CreateDestinationRequest; fallback: CreateDestinationRequest }) => unwrap(api.POST('/api/v1/destinations/pair', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['destination'] }),
  });
}

export function useUpdateDestination(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ version, body }: { version: number; body: UpdateDestinationRequest }) =>
      unwrap(api.PUT('/api/v1/destinations/{id}', { params: { path: { id } }, headers: { 'If-Match': `"${version}"` }, body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['destination'] }),
  });
}

export function useTestDestination(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/destinations/{id}/test', { params: { path: { id } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['destination'] }),
  });
}

export function useRevealDestination(id: string) {
  return useMutation({
    mutationFn: (password: string) => unwrap(api.POST('/api/v1/destinations/{id}/reveal', { params: { path: { id } }, body: { password } })),
  });
}

// ---- templates ----------------------------------------------------------------------------------------------------

export const templatesQuery = () =>
  queryOptions({
    queryKey: ['template', 'list'],
    queryFn: () => unwrap(api.GET('/api/v1/webhook-templates')),
    staleTime: 30_000,
  });

export const templateVersionsQuery = (id: string) =>
  queryOptions({
    queryKey: ['template', id, 'versions'],
    queryFn: () => unwrap(api.GET('/api/v1/webhook-templates/{id}', { params: { path: { id } } })),
    staleTime: 5_000,
  });

/** Renders a stored version against the sample (or a real episode) — the model reference and the picker preview use it. */
export const templateRenderQuery = (id: string, version: number, episodeId?: string | null) =>
  queryOptions({
    queryKey: ['template', id, version, 'render', episodeId ?? ''],
    queryFn: () => unwrap(api.POST('/api/v1/webhook-templates/{id}/{version}/render', { params: { path: { id, version } }, body: { episodeId: episodeId || null, sampleEvent: null } })),
    retry: false,
    staleTime: 30_000,
  });

export function useRenderDraft() {
  return useMutation({
    mutationFn: (body: { format: string; body: string; contentType?: string | null; sampleEvent?: unknown }) => unwrap(api.POST('/api/v1/webhook-templates/render', { body })),
  });
}

export function useCreateTemplateVersion() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateTemplateRequest) => unwrap(api.POST('/api/v1/webhook-templates', { body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['template'] }),
  });
}

export function useActivateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, version }: { id: string; version: number }) => unwrapEmpty(api.POST('/api/v1/webhook-templates/{id}/{version}/activate', { params: { path: { id, version } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['template'] }),
  });
}

/** Pretty-prints JSON bodies for the preview panes; anything that is not JSON is shown as is. */
export function prettyBody(body: string, contentType: string): string {
  if (!/json/i.test(contentType)) return body;
  try {
    return JSON.stringify(JSON.parse(body), null, 2);
  } catch {
    return body;
  }
}

/** Header rows as the form edits them ↔ the API's object; blank names are dropped. */
export function headersToObject(rows: { name: string; value: string }[]): Record<string, string> | null {
  const entries = rows.filter((r) => r.name.trim()).map((r) => [r.name.trim(), r.value] as const);
  return entries.length === 0 ? null : Object.fromEntries(entries);
}
