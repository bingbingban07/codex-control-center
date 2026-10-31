import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, readFile, writeFile, mkdir, rm, access } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { pathToFileURL } from 'node:url';
import { createHotRouter } from '../hot-router.mjs';

const exec = (file, args) => new Promise((accept, reject) => {
  execFile(file, args, { windowsHide: true, timeout: 30000 }, (error, stdout) => {
    if (error) reject(new Error('Isolated PowerShell fixture failed; output suppressed.'));
    else accept(stdout);
  });
});
const psArgs = script => ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script];

test('installed wrapper preserves engine, loads DPAPI via private pipe, switches credential and route', { skip: process.platform !== 'win32' }, async t => {
  const routerRoot = join(process.env.USERPROFILE, '.codex', 'model-router-sandbox');
  let engineFile = new URL('../router-engine.mjs', import.meta.url);
  try { await access(engineFile); } catch { engineFile = join(routerRoot, 'router-control-center-engine.mjs'); }
  try { await access(engineFile); } catch { engineFile = join(routerRoot, 'router.mjs'); }
  try { await access(engineFile); } catch { t.skip('A supported local router engine is not installed.'); return; }
  const root = await mkdtemp(join(tmpdir(), 'cc-installed-中文-'));
  const routerHome = join(root, 'router');
  const codexHome = join(root, 'codex');
  await mkdir(routerHome); await mkdir(join(codexHome, 'secrets'), { recursive: true });
  const engineSource = await readFile(engineFile, 'utf8');
  // Use the existing engine only as local source code; no production config, keys or requests.
  assert.ok(!engineSource.includes('CODEX_CONTROL_CENTER_HOT_RELOAD_V1'), 'Fixture must use the original engine');
  await writeFile(join(routerHome, 'router.mjs'), engineSource);
  const token = 'synthetic-local-token-abcdefghijklmnopqrstuvwxyz';
  await writeFile(join(root, 'synthetic.token'), token);
  const protect = join(root, 'protect.ps1');
  await writeFile(protect, `param([string]$Target,[string]$Revision)
    Add-Type -AssemblyName System.Security
    $bytes=[Text.Encoding]::UTF8.GetBytes('synthetic-credential-'+$Revision)
    try { $protected=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
      [IO.File]::WriteAllText($Target,[Convert]::ToBase64String($protected))
    } finally { [Array]::Clear($bytes,0,$bytes.Length) }
  `);
  const credentialPath = join(codexHome, 'secrets/control-center-alpha.dpapi');
  const secondCredentialPath = join(codexHome, 'secrets/control-center-beta.dpapi');
  await exec('powershell.exe', [...psArgs(protect), '-Target', credentialPath, '-Revision', 'one']);
  await exec('powershell.exe', [...psArgs(protect), '-Target', secondCredentialPath, '-Revision', 'one']);
  let expected = 'synthetic-credential-one', lastModel = null, authenticated = false;
  const upstreamCalls = [];
  const upstream = createServer((req, res) => {
    let body = ''; req.on('data', data => body += data);
    req.on('end', () => {
      authenticated = req.headers.authorization === 'Bearer ' + expected;
      const model = JSON.parse(body).model;
      lastModel = model;
      upstreamCalls.push({ model, authorization: req.headers.authorization });
      res.setHeader('content-type', 'application/json');
      res.end(JSON.stringify({ id: 'test-local-response', object: 'response', output: [] }));
    });
  });
  upstream.listen(0, '127.0.0.1'); await once(upstream, 'listening');
  const configPath = join(routerHome, 'router.json');
  const config = { host: '127.0.0.1', port: 49743, auth_token_file: join(root, 'synthetic.token'),
    providers: {
      alpha: { base_url: `http://127.0.0.1:${upstream.address().port}/v1`, key_env: 'CC_ISOLATED_ALPHA' },
      beta: { base_url: `http://127.0.0.1:${upstream.address().port}/v1`, key_env: 'CC_ISOLATED_BETA' },
    },
    routes: {
      original: { provider: 'alpha', model: 'original-model' },
      secondary: { provider: 'beta', model: 'secondary-model' },
    } };
  await writeFile(configPath, JSON.stringify(config));
  const install = resolve('scripts/install-router-runtime.ps1');
  const runInstaller = async () => JSON.parse(await exec('pwsh.exe', [...psArgs(install), '-RouterHome', routerHome, '-CodexHome', codexHome]));
  const installed = await runInstaller();
  assert.equal(installed.installed, true);
  assert.equal(await readFile(join(routerHome, 'router-control-center-engine.mjs'), 'utf8'), engineSource);
  await runInstaller(); // Idempotent upgrades preserve the underlying engine.
  assert.equal(await readFile(join(routerHome, 'router-control-center-engine.mjs'), 'utf8'), engineSource);
  const engine = await import(pathToFileURL(join(routerHome, 'router.mjs')).href);
  assert.equal(typeof engine.createRouter, 'function');
  const server = await createHotRouter({ configPath, codexHome, enginePath: join(routerHome, 'router.mjs') });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const url = `http://127.0.0.1:${server.address().port}`;
  t.after(async () => {
    server.closeAllConnections(); upstream.closeAllConnections();
    await Promise.all([new Promise(done => server.close(done)), new Promise(done => upstream.close(done))]);
    await rm(root, { recursive: true, force: true });
  });
  const call = model => fetch(url + '/v1/responses', { method: 'POST',
    headers: { 'content-type': 'application/json', 'x-local-router-key': token }, body: JSON.stringify({ model, input: 'local fixture' }) });
  assert.equal((await call('original')).status, 200); assert.equal(authenticated, true); assert.equal(lastModel, 'original-model');
  assert.equal((await call('secondary')).status, 200); assert.equal(lastModel, 'secondary-model');
  expected = 'synthetic-credential-two';
  await exec('powershell.exe', [...psArgs(protect), '-Target', credentialPath, '-Revision', 'two']);
  config.routes.added = { provider: 'alpha', model: 'new-model' };
  await writeFile(configPath, JSON.stringify(config));
  const list = await (await fetch(url + '/v1/models', { headers: { 'x-local-router-key': token } })).json();
  assert.ok(list.data.some(model => model.id === 'added'));
  assert.equal((await call('added')).status, 200); assert.equal(authenticated, true); assert.equal(lastModel, 'new-model');
  // Invalid DPAPI must not fall back to a stale environment value or issue another upstream call.
  const callsBeforeFailure = upstreamCalls.length;
  const newModelCallsBefore = upstreamCalls.filter(entry => entry.model === 'new-model').length;
  await writeFile(credentialPath, 'invalid base64');
  // The broken source is dropped with its route: the request cannot reach an upstream any more.
  assert.notEqual((await call('added')).status, 200);
  // Only the broken source is disabled: the healthy source keeps working with its own credential.
  assert.equal((await call('secondary')).status, 200); assert.equal(lastModel, 'secondary-model');
  assert.equal(upstreamCalls.filter(entry => entry.model === 'new-model').length, newModelCallsBefore);
  assert.equal(upstreamCalls.length, callsBeforeFailure + 1);
  const runtime = (await (await fetch(url + '/health')).json()).runtime;
  assert.equal(runtime.credential_status, 'degraded');
  assert.equal(runtime.unavailable_providers.some(entry => entry.id === 'alpha'), true);
  assert.ok(!JSON.stringify(runtime).includes('synthetic-credential-one'));
});
