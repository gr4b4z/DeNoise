import { describe, expect, it } from 'vitest';
import { validateEnvironment } from './queries';

describe('validateEnvironment (ADR-17)', () => {
  it('accepts a lower-case key, a label and an optional whole-number order', () => {
    expect(validateEnvironment({ key: 'uat', label: 'UAT', sortOrder: '' })).toBeNull();
    expect(validateEnvironment({ key: 'pre-prod_2', label: 'Pre-prod', sortOrder: '25' })).toBeNull();
  });

  it('refuses keys the server and the check constraint would refuse', () => {
    for (const key of ['', 'UAT', '2nd', 'has space', 'a'.repeat(33)]) {
      expect(validateEnvironment({ key, label: 'X', sortOrder: '' })).toBe('key');
    }
  });

  it('skips the key when editing, since keys are immutable', () => {
    expect(validateEnvironment({ label: 'Staging', sortOrder: '20' })).toBeNull();
  });

  it('needs a label of 1–64 characters and a whole-number order', () => {
    expect(validateEnvironment({ key: 'uat', label: '   ', sortOrder: '' })).toBe('label');
    expect(validateEnvironment({ key: 'uat', label: 'x'.repeat(65), sortOrder: '' })).toBe('label');
    expect(validateEnvironment({ key: 'uat', label: 'UAT', sortOrder: '2.5' })).toBe('sortOrder');
  });
});
