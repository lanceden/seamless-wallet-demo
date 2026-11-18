// k6 共用函式：HMAC 簽章呼叫、後台輔助。
import crypto from 'k6/crypto';
import http from 'k6/http';

export const BASE = __ENV.BASE_URL || 'http://wallet-api:8080';
export const PROVIDER = __ENV.PROVIDER || 'K6';
export const SECRET = __ENV.SECRET || 'k6-secret-key';

const JSON_HEADERS = { 'Content-Type': 'application/json' };

export function signedPost(path, bodyObj, tags) {
  const raw = JSON.stringify(bodyObj);
  const ts = Math.floor(Date.now() / 1000).toString();
  const sig = crypto.hmac('sha256', SECRET, ts + '.' + raw, 'hex');
  return http.post(BASE + path, raw, {
    headers: { ...JSON_HEADERS, 'X-Provider-Code': PROVIDER, 'X-Timestamp': ts, 'X-Signature': sig },
    tags,
  });
}

export function adminPost(path, bodyObj, expected) {
  const params = { headers: JSON_HEADERS };
  if (expected) params.responseCallback = http.expectedStatuses(...expected);
  return http.post(BASE + path, JSON.stringify(bodyObj), params);
}

export function adminGet(path) {
  return http.get(BASE + path);
}

/** 建立壓測用遊戲商（已存在回 409，視為成功）。 */
export function ensureProvider() {
  const r = adminPost('/admin/providers', { code: PROVIDER, secret: SECRET }, [201, 409]); // 409 = 已存在，不算失敗
  if (r.status !== 201 && r.status !== 409) throw new Error('ensureProvider failed: ' + r.status + ' ' + r.body);
}

export function createPlayer(externalId, balance) {
  const r = adminPost('/admin/players', { externalId });
  if (r.status !== 201) throw new Error('create player failed: ' + r.status + ' ' + r.body);
  if (balance > 0) {
    const d = adminPost(`/admin/players/${externalId}/deposit`, { amount: balance });
    if (d.status !== 200) throw new Error('deposit failed: ' + d.status + ' ' + d.body);
  }
}

export function invariantsOk() {
  const r = adminGet('/admin/health/invariants');
  return r.status === 200 && r.json('ok') === true;
}

export function balanceOf(playerId) {
  return signedPost('/api/v1/wallet/balance', { playerId }).json('balance');
}

export const trendStats = ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'];
