import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import type { EpisodeListItem, PagedEpisodes } from '@/api/types';
import { applyEpisodeChanged, parseEpisodeChanged } from './cache';

function item(id: string, version: number): EpisodeListItem {
  return {
    id,
    severity: 'high',
    summary: 's',
    resourceName: 'r',
    service: null,
    environment: null,
    conditionState: 'firing',
    handlingState: 'new',
    owningTeam: { id: 't1', name: 'team' },
    assignee: null,
    firstSeen: '2026-09-11T12:00:00+00:00',
    lastSeen: '2026-09-11T12:00:00+00:00',
    occurrenceCount: 1,
    occurrenceCountExact: true,
    ackDeadlineAt: null,
    ackOverdue: false,
    nextEscalationAt: null,
    autoResolveAt: null,
    autoResolveSuspended: false,
    coverageState: 'unknown',
    stale: false,
    suppressedUntil: null,
    deliveryFailure: false,
    groupId: null,
    routingCorrectionRequired: false,
    integrationId: 'i1',
    integrationName: 'gen',
    accessScope: 'scope-a',
    isActionable: true,
    closedAt: null,
    closureReason: null,
    resolutionEvidence: null,
    version,
  };
}

const change = { id: 'e1', version: 2, handling: 'acknowledged', condition: 'firing', severity: 'high', teamId: 't1', closed: false };

describe('applyEpisodeChanged (08 §5 cache patching)', () => {
  it('patches the row in every list cache when the event is newer', () => {
    const qc = new QueryClient();
    const key = ['episodes', { view: 'needsAttention' }, 100];
    qc.setQueryData<PagedEpisodes>(key, { items: [item('e1', 1), item('e2', 1)], nextCursor: null, total: 2 });

    expect(applyEpisodeChanged(qc, change)).toBe('patched');
    const items = qc.getQueryData<PagedEpisodes>(key)?.items ?? [];
    expect(items[0]).toMatchObject({ id: 'e1', version: 2, handlingState: 'acknowledged' });
    expect(items[1]).toMatchObject({ id: 'e2', version: 1 });
  });

  it('ignores an event older than or equal to the cached version', () => {
    const qc = new QueryClient();
    qc.setQueryData<PagedEpisodes>(['episodes', {}, 100], { items: [item('e1', 3)], nextCursor: null, total: 1 });
    expect(applyEpisodeChanged(qc, change)).toBe('ignored');
    expect(qc.getQueryData<PagedEpisodes>(['episodes', {}, 100])?.items[0]?.version).toBe(3);
  });

  it('reports unknown when no list holds the row (the detail query is still invalidated)', () => {
    const qc = new QueryClient();
    expect(applyEpisodeChanged(qc, change)).toBe('unknown');
  });

  it('parses the server payload and rejects garbage', () => {
    expect(parseEpisodeChanged('{"id":"e1","version":2,"handling":"acknowledged"}')).toMatchObject({ id: 'e1', version: 2, handling: 'acknowledged' });
    expect(parseEpisodeChanged('not json')).toBeNull();
    expect(parseEpisodeChanged('{"id":1}')).toBeNull();
  });
});
