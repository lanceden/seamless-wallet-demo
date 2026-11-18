// 情境：同一個 txnId 由 50 個 VU 同時送出。
// 通過條件：只有 1 筆 BET 帳變、只扣一次款；50 個回應內容一致（code / balance / txnId / timestamp 都和那唯一一筆帳變吻合）。
import { check } from 'k6';
import { signedPost, adminGet, ensureProvider, createPlayer, invariantsOk, trendStats } from './lib.js';

export const options = {
  scenarios: {
    storm: { executor: 'shared-iterations', vus: 50, iterations: 50, maxDuration: '1m' },
  },
  thresholds: {
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
  },
  summaryTrendStats: trendStats,
};

const INITIAL = 1_000_000;
const AMOUNT = 5_000;

export function setup() {
  ensureProvider();
  const runId = Date.now().toString(36);
  const playerId = `storm-${runId}`;
  createPlayer(playerId, INITIAL);
  return { playerId, txnId: `T-storm-${runId}`, roundId: `R-storm-${runId}` };
}

// "2026-11-18T04:50:02.823397Z" → unix 秒（只取到秒，避免 JS 對 6 位小數的解析差異）
function epoch(iso) {
  return Math.floor(Date.parse(iso.substring(0, 19) + 'Z') / 1000);
}

export default function (data) {
  const res = signedPost('/api/v1/wallet/bet', {
    playerId: data.playerId, txnId: data.txnId, roundId: data.roundId, gameCode: 'SLOT01', amount: AMOUNT,
  });
  const body = res.json();

  // 該玩家的帳變此時只會有 2 筆：加值 + 那唯一一筆下注。每個 VU 都拿自己的回應去比對那筆帳變。
  const tx = adminGet(`/admin/players/${data.playerId}/transactions?pageSize=50`).json();
  const betRows = tx.items.filter((i) => i.type === 'BET');

  check(body, {
    'code is OK': (b) => b.code === 'OK',
    'balance is charged exactly once': (b) => b.balance === INITIAL - AMOUNT,
    'txnId echoed': (b) => b.txnId === data.txnId,
  });
  check(betRows, {
    'exactly one BET ledger row': (rows) => rows.length === 1,
    'response timestamp equals the ledger row time': (rows) => rows.length === 1 && body.timestamp === epoch(rows[0].createdAt),
    'ledger balanceAfter equals response balance': (rows) => rows.length === 1 && rows[0].balanceAfter === body.balance,
  });
}

export function teardown() {
  check(invariantsOk(), { 'accounting invariants hold': (ok) => ok === true });
}
