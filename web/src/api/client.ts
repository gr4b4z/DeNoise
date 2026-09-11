import createClient, { type Middleware } from 'openapi-fetch';
import type { paths } from './schema';

/**
 * The only HTTP transport in the app (AGENTS.md rule 12). Cookies stay first-party through the Vite proxy / ingress.
 * Middleware adds the CSRF token to every cookie-authenticated mutation and announces 401s so the shell can redirect.
 */
export const api = createClient<paths>({ baseUrl: '', credentials: 'same-origin' });

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

let csrfToken: string | null = null;
let csrfInflight: Promise<string> | null = null;

async function fetchCsrf(): Promise<string> {
  const { data } = await api.GET('/auth/csrf');
  csrfToken = data?.token ?? '';
  return csrfToken;
}

/** Fetches the CSRF token once per session (a bearer principal receives an empty token and the header is skipped). */
export function ensureCsrf(): Promise<string> {
  if (csrfToken !== null) return Promise.resolve(csrfToken);
  csrfInflight ??= fetchCsrf().finally(() => {
    csrfInflight = null;
  });
  return csrfInflight;
}

export function resetCsrf(): void {
  csrfToken = null;
}

/** Fired with `unauthorized` when any call returns 401 (except the login call itself). */
export const authEvents = new EventTarget();

const csrfMiddleware: Middleware = {
  async onRequest({ request }) {
    if (SAFE_METHODS.has(request.method) || request.url.endsWith('/auth/login')) return request;
    const token = await ensureCsrf();
    if (token) request.headers.set('X-CSRF-Token', token);
    return request;
  },
  onResponse({ response, request }) {
    if (response.status === 401 && !request.url.endsWith('/auth/login')) {
      authEvents.dispatchEvent(new Event('unauthorized'));
    }
    if (response.status === 403 && request.method !== 'GET') {
      // A rejected CSRF token means our cached token is stale (new session); drop it so the next call fetches a fresh one.
      void response
        .clone()
        .json()
        .then((body: unknown) => {
          if (typeof body === 'object' && body !== null && (body as { type?: string }).type === 'urn:alerthub:error:csrf') resetCsrf();
        })
        .catch(() => undefined);
    }
    return response;
  },
};

api.use(csrfMiddleware);

/** Headers every episode action carries (06 §1): the version we saw and a fresh idempotency key. */
export function actionHeaders(version: number, idempotencyKey: string = crypto.randomUUID()): Record<string, string> {
  return { 'If-Match': `"${version}"`, 'Idempotency-Key': idempotencyKey };
}

/**
 * Retries a mutation on *network* failure only (never on an HTTP response) with the same idempotency key, up to two more times,
 * so a lost response cannot apply an action twice (08 §5).
 */
export async function withIdempotentRetry<T>(run: () => Promise<T>): Promise<T> {
  let attempt = 0;
  for (;;) {
    try {
      return await run();
    } catch (error) {
      attempt += 1;
      if (attempt > 2 || !(error instanceof TypeError)) throw error;
      await new Promise((resolve) => setTimeout(resolve, 300 * attempt));
    }
  }
}
