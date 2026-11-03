/** A returnTo worth carrying through sign-in: a local path, never protocol-relative (the server re-validates it). */
export function localReturnTo(returnTo: string | undefined): string | undefined {
  if (!returnTo || !returnTo.startsWith('/') || returnTo.startsWith('//') || returnTo.startsWith('/\\')) return undefined;
  return returnTo;
}

/** The SSO button's target: the server-side start endpoint from /auth/providers, plus the page to come back to. */
export function ssoHref(loginUrl: string, returnTo: string | undefined): string {
  const target = localReturnTo(returnTo);
  if (!target) return loginUrl;
  return `${loginUrl}${loginUrl.includes('?') ? '&' : '?'}returnTo=${encodeURIComponent(target)}`;
}
