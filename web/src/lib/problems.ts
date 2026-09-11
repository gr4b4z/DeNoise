import type { Problem } from '@/api/types';

/** Normalises whatever openapi-fetch hands back as `error` into a Problem (06 §1). */
export function asProblem(error: unknown, status?: number): Problem {
  if (typeof error === 'object' && error !== null) {
    const p = error as Problem;
    return { ...p, status: p.status ?? status };
  }
  if (typeof error === 'string' && error.length > 0) return { title: error, status };
  return { title: 'Request failed', status };
}

export function problemCode(problem: Problem | undefined): string | undefined {
  const type = problem?.type;
  if (!type) return undefined;
  const marker = 'urn:alerthub:error:';
  return type.startsWith(marker) ? type.slice(marker.length) : type;
}

/** Copy the operator sees for a Problem, following 08 §3.8 / §7 (state what happened and what to do). */
export function problemMessage(problem: Problem | undefined, fallback = 'Something went wrong. Try again.'): string {
  if (!problem) return fallback;
  const code = problemCode(problem);
  switch (code) {
    case 'invalid-credentials':
      return 'Incorrect username or password';
    case 'locked': {
      const until = problem.lockedUntil ? new Date(problem.lockedUntil) : undefined;
      if (until && !Number.isNaN(until.getTime())) {
        const minutes = Math.max(1, Math.ceil((until.getTime() - Date.now()) / 60_000));
        return `Account locked — try again in ${minutes} minute${minutes === 1 ? '' : 's'}`;
      }
      return 'Account locked — try again later';
    }
    case 'version-conflict':
      return 'Updated by someone else just now — refreshing';
    case 'forbidden':
      return 'You do not have access to this scope';
    case 'not-found':
      return 'Not found, or not visible to you';
    case 'csrf':
      return 'Your session changed. Reload the page and try again.';
    case 'rate-limited':
    case 'too-many-requests':
      return 'Too many requests — wait a moment and try again';
    default:
      return problem.detail ?? problem.title ?? fallback;
  }
}
