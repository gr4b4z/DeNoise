import { describe, expect, it } from 'vitest';
import { describeInterval, pingSnippets, toShort } from './queries';

describe('heartbeat helpers', () => {
  it('describes .NET interval strings in words', () => {
    expect(describeInterval('00:05:00')).toBe('every 5 min');
    expect(describeInterval('01:00:00')).toBe('hourly');
    expect(describeInterval('1.00:00:00')).toBe('daily');
    expect(describeInterval('00:00:30')).toBe('every 30 s');
  });

  it('turns hh:mm:ss into the short form the API accepts', () => {
    expect(toShort('00:05:00')).toBe('5m');
    expect(toShort('02:00:00')).toBe('2h');
    expect(toShort('7.00:00:00')).toBe('7d');
    expect(toShort('00:00:45')).toBe('45s');
    expect(toShort('15m')).toBe('15m');
  });

  it('snippets embed the ping URL and cover curl, PowerShell, Actions and cron', () => {
    const url = 'https://ingest.example/hb/abcdefghjkmn.secret';
    const snippets = pingSnippets(url);
    expect(snippets.map((s) => s.label)).toHaveLength(5);
    expect(snippets[0]?.code).toBe(`curl -fsS --retry 3 ${url}`);
    for (const s of snippets) expect(s.code).toContain('abcdefghjkmn');
    expect(snippets.find((s) => s.label === 'PowerShell')?.code).toContain('Invoke-RestMethod');
  });
});
