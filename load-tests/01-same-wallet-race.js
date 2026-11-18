// 情境：1 個錢包餘額 10,000 元（1,000,000 分），1,000 個 VU 同時各下注 100 元（10,000 分）。
// 通過條件：剛好 100 筆成功、900 筆餘額不足；餘額 = 0；帳務不變式成立。
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { signedPost, ensureProvider, createPlayer, balanceOf, invariantsOk, trendStats } from './lib.js';

const betOk = new Counter('bet_ok');
const betInsufficient = new Counter('bet_insufficient');
const betOther = new Counter('bet_other');

export const options = {
  scenarios: {
    race: { executor: 'shared-iterations', vus: 1000, iterations: 1000, maxDuration: '2m' },
  },
  thresholds: {
    bet_ok: ['count==100'],
    bet_insufficient: ['count==900'],
    bet_other: ['count==0'],
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
  },
  summaryTrendStats: trendStats,
};

export function setup() {
  ensureProvider();
  const runId = Date.now().toString(36);
  const playerId = `race-${runId}`;
  createPlayer(playerId, 1_000_000);
  return { runId, playerId };
}

export default function (data) {
  const res = signedPost('/api/v1/wallet/bet', {
    playerId: data.playerId,
    txnId: `T-${data.runId}-${__VU}-${__ITER}`,
    roundId: `R-${data.runId}-${__VU}-${__ITER}`,
    gameCode: 'SLOT01',
    amount: 10_000,
  });
  const code = res.status === 200 ? res.json('code') : `HTTP_${res.status}`;
  if (code === 'OK') betOk.add(1);
  else if (code === 'INSUFFICIENT_BALANCE') betInsufficient.add(1);
  else betOther.add(1);
  check(res, { 'HTTP 200': (r) => r.status === 200 });
}

export function teardown(data) {
  check(balanceOf(data.playerId), { 'final balance is exactly 0': (b) => b === 0 });
  check(invariantsOk(), { 'accounting invariants hold': (ok) => ok === true });
}
