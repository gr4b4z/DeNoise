import { describe, expect, it } from 'vitest';
import { diffLines } from './diff';

describe('diffLines', () => {
  it('marks unchanged, removed and added lines', () => {
    const before = '{\n  "a": 1,\n  "b": 2\n}';
    const after = '{\n  "a": 1,\n  "b": 3,\n  "c": 4\n}';
    expect(diffLines(before, after).map((l) => `${l.op[0]}:${l.text.trim()}`)).toEqual(['s:{', 's:"a": 1,', 'd:"b": 2', 'a:"b": 3,', 'a:"c": 4', 's:}']);
  });

  it('is empty-safe', () => {
    expect(diffLines('', '')).toEqual([{ op: 'same', text: '' }]);
    expect(diffLines('x', '')).toEqual([{ op: 'del', text: 'x' }, { op: 'add', text: '' }]);
  });
});
