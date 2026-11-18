// 情境：1 萬個錢包，隨機下注與派彩，持續 5 分鐘（可用 DURATION / VUS 環境變數調整）。
// 記錄：TPS（http_reqs/s）、P95、P99、錯誤率；結束後帳務不變式必須成立。
import http from 'k6/http';
import { check } from 'k6';
import { signedPost, ensureProvider, adminPost, invariantsOk, trendStats, BASE } from './lib.js';

const WALLETS = parseInt(__ENV.WALLETS || '10000');
const INITIAL = 1_000_000; // 每個錢包 10,000 元

export const options = {
  scenarios: {
    mixed: { executor: 'constant-vus', vus: parseInt(__ENV.VUS || '128'), duration: __ENV.DURATION || '5m' },
  },
  setupTimeout: '10m',
  thresholds: {
    http_req_failed: ['rate<0.001'],
    checks: ['rate>0.999'],
    // 以 endpoint 標籤拆出子指標，讓摘要只含 bet / settle（不混入 setup 的後台請求）；門檻寬鬆，目的是取得數字
    'http_req_duration{endpoint:bet}': ['p(99)<2000'],
    'http_req_duration{endpoint:settle}': ['p(99)<2000'],
    'http_reqs{endpoint:bet}': ['count>=0'],
    'http_reqs{endpoint:settle}': ['count>=0'],
  },
  summaryTrendStats: trendStats,
};

export function setup() {
  ensureProvider();
  const runId = Date.now().toString(36);
  const headers = { 'Content-Type': 'application/json' };
  // 分批並行建立錢包與加值
  for (let start = 0; start < WALLETS; start += 200) {
    const creates = [];
    for (let i = start; i < Math.min(start + 200, WALLETS); i++)
      creates.push(['POST', `${BASE}/admin/players`, JSON.stringify({ externalId: `tp-${runId}-${i}` }), { headers }]);
    http.batch(creates);
    const deposits = [];
    for (let i = start; i < Math.min(start + 200, WALLETS); i++)
      deposits.push(['POST', `${BASE}/admin/players/tp-${runId}-${i}/deposit`, JSON.stringify({ amount: INITIAL }), { headers }]);
    const rs = http.batch(deposits);
    for (const r of rs) if (r.status !== 200) throw new Error('setup deposit failed: ' + r.status);
  }
  return { runId };
}

export default function (data) {
  const player = `tp-${data.runId}-${Math.floor(Math.random() * WALLETS)}`;
  const amount = 100 * (1 + Math.floor(Math.random() * 50));
  const id = `${data.runId}-${__VU}-${__ITER}`;

  const bet = signedPost('/api/v1/wallet/bet',
    { playerId: player, txnId: `B-${id}`, roundId: `R-${id}`, gameCode: 'SLOT01', amount },
    { endpoint: 'bet' });
  const betOk = check(bet, { 'bet 200': (r) => r.status === 200, 'bet OK': (r) => r.json('code') === 'OK' });
  if (!betOk) return;

  const payout = Math.floor((amount * Math.floor(Math.random() * 7)) / 2); // 0 ~ 3 倍
  const settle = signedPost('/api/v1/wallet/settle',
    { playerId: player, txnId: `S-${id}`, roundId: `R-${id}`, amount: payout },
    { endpoint: 'settle' });
  check(settle, { 'settle 200': (r) => r.status === 200, 'settle OK': (r) => r.json('code') === 'OK' });
}

export function teardown() {
  check(invariantsOk(), { 'accounting invariants hold': (ok) => ok === true });
}
