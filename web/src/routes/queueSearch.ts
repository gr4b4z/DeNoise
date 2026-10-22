import type { QueueView } from '@/api/types';

/** URL state of the work queue (08 §2: every filterable screen serialises its state to the URL). */
export interface QueueSearch {
  view: QueueView;
  severity?: string[];
  team?: string;
  environment?: string[];
  service?: string;
  q?: string;
  /** Only episodes labelled flapping (spec §11.4). */
  flapping?: boolean;
  episode?: string;
}

/** One chip in the environment filter bar. `label` is absent until the dictionary has loaded; `configured` is false for a value outside it. */
export interface EnvironmentChip {
  key: string;
  label?: string;
  configured: boolean;
}

/**
 * Chips come from the server-side environment dictionary (ADR-17, `GET /api/v1/environments`) in its sort order, followed by any
 * value a shared URL or saved filter carries that the dictionary does not know (shown, but marked as not configured). Until the
 * dictionary loads, the default view's classes stand in so the bar never renders empty.
 */
export function environmentChips(dictionary: readonly { key: string; label: string; sortOrder: number }[] | undefined, selected: readonly string[] | undefined): EnvironmentChip[] {
  const known: EnvironmentChip[] = dictionary
    ? [...dictionary].sort((a, b) => a.sortOrder - b.sortOrder || a.key.localeCompare(b.key)).map((e) => ({ key: e.key, label: e.label, configured: true }))
    : DEFAULT_ENVIRONMENTS.map((key) => ({ key, configured: true }));
  const keys = new Set(known.map((c) => c.key));
  const extra = (selected ?? []).filter((key, i, all) => !keys.has(key) && all.indexOf(key) === i).map((key) => ({ key, configured: dictionary === undefined }));
  return [...known, ...extra];
}

/**
 * Spec §15.5: entry points open the queue on production plus unclassified episodes, so non-production noise stays out of the
 * default view while an alert whose mapping could not derive an environment is never hidden. A URL without `environment`
 * means every environment; only the entry points add the default.
 */
export const DEFAULT_ENVIRONMENTS: string[] = ['production', 'unknown'];

export const DEFAULT_QUEUE_SEARCH: QueueSearch = { view: 'needsAttention', environment: DEFAULT_ENVIRONMENTS };
