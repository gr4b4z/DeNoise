import type { QueueView } from '@/api/types';

/** URL state of the work queue (08 §2: every filterable screen serialises its state to the URL). */
export interface QueueSearch {
  view: QueueView;
  severity?: string[];
  team?: string;
  environment?: string[];
  service?: string;
  q?: string;
  episode?: string;
}

/** Environment chips offered in the filter bar (spec §9: production, staging, development or another configured value). */
export const ENVIRONMENTS = ['production', 'staging', 'development', 'unknown'] as const;

/**
 * Spec §15.5: entry points open the queue on production plus unclassified episodes, so non-production noise stays out of the
 * default view while an alert whose mapping could not derive an environment is never hidden. A URL without `environment`
 * means every environment; only the entry points add the default.
 */
export const DEFAULT_ENVIRONMENTS: string[] = ['production', 'unknown'];

export const DEFAULT_QUEUE_SEARCH: QueueSearch = { view: 'needsAttention', environment: DEFAULT_ENVIRONMENTS };
