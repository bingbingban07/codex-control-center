import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, writeFile, mkdir, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';

const HELPER = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'provider-credentials.ps1');
const ENVIRONMENT_NAME = 'CC_HELPER_SYNTHETIC_ALPHA';
const ENVIRONMENT_VALUE = 'synthetic-helper-credential-value';

function runHelper(configPath, codexHome, extra = []) {
  return new Promise((accept, reject) => {
    execFile('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', HELPER, '-ConfigPath', configPath, '-CodexHome', codexHome, ...extra],
    { windowsHide: true, timeout: 30000 }, (error, stdout, stderr) => {
      accept({ code: error ? (error.code ?? 1) : 0, stdout, stderr: String(stderr ?? '') });
    });
  });
}

async function workspace(t, providers) {
  const root = await mkdtemp(join(tmpdir(), 'cc-helper-'));
  const configPath = join(root, 'router.json');
  const codexHome = join(root, 'codex');
  await mkdir(join(codexHome, 'secrets'), { recursive: true });
  await writeFile(configPath, JSON.stringify({ host: '127.0.0.1', port: 18748, providers }));
  t.after(() => rm(root, { recursive: true, force: true }));
  return { root, configPath, codexHome };
}

test('one provider without a credential does not remove the credential of the healthy one', async t => {
  process.env[ENVIRONMENT_NAME] = ENVIRONMENT_VALUE;
  t.after(() => { delete process.env[ENVIRONMENT_NAME]; });
  const { configPath, codexHome } = await workspace(t, {
    alpha: { key_env: ENVIRONMENT_NAME },
    beta: { key_env: 'CC_HELPER_SYNTHETIC_MISSING' },
  });

  const result = await runHelper(configPath, codexHome);
  assert.equal(result.code, 0);
  const payload = JSON.parse(result.stdout.replace(/^\uFEFF/, ''));
  assert.equal(payload.credentials[ENVIRONMENT_NAME], ENVIRONMENT_VALUE);
  assert.equal(payload[ENVIRONMENT_NAME], ENVIRONMENT_VALUE, 'running legacy routers must receive the same flat credential');
  assert.deepEqual(payload.unavailable, [{ id: 'beta', reason: 'credential_unavailable' }]);
  assert.equal(typeof payload.env_fingerprint, 'string');
  assert.ok(payload.env_fingerprint.length > 0);
});

test('the fingerprint mode reports the same digest and never returns a credential', async t => {
  process.env[ENVIRONMENT_NAME] = ENVIRONMENT_VALUE;
  t.after(() => { delete process.env[ENVIRONMENT_NAME]; });
  const { configPath, codexHome } = await workspace(t, {
    alpha: { key_env: ENVIRONMENT_NAME },
    beta: { key_env: 'CC_HELPER_SYNTHETIC_MISSING' },
  });

  const fullResult = await runHelper(configPath, codexHome);
  const probeResult = await runHelper(configPath, codexHome, ['-FingerprintOnly']);
  const full = JSON.parse(fullResult.stdout.replace(/^\uFEFF/, ''));
  const probe = JSON.parse(probeResult.stdout.replace(/^\uFEFF/, ''));
  assert.equal(probe.env_fingerprint, full.env_fingerprint);
  assert.equal(probe.credentials, undefined);
  assert.equal(probe[ENVIRONMENT_NAME], undefined);
  assert.ok(!probeResult.stdout.includes(ENVIRONMENT_VALUE));
  assert.ok(!full.env_fingerprint.includes(ENVIRONMENT_VALUE));
});

test('a changed environment credential changes the fingerprint', async t => {
  const { configPath, codexHome } = await workspace(t, { alpha: { key_env: ENVIRONMENT_NAME } });
  process.env[ENVIRONMENT_NAME] = 'synthetic-rotation-one';
  const first = JSON.parse((await runHelper(configPath, codexHome, ['-FingerprintOnly'])).stdout.replace(/^\uFEFF/, ''));
  process.env[ENVIRONMENT_NAME] = 'synthetic-rotation-two';
  const second = JSON.parse((await runHelper(configPath, codexHome, ['-FingerprintOnly'])).stdout.replace(/^\uFEFF/, ''));
  delete process.env[ENVIRONMENT_NAME];
  assert.notEqual(first.env_fingerprint, second.env_fingerprint);
});

test('a broken encrypted payload disables only its own provider and never falls back to the environment', async t => {
  process.env[ENVIRONMENT_NAME] = ENVIRONMENT_VALUE;
  t.after(() => { delete process.env[ENVIRONMENT_NAME]; });
  const { configPath, codexHome } = await workspace(t, { alpha: { key_env: ENVIRONMENT_NAME } });
  await writeFile(join(codexHome, 'secrets', 'control-center-alpha.dpapi'), 'not-base64-encrypted-payload');

  const payload = JSON.parse((await runHelper(configPath, codexHome)).stdout.replace(/^\uFEFF/, ''));
  assert.deepEqual(payload.unavailable, [{ id: 'alpha', reason: 'credential_unreadable' }]);
  assert.equal(payload.credentials[ENVIRONMENT_NAME], undefined);
  assert.equal(payload[ENVIRONMENT_NAME], undefined, 'broken credentials cannot remain available to legacy readers');
});

test('a provider without a usable key name is reported as unavailable', async t => {
  const { configPath, codexHome } = await workspace(t, { alpha: { base_url: 'http://127.0.0.1:1/v1' } });
  const payload = JSON.parse((await runHelper(configPath, codexHome)).stdout.replace(/^\uFEFF/, ''));
  assert.deepEqual(payload.unavailable, [{ id: 'alpha', reason: 'missing_key_env' }]);
  assert.equal(payload.credentials[''], undefined);
});

test('a chatgpt provider needs no local credential', async t => {
  const { configPath, codexHome } = await workspace(t, { chatgpt: { auth_mode: 'chatgpt' } });
  const payload = JSON.parse((await runHelper(configPath, codexHome)).stdout.replace(/^\uFEFF/, ''));
  assert.deepEqual(payload.unavailable, []);
  assert.deepEqual(payload.credentials, {});
});
