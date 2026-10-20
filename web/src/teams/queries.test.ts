import { describe, expect, it } from 'vitest';
import { parseScopes, validateTeam } from './queries';

describe('team form helpers', () => {
  it('splits scopes on commas and whitespace and keeps the first of each duplicate', () => {
    expect(parseScopes('mpt, prism')).toEqual(['mpt', 'prism']);
    expect(parseScopes('mpt prism  cloudiq')).toEqual(['mpt', 'prism', 'cloudiq']);
    expect(parseScopes('mpt, mpt , prism')).toEqual(['mpt', 'prism']);
    expect(parseScopes('   ')).toEqual([]);
  });

  it('requires a name and at least one scope', () => {
    expect(validateTeam('', ['mpt'])).toBe('name');
    expect(validateTeam('   ', ['mpt'])).toBe('name');
    expect(validateTeam('Platform', [])).toBe('scopes');
    expect(validateTeam('Platform', ['mpt'])).toBeNull();
  });
});
