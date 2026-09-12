import type { MeResponse } from '@/api/types';

/** Permission names as the API spells them (04 §8); they double as PAT scopes. */
export const P = {
  episodeRead: 'episode.read',
  historyRead: 'history.read',
  episodeAck: 'episode.ack',
  episodeAssign: 'episode.assign',
  episodeNote: 'episode.note',
  episodeClose: 'episode.close',
  episodeRestore: 'episode.restore',
  episodeSilence: 'episode.silence',
  episodeBulk: 'episode.bulk',
  rawPayloadRead: 'episode.raw_payload.read',
  heartbeatManage: 'heartbeat.manage',
  integrationRead: 'integration.read',
  integrationManage: 'integration.manage',
  mappingManage: 'mapping.manage',
  replayPreview: 'replay.preview',
  replayRetry: 'replay.retry',
  replayHistorical: 'replay.historical',
  policyManage: 'policy.manage',
  teamManage: 'team.manage',
  destinationManage: 'destination.manage',
  suppressionCreateLong: 'suppression.create.long',
  hubHealthRead: 'hub.health.read',
  userManage: 'user.manage',
  meTokens: 'me.tokens',
  meSessions: 'me.sessions',
} as const;

export type Permission = (typeof P)[keyof typeof P];

export type Principal = Pick<MeResponse, 'roles' | 'scopes' | 'permissions'>;

/**
 * Mirrors the server rule (04 §8): a permission from the role matrix *and* the target's access scope in the user's scopes.
 * `platform_admin` sees every scope. Buttons and routes are gated with this; the server remains the authority.
 */
export function can(me: Principal | null | undefined, permission: string, scope?: string | null): boolean {
  if (!me) return false;
  if (!me.permissions.includes(permission)) return false;
  if (scope === undefined || scope === null) return true;
  return me.roles.includes('platform_admin') || me.scopes.includes(scope);
}

export function isPlatformAdmin(me: Principal | null | undefined): boolean {
  return me?.roles.includes('platform_admin') ?? false;
}
