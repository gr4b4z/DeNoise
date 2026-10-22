import { describe, expect, it } from 'vitest';
import { DEFAULT_ENVIRONMENTS, environmentChips } from './queueSearch';

const dictionary = [
  { key: 'unknown', label: 'Unknown', sortOrder: 1000 },
  { key: 'staging', label: 'Staging', sortOrder: 20 },
  { key: 'production', label: 'Production', sortOrder: 10 },
  { key: 'uat', label: 'UAT', sortOrder: 25 },
];

describe('environmentChips (ADR-17)', () => {
  it('lists the dictionary in sort order with its labels, including classes the code never knew about', () => {
    expect(environmentChips(dictionary, undefined)).toEqual([
      { key: 'production', label: 'Production', configured: true },
      { key: 'staging', label: 'Staging', configured: true },
      { key: 'uat', label: 'UAT', configured: true },
      { key: 'unknown', label: 'Unknown', configured: true },
    ]);
  });

  it('appends a value from the URL that the dictionary does not know, once, marked as not configured', () => {
    const chips = environmentChips(dictionary, ['production', 'prodcution', 'prodcution']);
    expect(chips.map((c) => c.key)).toEqual(['production', 'staging', 'uat', 'unknown', 'prodcution']);
    expect(chips.at(-1)).toEqual({ key: 'prodcution', configured: false });
  });

  it('falls back to the default view classes while the dictionary loads, without flagging selected values', () => {
    const chips = environmentChips(undefined, ['staging']);
    expect(chips.map((c) => c.key)).toEqual([...DEFAULT_ENVIRONMENTS, 'staging']);
    expect(chips.every((c) => c.configured)).toBe(true);
  });
});
