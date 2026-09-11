import type { components } from './schema';

export type Schemas = components['schemas'];
export type EpisodeListItem = Schemas['EpisodeListItem'];
export type EpisodeDetail = Schemas['EpisodeDetail'];
export type TimelineEntry = Schemas['TimelineEntry'];
export type PagedEpisodes = Schemas['PagedResponseOfEpisodeListItem'];
export type PagedTimeline = Schemas['PagedResponseOfTimelineEntry'];
export type MeResponse = Schemas['MeResponse'];
export type ProblemDetails = Schemas['ProblemDetails'];
export type UserRef = Schemas['UserRef'];
export type TeamRef = Schemas['TeamRef'];
export type ClosureDto = Schemas['ClosureDto'];

/** RFC 9457 body as the API writes it (06 §1). `current` is present on 409 version conflicts. */
export interface Problem {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;
  lockedUntil?: string;
  retryAfter?: number;
  current?: EpisodeDetail;
  errors?: { path: string; message: string }[];
  [key: string]: unknown;
}

export const QUEUE_VIEWS = ['needsAttention', 'mine', 'myTeams', 'unassigned', 'acknowledged', 'stale', 'suppressed', 'closed'] as const;
export type QueueView = (typeof QUEUE_VIEWS)[number];
export const SEVERITIES = ['critical', 'high', 'medium', 'low', 'info', 'unknown'] as const;
export type Severity = (typeof SEVERITIES)[number];
