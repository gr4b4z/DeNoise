import { describe, expect, it } from 'vitest';
import { localReturnTo, ssoHref } from './loginLinks';

describe('SSO link (M11b)', () => {
  it('carries a local returnTo to the start endpoint', () => {
    expect(ssoHref('/auth/oidc/start', '/episodes/1?tab=timeline')).toBe('/auth/oidc/start?returnTo=%2Fepisodes%2F1%3Ftab%3Dtimeline');
  });

  it('drops anything that is not a local path', () => {
    expect(ssoHref('/auth/oidc/start', 'https://evil.example')).toBe('/auth/oidc/start');
    expect(ssoHref('/auth/oidc/start', '//evil.example')).toBe('/auth/oidc/start');
    expect(localReturnTo('/\\evil.example')).toBeUndefined();
    expect(ssoHref('/auth/oidc/start', undefined)).toBe('/auth/oidc/start');
  });
});
