// 老虎機整條鏈路壓測：20 位玩家（每個 VU 一位，真實玩家一次只會有一局）各連轉 500 局 = 10,000 局。
// 每局都是：遊戲伺服器 → 錢包 bet → 伺服器端 RNG → 錢包 settle。結束後用 SQL 對 slot.rounds 與錢包 ledger（見 scripts/verify-slot-run.sh）。
import http from 'k6/http';
import { check } from 'k6';
import { adminPost, invariantsOk, trendStats, BASE } from './lib.js';

const GAME = __ENV.GAME_URL || 'http://slot-gameserver:8081';
const PLAYERS = parseInt(__ENV.PLAYERS || '20');
const SPINS = parseInt(__ENV.SPINS || '500');
const INITIAL = 5_000_000;
const BETS = [100, 200, 500, 1000];

export const options = {
  scenarios: { spins: { executor: 'per-vu-iterations', vus: PLAYERS, iterations: SPINS, maxDuration: '10m' } },
  thresholds: {
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
    'http_req_duration{endpoint:spin}': ['p(99)<1000'],
    'http_reqs{endpoint:spin}': ['count>=0'],
  },
  summaryTrendStats: trendStats,
};

export function setup() {
  const runId = Date.now().toString(36);
  const json = { 'Content-Type': 'application/json' };
  const tokens = [];
  for (let i = 0; i < PLAYERS; i++) {
    const id = `k6slot-${runId}-${i}`;
    if (adminPost('/admin/players', { externalId: id }).status !== 201) throw new Error('create player failed');
    if (adminPost(`/admin/players/${id}/deposit`, { amount: INITIAL }).status !== 200) throw new Error('deposit failed');
    const r = http.post(`${BASE}/admin/players/${id}/launch?provider=SLOT`, null, { headers: json });
    if (r.status !== 200) throw new Error('launch failed: ' + r.status + ' ' + r.body);
    tokens.push(r.json('token'));
  }
  return { runId, tokens };
}

export default function (data) {
  const token = data.tokens[__VU - 1];
  const res = http.post(`${GAME}/game/spin`, JSON.stringify({ token, lineBet: BETS[(__VU + __ITER) % BETS.length] }),
    { headers: { 'Content-Type': 'application/json' }, tags: { endpoint: 'spin' } });
  check(res, {
    'HTTP 200': (r) => r.status === 200,
    'code OK': (r) => r.json('code') === 'OK',
    'settled (no pending win)': (r) => r.json('settlePending') === false,
    'win never exceeds the 108x total bet maximum': (r) => r.json('totalWin') <= 108 * r.json('totalBet'),
  });
}

export function teardown(data) {
  check(invariantsOk(), { 'wallet accounting invariants hold': (ok) => ok === true });
  console.log('RUN_ID=' + data.runId);
}
