export type DiffOp = 'same' | 'add' | 'del';
export interface DiffLine {
  op: DiffOp;
  text: string;
}

/**
 * Line diff for the template version compare (08 §3.7b): a plain LCS over lines is enough for bodies of a few hundred lines
 * and keeps the diff readable without a dependency. Ties resolve towards deletions first, so a changed line reads "old, new".
 */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = before.split('\n');
  const b = after.split('\n');
  const n = a.length;
  const m = b.length;
  // lcs[i * (m + 1) + j] = length of the LCS of a[i..] and b[j..] (flat array: no undefined cells to assert away)
  const w = m + 1;
  const lcs = new Uint32Array((n + 1) * w);
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i * w + j] = a[i] === b[j] ? (lcs[(i + 1) * w + j + 1] ?? 0) + 1 : Math.max(lcs[(i + 1) * w + j] ?? 0, lcs[i * w + j + 1] ?? 0);
    }
  }
  const out: DiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    const left = a[i] ?? '';
    const right = b[j] ?? '';
    if (left === right) {
      out.push({ op: 'same', text: left });
      i++;
      j++;
    } else if ((lcs[(i + 1) * w + j] ?? 0) >= (lcs[i * w + j + 1] ?? 0)) {
      out.push({ op: 'del', text: left });
      i++;
    } else {
      out.push({ op: 'add', text: right });
      j++;
    }
  }
  while (i < n) out.push({ op: 'del', text: a[i++] ?? '' });
  while (j < m) out.push({ op: 'add', text: b[j++] ?? '' });
  return out;
}
