import { describe, expect, it } from 'vitest';
import { can, P } from './permissions';

const operator = { roles: ['operator'], scopes: ['scope-a'], permissions: ['episode.read', 'episode.ack', 'episode.close'] };
const admin = { roles: ['platform_admin'], scopes: [], permissions: ['episode.read', 'episode.ack', 'user.manage'] };

describe('can() mirrors 04 §8', () => {
  it('requires the permission from the role matrix', () => {
    expect(can(operator, P.episodeAck)).toBe(true);
    expect(can(operator, P.userManage)).toBe(false);
  });

  it('requires the target scope unless the user is platform_admin', () => {
    expect(can(operator, P.episodeAck, 'scope-a')).toBe(true);
    expect(can(operator, P.episodeAck, 'scope-b')).toBe(false);
    expect(can(admin, P.episodeAck, 'scope-b')).toBe(true);
  });

  it('is false for an anonymous principal', () => {
    expect(can(null, P.episodeRead)).toBe(false);
    expect(can(undefined, P.episodeRead, 'scope-a')).toBe(false);
  });
});
