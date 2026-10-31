import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, writeFile, mkdir, rm, access } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { createHotRouter } from '../hot-router.mjs';

const ROUTER_ROOT = join(process.env.USERPROFILE, '.codex', 'model-router-sandbox');
const KEY = 'synthetic-real-engine-key';

async function realEnginePath(t) {
  const bundled=fileURLToPath(new URL('../router-engine.mjs',import.meta.url));
  try { await access(bundled); return bundled; } catch { /* Local development may use the installed engine. */ }
  for (const name of ['router-control-center-engine.mjs', 'router.mjs']) {
    const candidate = join(ROUTER_ROOT, name);
    try { await access(candidate); return candidate; } catch { /* try the next one */ }
  }
  t.skip('A supported local router engine is not installed.');
  return null;
}

/// <summary>Mock upstream that answers the slow model later than the fast one.</summary>
async function mockUpstream() {
  const received = [];
  const server = createServer((request, response) => {
    const chunks = [];
    request.on('data', data => chunks.push(data));
    request.on('end', () => {
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      received.push({ model: body.model, authorization: request.headers.authorization, tools: body.tools, input: body.input });
      // Simulate gateways that register legacy RLite declarations together with
      // current standard Responses tools, then reject a duplicated identity.
      if (Array.isArray(body.tools) && body.input?.some?.(item=>item.type==='additional_tools')) {
        response.writeHead(400,{'content-type':'application/json'});
        response.end(JSON.stringify({error:{message:'Invalid request: duplicate tool name "mcp__cua_repl__js"'}}));return;
      }
      const delay = body.model === 'slow-model' ? 400 : 10;
      setTimeout(() => {
        response.writeHead(200, { 'content-type': 'application/json' });
        const output = body.tools?.some(tool => tool.type === 'custom' && tool.name === 'exec')
          ? [{ type: 'function_call', name: 'exec', id: 'fc_mock', call_id: 'call_mock', arguments: JSON.stringify({ input: 'text("synthetic tool handshake");' }) }]
          : [];
        response.end(JSON.stringify({ id: 'local-fixture', model: body.model, output }));
      }, delay);
    });
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return { server, received };
}

async function fixture(t, enginePath) {
  const root = await mkdtemp(join(tmpdir(), 'cc-real-engine-'));
  const configPath = join(root, 'router.json');
  const tokenPath = join(root, 'router.token');
  await mkdir(join(root, 'secrets'), { recursive: true });
  const token = 'synthetic-local-token-abcdefghijklmnopqrstuvwxyz';
  await writeFile(tokenPath, token);
  const upstream = await mockUpstream();
  const config = {
    host: '127.0.0.1', port: 49781, auth_token_file: tokenPath,
    providers: { alpha: { base_url: `http://127.0.0.1:${upstream.server.address().port}/v1`, key_env: 'CC_REAL_ENGINE_KEY' } },
    routes: {
      'fast-alias': { provider: 'alpha', model: 'fast-model' },
      'slow-alias': { provider: 'alpha', model: 'slow-model' },
    },
  };
  await writeFile(configPath, JSON.stringify(config));

  const events = [];
  const server = await createHotRouter({
    configPath, codexHome: root, enginePath,
    credentialLoader: async () => ({ credentials: { CC_REAL_ENGINE_KEY: KEY }, unavailable: [], env_fingerprint: 'fingerprint-1' }),
    observe: { writeEvent: event => events.push(event) },
  });
  await new Promise(ready => server.listen(0, '127.0.0.1', ready));
  const url = `http://127.0.0.1:${server.address().port}`;
  t.after(async () => {
    server.closeAllConnections();
    await new Promise(ready => server.close(ready));
    upstream.server.closeAllConnections();
    await new Promise(ready => upstream.server.close(ready));
    await rm(root, { recursive: true, force: true });
  });
  const call = async alias => {
    const response = await fetch(url + '/v1/responses', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-local-router-key': token },
      body: JSON.stringify({ model: alias, input: 'local fixture' }),
    });
    return { status: response.status, body: await response.json().catch(() => null) };
  };
  return { url, call, events, upstream, token };
}

const settle = async (events, count, timeoutMs = 8000) => {
  const deadline = Date.now() + timeoutMs;
  while (events.length < count && Date.now() < deadline) {
    await new Promise(ready => setTimeout(ready, 25));
  }
};

test('real engine and hot wrapper repair the observed DeepSeek custom tool mismatch', async t => {
  const enginePath = await realEnginePath(t);
  if (!enginePath) return;
  const f = await fixture(t, enginePath);
  const response = await fetch(f.url + '/v1/responses', {
    method: 'POST', headers: { 'content-type': 'application/json', 'x-local-router-key': f.token },
    body: JSON.stringify({ model: 'fast-alias', input: 'synthetic tool handshake', tools: [{ type: 'custom', name: 'exec' }] }),
  });
  assert.equal(response.status, 200);
  const result = await response.json();
  assert.deepEqual(result.output[0], { type: 'custom_tool_call', name: 'exec', id: 'fc_mock', call_id: 'call_mock', input: 'text("synthetic tool handshake");' });
  assert.equal((await (await fetch(f.url + '/health')).json()).features.custom_tool_compat, true);
});

test('real installed engine deduplicates tool definitions and rejects conflicts before upstream', async t => {
  const enginePath = await realEnginePath(t); if (!enginePath) return;
  const f = await fixture(t, enginePath);
  const tool = { type: 'function', name: 'collaboration__followup_task', parameters: { type: 'object', properties: { message: { type: 'string' } } } };
  const call = tools => fetch(f.url + '/v1/responses', {
    method: 'POST', headers: { 'content-type': 'application/json', 'x-local-router-key': f.token },
    body: JSON.stringify({ model: 'fast-alias', input: 'synthetic duplicate-name reproduction', tools }),
  });
  assert.equal((await call([tool, structuredClone(tool)])).status, 200);
  assert.deepEqual(f.upstream.received.at(-1).tools, [tool]);
  const calls = f.upstream.received.length;
  const rejected = await call([tool, { ...tool, parameters: { type: 'object', properties: { id: { type: 'number' } } } }]);
  assert.equal(rejected.status, 400);
  assert.equal((await rejected.json()).error.code, 'tool_definition_conflict');
  assert.equal(f.upstream.received.length, calls);
  assert.equal((await (await fetch(f.url + '/health')).json()).features.tool_definition_guard, true);
  assert.deepEqual((await (await fetch(f.url + '/health')).json()).runtime.tool_definition_guard,
    { deduplicated_requests: 1, duplicates_removed: 1, conflicting_requests: 1 });
});

test('real engine survives GPT to MiniMax switch with an old AdditionalTools catalog',async t=>{
  const enginePath=await realEnginePath(t);if(!enginePath)return;
  const f=await fixture(t,enginePath);
  const tool={type:'function',name:'js',parameters:{type:'object',properties:{code:{type:'string'}}}};
  const namespace={type:'namespace',name:'mcp__cua_repl',tools:[tool]};
  const message={type:'message',role:'user',content:[{type:'input_text',text:'synthetic continued task'}]};
  const call={type:'function_call',name:'mcp__cua_repl__js',call_id:'existing',arguments:'{"code":"synthetic"}'};
  const result={type:'function_call_output',call_id:'existing',output:'synthetic prior result'};
  const response=await fetch(f.url+'/v1/responses',{method:'POST',headers:{'content-type':'application/json','x-local-router-key':f.token},body:JSON.stringify({
    model:'fast-alias',tools:[namespace],input:[message,{type:'additional_tools',role:'developer',tools:[namespace]},call,result]
  })});
  assert.equal(response.status,200);await response.json();
  assert.deepEqual(f.upstream.received.at(-1).tools,[namespace]);
  assert.deepEqual(f.upstream.received.at(-1).input,[message,call,result]);
  const health=await (await fetch(f.url+'/health')).json();
  assert.equal(health.runtime.tool_structure.legacy_tool_items_removed,1);
  assert.equal(health.runtime.tool_structure.last.legacy_active_name_overlap,1);
});

test('sequential requests keep their own model metadata with the real engine', async t => {
  const enginePath = await realEnginePath(t);
  if (!enginePath) return;
  const f = await fixture(t, enginePath);

  const first = await f.call('fast-alias');
  assert.equal(first.status, 200);
  assert.equal(first.body.model, 'fast-model');
  const second = await f.call('slow-alias');
  assert.equal(second.status, 200);
  assert.equal(second.body.model, 'slow-model');

  await settle(f.events, 2);
  assert.equal(f.events.length, 2);
  assert.deepEqual(f.events.map(event => event.model), ['fast-alias', 'slow-alias']);
  assert.deepEqual(f.events.map(event => event.provider), ['alpha', 'alpha']);
  assert.ok(f.events.every(event => event.status === 200 && event.cancelled === false));
});

test('concurrent requests that finish out of order never exchange their metadata', async t => {
  const enginePath = await realEnginePath(t);
  if (!enginePath) return;
  const f = await fixture(t, enginePath);

  // The fast alias answers first even though the slow one was requested first.
  const slow = f.call('slow-alias');
  const fast = f.call('fast-alias');
  const [slowResult, fastResult] = await Promise.all([slow, fast]);
  assert.equal(slowResult.status, 200);
  assert.equal(fastResult.status, 200);
  assert.equal(slowResult.body.model, 'slow-model');
  assert.equal(fastResult.body.model, 'fast-model');

  await settle(f.events, 2);
  assert.equal(f.events.length, 2);
  // The fast request is recorded first because it completed first, and each record keeps its own alias.
  assert.deepEqual(f.events.map(event => event.model), ['fast-alias', 'slow-alias']);
  assert.ok(f.events.every(event => event.provider === 'alpha'));
  // The upstream saw both models with the configured credential.
  assert.deepEqual(f.upstream.received.map(entry => entry.model).sort(), ['fast-model', 'slow-model']);
  assert.ok(f.upstream.received.every(entry => entry.authorization === `Bearer ${KEY}`));
});

test('a cancelled request keeps its cancellation state and its own model', async t => {
  const enginePath = await realEnginePath(t);
  if (!enginePath) return;
  const f = await fixture(t, enginePath);

  const controller = new AbortController();
  const pending = fetch(f.url + '/v1/responses', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-local-router-key': f.token },
    body: JSON.stringify({ model: 'slow-alias', input: 'local fixture' }),
    signal: controller.signal,
  });
  // The mock upstream answers the slow model after 400 ms, so the client disconnects while the
  // request is still in flight.
  await new Promise(ready => setTimeout(ready, 120));
  controller.abort();
  await pending.catch(() => {});

  await settle(f.events, 1);
  assert.equal(f.events.length, 1);
  assert.equal(f.events[0].cancelled, true);
  assert.equal(f.events[0].status, 499);
  assert.equal(f.events[0].model, 'slow-alias');
  assert.equal(f.events[0].provider, 'alpha');
});
