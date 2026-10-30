import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, writeFile, mkdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createHotRouter } from '../hot-router.mjs';

const ALPHA_KEY = 'synthetic-alpha-key';
const BETA_KEY = 'synthetic-beta-key';

// Synthetic engine: it resolves the route from the request body, checks the credential that was
// loaded for the provider and counts how many requests would have reached an upstream.
const ENGINE_SOURCE = `
import http from 'node:http';
export async function createRouter(config, { env, logger }) {
  const providers = config.providers || {};
  const routes = config.routes || {};
  const calls = [];
  const server = http.createServer((request, response) => {
    const chunks = [];
    request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      let model = null;
      try { model = JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}').model; } catch {}
      const route = model ? routes[model] : null;
      const providerId = route ? route.provider : null;
      const provider = providerId ? providers[providerId] : null;
      if (!provider) {
        calls.push({ model, upstream: false });
        logger(JSON.stringify({ model_alias: model, provider: providerId, status: 404, upstream: false }));
        response.writeHead(404, { 'content-type': 'application/json' });
        response.end(JSON.stringify({ error: { message: 'model is not routable' } }));
        return;
      }
      calls.push({ model, upstream: true });
      logger(JSON.stringify({ model_alias: model, provider: providerId, status: 200, upstream: true }));
      response.writeHead(200, { 'content-type': 'application/json' });
      response.end(JSON.stringify({ model, provider: providerId, keyValid: env[provider.key_env] === provider.expected_key }));
    });
  });
  server.upstreamCalls = calls;
  return server;
}
`;

async function fixture(t, options = {}) {
  const root = await mkdtemp(join(tmpdir(), 'cc-isolation-'));
  const configPath = join(root, 'router.json');
  const tokenPath = join(root, 'router.token');
  const enginePath = join(root, 'engine.mjs');
  await mkdir(join(root, 'secrets'), { recursive: true });
  await writeFile(tokenPath, 'synthetic-router-token-abcdefghijklmnopqrstuvwxyz');
  await writeFile(enginePath, ENGINE_SOURCE);
  const config = {
    host: '127.0.0.1', port: 49761, auth_token_file: tokenPath,
    providers: {
      alpha: { key_env: 'CC_ALPHA', base_url: 'http://127.0.0.1:1/v1', expected_key: ALPHA_KEY },
      beta: { key_env: 'CC_BETA', base_url: 'http://127.0.0.1:2/v1', expected_key: BETA_KEY },
    },
    routes: {
      'model-alpha': { provider: 'alpha', model: 'upstream-alpha' },
      'model-beta': { provider: 'beta', model: 'upstream-beta' },
    },
  };
  const save = async updates => {
    Object.assign(config, updates);
    config.revision = (config.revision ?? 0) + 1;
    await writeFile(configPath, JSON.stringify(config));
  };
  await save({});

  const state = {
    credentials: options.credentials ?? { CC_ALPHA: ALPHA_KEY, CC_BETA: BETA_KEY },
    unavailable: options.unavailable ?? [],
    envFingerprint: options.envFingerprint ?? 'fingerprint-1',
    probeFingerprint: options.probeFingerprint ?? 'fingerprint-1',
    probeCount: 0,
    loadCount: 0,
  };
  const events = [];
  const logs = [];
  const server = await createHotRouter({
    configPath, codexHome: root, enginePath,
    credentialCheckIntervalMs: options.credentialCheckIntervalMs ?? 40,
    credentialLoader: async () => {
      state.loadCount++;
      return { credentials: { ...state.credentials }, unavailable: state.unavailable.map(entry => ({ ...entry })), env_fingerprint: state.envFingerprint };
    },
    credentialProbe: async () => { state.probeCount++; return { env_fingerprint: state.probeFingerprint }; },
    logger: line => logs.push(line),
    observe: { writeEvent: event => events.push(event) },
  });
  await new Promise(ready => server.listen(0, '127.0.0.1', ready));
  const url = `http://127.0.0.1:${server.address().port}`;
  t.after(async () => { server.closeAllConnections(); await new Promise(ready => server.close(ready)); await rm(root, { recursive: true, force: true }); });
  const call = async model => {
    const response = await fetch(url + '/v1/responses', { method: 'POST', body: JSON.stringify({ model, input: 'local fixture' }) });
    return { status: response.status, body: await response.json().catch(() => null) };
  };
  const health = async () => (await (await fetch(url + '/health')).json()).runtime;
  const upstreamCalls = () => logs.filter(line => {
    try { return JSON.parse(line).upstream === true; } catch { return false; }
  });
  return { root, configPath, url, server, state, events, logs, upstreamCalls, call, health, save };
}

