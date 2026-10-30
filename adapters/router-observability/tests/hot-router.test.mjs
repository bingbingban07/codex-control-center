import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, writeFile, mkdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createHotRouter } from '../hot-router.mjs';

async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), 'cc-hot-router-'));
  const configPath = join(root, 'router.json');
  const tokenPath = join(root, 'router.token');
  const enginePath = join(root, 'engine.mjs');
  await mkdir(join(root, 'secrets'));
  await writeFile(tokenPath, 'synthetic-router-token-abcdefghijklmnopqrstuvwxyz');
  await writeFile(enginePath, `
    import http from 'node:http';
    export async function createRouter(config, { env }) {
      if (config.fail) throw new Error('NEVER_LOG_SYNTHETIC_SECRET');
      const loaded = { name: config.name, key: env.CC_TEST_KEY };
      return http.createServer((req, res) => {
        let body = ''; req.on('data', x => body += x);
        req.on('end', () => {
          setTimeout(() => { res.setHeader('content-type','application/json');
            res.end(JSON.stringify({ name: loaded.name, keyValid: loaded.key === config.expectedKey, body }));
          }, req.url === '/slow' ? 200 : 0);
        });
      });
    }
  `);
  let credential = 'synthetic-a', failCredentials = false;
  let config = { host: '127.0.0.1', port: 49741, auth_token_file: tokenPath, name: 'old', expectedKey: credential,
    providers: { test: { key_env: 'CC_TEST_KEY' } } };
  const save = async updates => { config = { ...config, ...updates }; await writeFile(configPath, JSON.stringify(config)); };
  await save({});
  const logs = [];
  const server = await createHotRouter({ configPath, codexHome: root, enginePath, logger: line => logs.push(line),
    credentialLoader: async () => { if (failCredentials) throw new Error('credential_load_failed'); return { CC_TEST_KEY: credential }; } });
  await new Promise(accept => server.listen(0, '127.0.0.1', accept));
  const url = `http://127.0.0.1:${server.address().port}`;
  t.after(async () => { await new Promise(accept => { server.closeAllConnections(); server.close(accept); }); await rm(root, { recursive: true, force: true }); });
  return { root, configPath, tokenPath, server, logs, save, url,
    credential: value => { credential = value; }, failCredentials: value => { failCredentials = value; } };
}

test('changed routes load before POST; ongoing request keeps its original generation', async t => {
  const f = await fixture(t);
  const slow = fetch(f.url + '/slow', { method: 'POST', body: 'old-body' });
  // Wait for acceptance by observing the active request count, not a timing assumption.
  for (let i = 0; i < 100; i++) {
    const health = await (await fetch(f.url + '/health')).json();
    if (health.runtime.active_requests === 1) break;
    await new Promise(accept => setTimeout(accept, 2));
  }
  await f.save({ name: 'new' });
  const result = await (await fetch(f.url + '/v1/responses', { method: 'POST', body: '完整请求正文' })).json();
  assert.equal(result.name, 'new'); assert.equal(result.keyValid, true); assert.equal(result.body, '完整请求正文');
  assert.equal((await (await slow).json()).name, 'old');
  assert.equal((await (await fetch(f.url + '/health')).json()).runtime.reload_error, null);
});

test('invalid updates stop new calls, retain ongoing generation, recover without restarting', async t => {
  const f = await fixture(t);
  await f.save({ fail: true });
  assert.equal((await fetch(f.url + '/v1/responses', { method: 'POST', body: '{}' })).status, 503);
  const status = await (await fetch(f.url + '/health')).json();
  assert.equal(status.runtime.reload_error, 'configuration_reload_failed');
  assert.ok(!JSON.stringify(status).includes('NEVER_LOG'));
  assert.ok(!f.logs.join('').includes('NEVER_LOG'));
  await f.save({ fail: false, name: 'recovered' });
  assert.equal((await (await fetch(f.url + '/v1/responses')).json()).name, 'recovered');
});

test('encrypted credential change reloads; failure never spends with the previous key', async t => {
  const f = await fixture(t);
  f.credential('synthetic-b');
  await f.save({ expectedKey: 'synthetic-b' });
  await writeFile(join(f.root, 'secrets/control-center-test.dpapi'), 'synthetic encrypted revision');
  assert.equal((await (await fetch(f.url + '/v1/responses')).json()).keyValid, true);
  f.failCredentials(true);
  await writeFile(join(f.root, 'secrets/control-center-test.dpapi'), 'different longer revision');
  assert.equal((await fetch(f.url + '/v1/responses')).status, 503);
  assert.equal((await (await fetch(f.url + '/health')).json()).runtime.reload_error, 'credential_load_failed');
  f.failCredentials(false);
  assert.equal((await fetch(f.url + '/v1/responses')).status, 200);
});

test('local token and listener changes require restart rather than silently changing authentication', async t => {
  const f = await fixture(t);
  await writeFile(f.tokenPath, 'different-synthetic-router-token-abcdefghijklmnopqrstuvwxyz');
  assert.equal((await fetch(f.url + '/v1/responses')).status, 503);
  assert.equal((await (await fetch(f.url + '/health')).json()).runtime.reload_error, 'token_change_requires_restart');
  await f.save({ port: 49742 });
  assert.equal((await fetch(f.url + '/v1/responses')).status, 503);
  assert.equal((await (await fetch(f.url + '/health')).json()).runtime.reload_error, 'listener_or_token_change_requires_restart');
});
