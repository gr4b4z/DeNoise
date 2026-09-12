import { infiniteQueryOptions, queryOptions, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/api/client';
import type { Schemas } from '@/api/types';
import { UnauthorizedError } from '@/auth/queries';
import { asProblem } from '@/lib/problems';

export type PolicyVersion = Schemas['PolicyVersionDto'];
export type PolicyImpact = Schemas['PolicyImpactDto'];
export type ConfigImportResponse = Schemas['ConfigImportResponse'];
export type ConfigImportEntry = Schemas['ConfigImportEntryDto'];
export type AuditEntry = Schemas['AuditEntryDto'];
export type PagedAudit = Schemas['PagedResponseOfAuditEntryDto'];

/** 04 §7: the four policy kinds; routing and grouping are single documents with well-known ids, escalation and lifecycle are many. */
export const POLICY_KINDS = ['routing', 'escalation', 'lifecycle', 'grouping'] as const;
export type PolicyKind = (typeof POLICY_KINDS)[number];
export const SINGLE_DOCUMENT_KINDS: readonly PolicyKind[] = ['routing', 'grouping'];
/** Kinds whose activation has an impact preview (08 §3.6: shown before Activate enables). */
export const IMPACT_KINDS: readonly PolicyKind[] = ['lifecycle', 'routing'];

export function isPolicyKind(value: string | undefined): value is PolicyKind {
  return (POLICY_KINDS as readonly string[]).includes(value ?? '');
}

/** Starting documents for a new policy of each kind (07 §5 predicates, 04 §7 fields). */
export const POLICY_SKELETONS: Record<PolicyKind, string> = {
  routing: 'routing:\n  rules:\n    - name: example\n      priority: 10\n      match: { eq: [service, orders] }\n      team: 00000000-0000-0000-0000-000000000000\n',
  escalation: 'escalation:\n  ack_deadline: 15m\n  steps:\n    - { after: 0m, targets: [team_destinations] }\n    - { after: 15m, targets: [team_destinations] }\n  repeat_last_step_every: 30m\n  max_repeats: 2\n',
  lifecycle: 'lifecycle: { profile: repeating_while_active }\nauto_resolve: { after_silence: 45m }\n',
  grouping: 'grouping:\n  rules:\n    - name: by-service-and-environment\n      match: { exists: "service" }\n      key: [service, environment]\n      window: 10m\n      notify: first_and_new_critical\n',
};

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

// ---- policies -------------------------------------------------------------------------------------------------------

export const activePoliciesQuery = (kind: PolicyKind) =>
  queryOptions({
    queryKey: ['policy', kind, 'active'],
    queryFn: () => unwrap(api.GET('/api/v1/policies/{kind}', { params: { path: { kind } } })),
    staleTime: 5_000,
  });

export const policyVersionsQuery = (kind: PolicyKind, id: string) =>
  queryOptions({
    queryKey: ['policy', kind, id, 'versions'],
    queryFn: () => unwrap(api.GET('/api/v1/policies/{kind}/{id}/versions', { params: { path: { kind, id } } })),
    staleTime: 5_000,
  });

export const policyImpactQuery = (kind: PolicyKind, id: string, version: number) =>
  queryOptions({
    queryKey: ['policy', kind, id, version, 'impact'],
    queryFn: () => unwrap(api.POST('/api/v1/policies/{kind}/{id}/{version}/impact', { params: { path: { kind, id, version } } })),
    staleTime: 10_000,
    retry: false,
  });

export function useCreatePolicyVersion() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ kind, ...body }: { kind: PolicyKind; yaml: string; policyId: string | null; name: string | null }) => unwrap(api.POST('/api/v1/policies/{kind}', { params: { path: { kind } }, body })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['policy'] }),
  });
}

export function useActivatePolicy() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ kind, id, version }: { kind: PolicyKind; id: string; version: number }) => unwrapEmpty(api.POST('/api/v1/policies/{kind}/{id}/{version}/activate', { params: { path: { kind, id, version } } })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['policy'] });
      void qc.invalidateQueries({ queryKey: ['episodes'] });
    },
  });
}

export function useRollbackPolicy() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ kind, id, toVersion }: { kind: PolicyKind; id: string; toVersion: number }) => unwrapEmpty(api.POST('/api/v1/policies/{kind}/{id}/rollback', { params: { path: { kind, id } }, body: { toVersion } })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['policy'] });
      void qc.invalidateQueries({ queryKey: ['episodes'] });
    },
  });
}

// ---- config-as-code -------------------------------------------------------------------------------------------------

export const configExportQuery = () =>
  queryOptions({
    queryKey: ['config', 'export'],
    queryFn: async (): Promise<string> => {
      const { data, error, response } = await api.GET('/api/v1/config/export', { parseAs: 'text' });
      if (response.status === 401) throw new UnauthorizedError();
      if (!response.ok || data === undefined) throw asProblem(error, response.status);
      return data;
    },
    staleTime: 0,
  });

export function useImportConfig() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { yaml: string; dryRun: boolean }) => unwrap(api.POST('/api/v1/config/import', { body })),
    onSuccess: (_result, variables) => {
      if (!variables.dryRun) {
        void qc.invalidateQueries({ queryKey: ['policy'] });
        void qc.invalidateQueries({ queryKey: ['config'] });
        void qc.invalidateQueries({ queryKey: ['audit'] });
      }
    },
  });
}

// ---- audit ----------------------------------------------------------------------------------------------------------

export interface AuditFilters {
  target?: string;
  targetType?: string;
  actor?: string;
  action?: string;
  from?: string;
  to?: string;
}

export const AUDIT_TARGET_TYPES = ['episode', 'policy', 'integration', 'mapping', 'destination', 'team', 'user', 'suppression', 'heartbeat', 'webhook_template', 'session'] as const;

export const auditQuery = (filters: AuditFilters, limit = 100) =>
  infiniteQueryOptions({
    queryKey: ['audit', filters, limit],
    queryFn: ({ pageParam }): Promise<PagedAudit> =>
      unwrap(
        api.GET('/api/v1/audit', {
          params: {
            query: {
              target: filters.target || undefined,
              targetType: filters.targetType || undefined,
              actor: filters.actor || undefined,
              action: filters.action || undefined,
              from: filters.from || undefined,
              to: filters.to || undefined,
              limit,
              cursor: pageParam || undefined,
            },
          },
        }),
      ),
    initialPageParam: '',
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: 10_000,
  });

/** Where an audit target lives in the UI, when it has a screen. */
export function auditTargetLink(targetType: string, targetId: string): { to: string; params: Record<string, string> } | null {
  switch (targetType) {
    case 'episode':
      return { to: '/episodes/$id', params: { id: targetId } };
    case 'integration':
      return { to: '/integrations/$id', params: { id: targetId } };
    case 'destination':
      return { to: '/destinations/$id', params: { id: targetId } };
    case 'heartbeat':
      return { to: '/heartbeats/$id', params: { id: targetId } };
    case 'webhook_template':
      return { to: '/templates/$id', params: { id: targetId } };
    case 'team':
      return { to: '/teams/$id', params: { id: targetId } };
    default:
      return null;
  }
}

/** `policy.lifecycle.activate` → kind for the policy target link. */
export function policyKindFromAction(action: string): PolicyKind | null {
  const parts = action.split('.');
  return parts[0] === 'policy' && isPolicyKind(parts[1]) ? parts[1] : null;
}