test('one unusable source only disables that source and never removes the healthy one', async t => {
  const f = await fixture(t, {
    credentials: { CC_ALPHA: ALPHA_KEY },
    unavailable: [{ id: 'beta', reason: 'credential_unavailable' }],
  });

  const runtime = await f.health();
  assert.equal(runtime.credential_status, 'degraded');
  assert.deepEqual(runtime.unavailable_providers, [{ id: 'beta', reason: 'credential_unavailable' }]);
  assert.equal(runtime.providers_total, 2);
  assert.equal(runtime.providers_ready, 1);
  assert.equal(runtime.reload_error, null);
  assert.equal(runtime.config_synced, true);
  assert.ok(typeof runtime.credential_checked_at === 'string' && runtime.credential_checked_at.length > 0);

  // The healthy source still works with its own credential.
  const healthy = await f.call('model-alpha');
  assert.equal(healthy.status, 200);
  assert.equal(healthy.body.keyValid, true);
  assert.equal(healthy.body.provider, 'alpha');

  // The unusable source is dropped with its route, so no request can reach an upstream for it.
  const broken = await f.call('model-beta');
  assert.notEqual(broken.status, 200);
  assert.equal(f.upstreamCalls().filter(call => call.model === 'model-beta').length, 0);

  // The health payload never exposes a credential value.
  const serialized = JSON.stringify(await (await fetch(f.url + '/health')).json());
  assert.ok(!serialized.includes(ALPHA_KEY));
  assert.ok(!serialized.includes('CC_ALPHA'));
});

test('a revoked credential replaces the running generation instead of keeping the old key', async t => {
  const f = await fixture(t);
  const first = await f.call('model-alpha');
  assert.equal(first.status, 200);
  assert.equal(first.body.keyValid, true);

  // The beta credential is revoked while the router keeps running.
  f.state.credentials = { CC_ALPHA: ALPHA_KEY };
  f.state.unavailable = [{ id: 'beta', reason: 'credential_unreadable' }];
  await f.save({});

  const runtime = await f.health();
  assert.equal(runtime.credential_status, 'degraded');
  assert.equal(runtime.unavailable_providers[0].reason, 'credential_unreadable');

  const alpha = await f.call('model-alpha');
  assert.equal(alpha.status, 200);
  assert.equal(alpha.body.keyValid, true);
  // The revoked source is not served with the key of the previous generation.
  const beta = await f.call('model-beta');
  assert.notEqual(beta.status, 200);
  assert.equal(f.upstreamCalls().filter(call => call.model === 'model-beta').length, 0);
});

test('an environment sourced credential change is picked up by the controlled probe', async t => {
  const f = await fixture(t, {
    credentials: { CC_ALPHA: ALPHA_KEY },
    unavailable: [],
    envFingerprint: 'fingerprint-1',
    probeFingerprint: 'fingerprint-1',
  });
  const before = await f.call('model-alpha');
  assert.equal(before.body.keyValid, true);
  assert.ok(f.state.probeCount >= 0);

  // The operator rotates the environment credential; only the digest changes.
  f.state.credentials = { CC_ALPHA: 'synthetic-alpha-key-rotated' };
  f.state.probeFingerprint = 'fingerprint-2';

  const deadline = Date.now() + 5000;
  let rotated = null;
  while (Date.now() < deadline) {
    await new Promise(ready => setTimeout(ready, 25));
    const current = await f.call('model-alpha');
    if (current.status === 200 && current.body.keyValid === false) { rotated = current; break; }
  }
  assert.ok(rotated, 'the rotated environment credential was not applied');
  assert.ok(f.state.probeCount > 0, 'the environment credential probe never ran');
  assert.equal(f.state.loadCount >= 2, true, 'the router did not reload after the fingerprint changed');
});

test('health distinguishes configuration sync from the credential check', async t => {
  const f = await fixture(t, {
    credentials: {},
    unavailable: [{ id: 'alpha', reason: 'credential_unavailable' }, { id: 'beta', reason: 'credential_unavailable' }],
  });
  const runtime = await f.health();
  assert.equal(runtime.credential_status, 'unavailable');
  assert.equal(runtime.providers_ready, 0);
  assert.equal(runtime.unavailable_providers.length, 2);

  // Without a usable source the router still answers, but never calls an upstream.
  const response = await fetch(f.url + '/v1/responses', { method: 'POST', body: JSON.stringify({ model: 'model-alpha' }) });
  assert.equal(response.status, 503);
  const payload = await response.json();
  assert.equal(payload.error.code, 'no_usable_credential');
  assert.equal(payload.error.unavailable_providers.length, 2);
  assert.equal(f.upstreamCalls().length, 0);
});

test('probe traffic is not recorded and a real request keeps its model metadata', async t => {
  const f = await fixture(t);
  await fetch(f.url + '/health');
  await fetch(f.url + '/v1/models');
  await new Promise(ready => setTimeout(ready, 60));
  assert.equal(f.events.length, 0, 'probe traffic was recorded as a model request');

  await f.call('model-alpha');
  await new Promise(ready => setTimeout(ready, 60));
  assert.equal(f.events.length, 1);
  assert.equal(f.events[0].model, 'model-alpha');
  assert.equal(f.events[0].provider, 'alpha');
  assert.equal(f.events[0].cancelled, false);
  assert.ok(!JSON.stringify(f.events[0]).includes(ALPHA_KEY));
});
