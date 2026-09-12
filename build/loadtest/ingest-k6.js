// Alert Hub ingest load test (09 M11: baseline ×1 sustained, ×3 burst).
//
//   k6 run -e INGEST_URL=https://alerts.example/ingest/<keyId> -e TOKEN=<ingest token> -e BASELINE_RPS=10 build/loadtest/ingest-k6.js
//
// BASELINE_RPS is the measured peak from owner input C9 (events per second across the hub). The `sustained` scenario
// holds ×1 for 10 minutes, `burst` then pushes ×3 for 2 minutes. Thresholds encode the spec §17 budget: p95 accept
// latency under 250 ms, no 5xx, and 429s only in the burst (the per-integration limit is allowed to bite there).
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const baseline = Number(__ENV.BASELINE_RPS || 10);
const url = __ENV.INGEST_URL;
const token = __ENV.TOKEN;
if (!url || !token) throw new Error('INGEST_URL and TOKEN are required');

const accepted = new Counter('ingest_accepted');
const rateLimited = new Counter('ingest_rate_limited');
const acceptLatency = new Trend('ingest_accept_latency', true);

export const options = {
  scenarios: {
    sustained: { executor: 'constant-arrival-rate', rate: baseline, timeUnit: '1s', duration: '10m', preAllocatedVUs: Math.max(10, baseline * 2), tags: { phase: 'sustained' } },
    burst: { executor: 'constant-arrival-rate', rate: baseline * 3, timeUnit: '1s', duration: '2m', startTime: '10m', preAllocatedVUs: Math.max(20, baseline * 6), tags: { phase: 'burst' } },
  },
  thresholds: {
    'ingest_accept_latency{phase:sustained}': ['p(95)<250'],
    'http_req_failed{phase:sustained}': ['rate<0.001'],
    'checks{phase:sustained}': ['rate>0.999'],
    'http_req_duration{phase:burst}': ['p(95)<1000'],
  },
};

function payload(i) {
  const alert = `load-${__VU}-${i % 50}`; // 50 distinct conditions per VU: repeats exercise the episode path, not only opens
  return JSON.stringify({
    eventType: i % 7 === 0 ? 'resolved' : 'firing',
    alertId: alert,
    eventId: `${__VU}-${__ITER}-${Date.now()}`,
    occurredAt: new Date().toISOString(),
    severity: ['low', 'medium', 'high', 'critical'][i % 4],
    environment: 'loadtest',
    service: `svc-${i % 12}`,
    resource: { id: `res-${alert}`, name: `Resource ${alert}` },
    rule: { id: 'load', name: 'load rule' },
    summary: `load test ${alert}`,
  });
}

export default function () {
  const res = http.post(url, payload(__ITER), { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, tags: { name: 'ingest' } });
  if (res.status === 202) {
    accepted.add(1);
    acceptLatency.add(res.timings.duration);
  } else if (res.status === 429) {
    rateLimited.add(1);
  }
  check(res, { 'accepted or rate-limited': (r) => r.status === 202 || r.status === 429, 'no server error': (r) => r.status < 500 });
}

export function handleSummary(data) {
  const line = (m) => (data.metrics[m] ? JSON.stringify(data.metrics[m].values) : 'n/a');
  return {
    stdout: `\naccepted: ${line('ingest_accepted')}\nrate-limited: ${line('ingest_rate_limited')}\naccept latency: ${line('ingest_accept_latency')}\n`,
    'build/loadtest/last-run.json': JSON.stringify(data, null, 2),
  };
}
