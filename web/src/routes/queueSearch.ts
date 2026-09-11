import type { QueueView } from '@/api/types';

/** URL state of the work queue (08 §2: every filterable screen serialises its state to the URL). */
export interface QueueSearch {
  view: QueueView;
  severity?: string[];
  team?: string;
  environment?: string;
  service?: string;
  q?: string;
  episode?: string;
}
