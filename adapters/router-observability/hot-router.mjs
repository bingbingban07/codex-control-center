import http from 'node:http';
import { createHash } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
import { readFile, stat } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { compatibleRequest } from './custom-tool-compat.mjs';
import { attachObserver, RotatingNdjsonWriter, createRequestContext, parseEngineLogRecord, applyEngineLogRecord, bindRequestContext } from './observer.mjs';

const hash = value => createHash('sha256').update(value).digest('hex');
const json = (response, status, data) => {
  response.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
  response.end(JSON.stringify(data));
};
// Environment-sourced credentials are not covered by the encrypted-file stamp, so they are
// re-checked on a slower, bounded cadence instead of on every request.
const CREDENTIAL_CHECK_INTERVAL_MS = 15_000;
const PROVIDER_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

/// <summary>File stamp of every encrypted credential plus the providers that use the environment.</summary>
async function credentialState(config, codexHome, configPath) {
  const entries = [];
  const environmentSourced = [];
  for (const [id, provider] of Object.entries(config.providers || {})) {
    if (provider.auth_mode === 'chatgpt') continue;
    if (!PROVIDER_ID_PATTERN.test(id)) continue;
    const path = join(codexHome, 'secrets', `control-center-${id}.dpapi`);
    try {
      const value = await stat(path);
      entries.push([id, path, value.mtimeMs, value.size]);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      entries.push([id, path, null]);
      environmentSourced.push(id);
    }
    if (id === 'b' || id === 'mimo') {
      const legacy = join(dirname(configPath), `key-${id}.dpapi`);
      try {
        const value = await stat(legacy);
        entries.push([id, legacy, value.mtimeMs, value.size]);
      } catch (error) {
        if (error.code !== 'ENOENT') throw error;
        entries.push([id, legacy, null]);
      }
    }
  }
  return { stamp: hash(JSON.stringify(entries)), environmentSourced };
}

function runCredentialHelper(helperPath, args) {
  return new Promise((accept, reject) => {
    execFile('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', helperPath, ...args],
    { windowsHide: true, timeout: 15000, maxBuffer: 1024 * 1024 }, (error, stdout) => {
      // No child output or exception object is logged; stdout contains private credentials.
      if (error) { reject(new Error('credential_load_failed')); return; }
      try {
        const parsed = JSON.parse(stdout.replace(/^\uFEFF/, ''));
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error();
        accept(parsed);
      } catch { reject(new Error('credential_load_failed')); }
    });
  });
}

/// <summary>Accepts the per-provider protocol and the legacy flat credential map.</summary>
function normalizeCredentialPayload(payload) {
  const source = payload && typeof payload === 'object' && !Array.isArray(payload) ? payload : {};
  let raw = source.credentials;
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    raw = Object.values(source).every(value => typeof value === 'string') ? source : {};
  }
  const credentials = {};
  for (const [key, value] of Object.entries(raw)) {
    if (typeof value === 'string') credentials[key] = value;
  }
  const unavailable = Array.isArray(source.unavailable)
    ? source.unavailable
      .filter(entry => entry && typeof entry.id === 'string' && entry.id.length > 0)
      .map(entry => ({
        id: entry.id,
        reason: typeof entry.reason === 'string' && entry.reason.length > 0 ? entry.reason : 'credential_unavailable',
      }))
    : [];
  return {
    credentials,
    unavailable,
    envFingerprint: typeof source.env_fingerprint === 'string' ? source.env_fingerprint : null,
  };
}

/// <summary>
/// Removes providers without a usable credential and the routes that point at them, so the engine
/// can never spend with an old key or reach an upstream that cannot be authenticated.
/// </summary>
function buildEffectiveConfig(config, unavailable) {
  const unavailableIds = new Set(unavailable.map(entry => entry.id));
  const reasons = new Map(unavailable.map(entry => [entry.id, entry.reason]));
  const effective = { ...config };
  const droppedRoutes = [];
  if (unavailableIds.size > 0) {
    const providers = { ...(config.providers || {}) };
    for (const id of unavailableIds) delete providers[id];
    effective.providers = providers;
    if (config.routes && typeof config.routes === 'object' && !Array.isArray(config.routes)) {
      const routes = {};
      for (const [routeId, route] of Object.entries(config.routes)) {
        const providerId = route && typeof route === 'object' ? (route.provider ?? route.provider_id) : undefined;
        if (providerId !== undefined && providerId !== null && unavailableIds.has(String(providerId))) {
          droppedRoutes.push({ route: routeId, provider: String(providerId), reason: reasons.get(String(providerId)) || 'credential_unavailable' });
          continue;
        }
        routes[routeId] = route;
      }
      effective.routes = routes;
    }
    if (Array.isArray(config.models)) {
      effective.models = config.models.filter(entry => {
        const providerId = entry && typeof entry === 'object' ? (entry.provider ?? entry.provider_id) : undefined;
        return !(providerId !== undefined && providerId !== null && unavailableIds.has(String(providerId)));
      });
    }
  }
  return { effective, droppedRoutes };
}

function credentialStatusFor(config, unavailable) {
  const total = Object.keys(config.providers || {}).length;
  if (total === 0) return 'ready';
  if (unavailable.length === 0) return 'ready';
  return unavailable.length >= total ? 'unavailable' : 'degraded';
}

export async function createHotRouter({ configPath, codexHome, enginePath, credentialLoader, credentialProbe,
  credentialCheckIntervalMs = CREDENTIAL_CHECK_INTERVAL_MS, logger = () => {}, observe }) {
  configPath = resolve(configPath);
  codexHome = resolve(codexHome);
  const engine = await import(pathToFileURL(resolve(enginePath)).href);
  const helperPath = join(dirname(fileURLToPath(import.meta.url)), 'provider-credentials.ps1');
  // The engine reports the served model while the request is being processed. An async local
  // storage binds that report to the request that is currently handled, so concurrent requests can
  // never exchange their metadata.
  const storage = new AsyncLocalStorage();
  const contexts = new WeakMap();
  const emit = line => {
    const record = parseEngineLogRecord(line);
    if (record) applyEngineLogRecord(storage.getStore(), record);
    logger(line);
  };
  const getCredentials = credentialLoader
    ? async config => normalizeCredentialPayload(await credentialLoader(config))
    : async config => normalizeCredentialPayload(await runCredentialHelper(helperPath, ['-ConfigPath', configPath, '-CodexHome', codexHome]));
  // A custom loader owns its own credential source, so the environment probe stays disabled unless
  // the caller provides one explicitly.
  const probeCredentials = credentialProbe
    ? async config => normalizeCredentialPayload(await credentialProbe(config)).envFingerprint
    : credentialLoader
      ? async () => null
      : async config => normalizeCredentialPayload(await runCredentialHelper(helperPath,
        ['-ConfigPath', configPath, '-CodexHome', codexHome, '-FingerprintOnly'])).envFingerprint;

  let current, signature, configHash, credentialsRevision, credentialFingerprint = null;
  let invariant, tokenRevision, lastError = null, active = 0;
  let loadedConfig = null, unavailableProviders = [], unavailableRoutes = [];
  let credentialStatus = 'unknown', credentialCheckedAt = null, configSynced = false;
  let pending, credentialsDirty = false, probing = false;
  const retired = new Set();
  const toolGuard = { deduplicated_requests: 0, duplicates_removed: 0, conflicting_requests: 0 };
  let lastToolStructure = null, lastMinimaxToolStructure = null, toolRequestsInspected = 0, upstreamDuplicateToolErrors = 0, legacyNormalizedRequests = 0, legacyToolItemsRemoved = 0;
  const guardedTransport = (url, options, callback) => compatibleRequest(url, options, callback, result => {
    // Counters only: never retain names, schemas, prompts, headers or credentials.
    if (result.inspected) { toolRequestsInspected++; lastToolStructure = result.summary; if (url.hostname === 'api.minimax.cn') lastMinimaxToolStructure = result.summary; }
    else if ('upstream_duplicate_tool_error' in result) { if (result.upstream_duplicate_tool_error) upstreamDuplicateToolErrors++; }
    else if (result.conflict) toolGuard.conflicting_requests++;
    else {
      if (result.removed) { toolGuard.deduplicated_requests++; toolGuard.duplicates_removed += result.removed; }
      if (result.legacy_removed) { legacyNormalizedRequests++; legacyToolItemsRemoved += result.legacy_removed; }
    }
  });

  async function refresh() {
    if (pending) return pending;
    pending = (async () => {
      let candidate;
      try {
        const bytes = await readFile(configPath);
        const parsed = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
        if (parsed.host !== '127.0.0.1' || !Number.isInteger(parsed.port) || parsed.port < 1 || parsed.port > 65535)
          throw new Error('invalid_listener');
        const nextInvariant = JSON.stringify([parsed.host, parsed.port, parsed.auth_token_file]);
        const nextConfigHash = hash(bytes);
        const state = await credentialState(parsed, codexHome, configPath);
        const nextSignature = nextConfigHash + ':' + state.stamp;
        if (invariant && invariant !== nextInvariant) throw new Error('listener_or_token_change_requires_restart');
        const nextTokenRevision = hash(await readFile(parsed.auth_token_file));
        if (tokenRevision && tokenRevision !== nextTokenRevision) throw new Error('token_change_requires_restart');
        if (signature === nextSignature && !credentialsDirty) {
          lastError = null;
          configSynced = true;
          return;
        }

        const loaded = await getCredentials(parsed);
        const { effective, droppedRoutes } = buildEffectiveConfig(parsed, loaded.unavailable);
        const providerIds = Object.keys(parsed.providers || {});
        const usableProviders = providerIds.filter(id => !loaded.unavailable.some(entry => entry.id === id));
        const credentials = loaded.credentials;
        let created = null;
        if (providerIds.length > 0 && usableProviders.length === 0) {
          // No source can be authenticated. The previous generation is retired instead of kept
          // alive, so no request can spend with a revoked key; the router keeps running and
          // recovers by itself once a credential becomes available again.
          const retiredGeneration = current;
          current = null;
          if (retiredGeneration) { retired.add(retiredGeneration); if (active === 0) disposeRetired(); }
        } else {
          // Pass only explicitly configured references; do not enumerate the host environment.
          const env = { ...credentials };
          for (const provider of Object.values(parsed.providers || {})) {
            if (typeof provider.proxy_env === 'string' && /^[A-Za-z_][A-Za-z0-9_]*$/.test(provider.proxy_env))
              env[provider.proxy_env] = process.env[provider.proxy_env];
          }
          try { created = await engine.createRouter(effective, { env, logger: emit, requestTransport: guardedTransport }); }
          // The engine may read the credential per request, so the environment copy handed to the
          // generation keeps its value for the lifetime of that generation. Only the temporary
          // loader result is cleared; every generation builds its own environment.
          finally { for (const key of Object.keys(credentials)) credentials[key] = null; }
          candidate = created;
          // Detect concurrent configuration edits during credential loading before publishing the generation.
          if (hash(await readFile(configPath)) !== nextConfigHash ||
              (await credentialState(parsed, codexHome, configPath)).stamp !== state.stamp ||
              hash(await readFile(parsed.auth_token_file)) !== nextTokenRevision)
            throw new Error('configuration_changed_during_reload');
        }

        const old = current;
        current = candidate; candidate = null;
        invariant = nextInvariant; tokenRevision = nextTokenRevision;
        signature = nextSignature; configHash = nextConfigHash;
        loadedConfig = parsed;
        unavailableProviders = loaded.unavailable;
        unavailableRoutes = droppedRoutes;
        credentialStatus = credentialStatusFor(parsed, loaded.unavailable);
        credentialCheckedAt = new Date().toISOString();
        credentialsRevision = hash(state.stamp + ':' + (loaded.envFingerprint || ''));
        credentialFingerprint = loaded.envFingerprint;
        credentialsDirty = false;
        configSynced = true;
        lastError = null;
        if (old) { retired.add(old); if (active === 0) disposeRetired(); }
        logger(JSON.stringify({ event: 'config_loaded', status: 'config_loaded', time: new Date().toISOString() }));
      } catch (error) {
        candidate?.close();
        const allowed = ['listener_or_token_change_requires_restart', 'token_change_requires_restart', 'credential_load_failed', 'configuration_changed_during_reload'];
        lastError = allowed.includes(error.message) ? error.message : 'configuration_reload_failed';
        configSynced = false;
        if (!current) throw new Error(lastError);
      }
    })().finally(() => { pending = null; });
    return pending;
  }

  /// <summary>
  /// Re-checks environment-sourced credentials on a bounded cadence. Only the configured key names
  /// are read and only their digest is kept; a change republishes the router with the new key.
  /// </summary>
  async function probeEnvironmentCredentials() {
    if (probing) return;
    probing = true;
    try {
      const bytes = await readFile(configPath);
      const parsed = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
      const state = await credentialState(parsed, codexHome, configPath);
      if (state.environmentSourced.length === 0) return;
      const fingerprint = await probeCredentials(parsed);
      if (fingerprint === null || fingerprint === undefined) return;
      if (fingerprint === credentialFingerprint) return;
      credentialsDirty = true;
      await refresh();
    } catch {
      // The current generation keeps serving; the next probe retries.
    } finally {
      probing = false;
    }
  }

  function disposeRetired() { for (const server of retired) server.close(); retired.clear(); }
  await refresh();
  const server = http.createServer((request, response) => {
    // Install a listener before awaiting a reload so POST bodies are neither drained nor lost.
    request.pause();
    void refresh().then(() => {
      if (request.destroyed || response.destroyed) return;
      const pathname = new URL(request.url, 'http://127.0.0.1').pathname;
      if (request.method === 'GET' && pathname === '/health') {
        json(response, 200, healthPayload());
        request.resume(); return;
      }
      // A rejected update must never silently call the old route or spend with an old key.
      if (lastError) { json(response, 503, { error: { message: 'Router configuration was saved but could not be activated. Check management console runtime status.', code: lastError } }); request.resume(); return; }
      if (!current) {
        json(response, 503, { error: {
          message: 'No provider has a usable credential. Requests are not sent to any upstream until a credential is available.',
          code: 'no_usable_credential',
          unavailable_providers: unavailableProviders,
        } });
        request.resume(); return;
      }
      const generation = current;
      active++;
      let finished = false;
      const finish = () => { if (finished) return; finished = true; active--; if (active === 0) disposeRetired(); };
      response.once('finish', finish); response.once('close', finish);
      let context = contexts.get(request);
      if (!context) {
        context = createRequestContext();
        contexts.set(request, context);
      }
      bindRequestContext(request, response, storage, context);
      storage.run(context, () => generation.emit('request', request, response));
      request.resume();
    }).catch(() => { json(response, 503, { error: { message: 'Router configuration is unavailable.', code: 'configuration_reload_failed' } }); request.resume(); });
  });
  function healthPayload() {
    const total = loadedConfig ? Object.keys(loadedConfig.providers || {}).length : 0;
    return {
      status: 'ok',
      features: { chatgpt: true, config_reload: true, custom_tool_compat: true, tool_definition_guard: true, tool_definition_guard_revision: 6 },
      runtime: {
        config_sha256: configHash,
        credentials_revision: credentialsRevision,
        reload_error: lastError,
        active_requests: active,
        tool_definition_guard: { ...toolGuard },
        tool_structure: { inspected_requests: toolRequestsInspected, upstream_duplicate_errors: upstreamDuplicateToolErrors, legacy_normalized_requests: legacyNormalizedRequests, legacy_tool_items_removed: legacyToolItemsRemoved, last: lastToolStructure, last_minimax: lastMinimaxToolStructure },
        // The configuration fingerprint and the credential check are reported separately: a synced
        // configuration can still run with an unusable source.
        config_synced: configSynced,
        credential_status: credentialStatus,
        credential_checked_at: credentialCheckedAt,
        providers_total: total,
        providers_ready: Math.max(0, total - unavailableProviders.length),
        unavailable_providers: unavailableProviders,
        unavailable_routes: unavailableRoutes,
      },
    };
  }
  server.requestTimeout = 0;
  server.headersTimeout = 65000;
  const timer = setInterval(() => { void refresh().catch(() => {}); }, 3000);
  timer.unref();
  const credentialTimer = setInterval(() => { void probeEnvironmentCredentials().catch(() => {}); }, credentialCheckIntervalMs);
  credentialTimer.unref();
  server.on('close', () => { clearInterval(timer); clearInterval(credentialTimer); current?.close(); disposeRetired(); });
  if (observe && typeof observe.writeEvent === 'function') {
    attachObserver(server, { writeEvent: observe.writeEvent, storage, contexts });
  }
  return server;
}

export async function main(argv = process.argv.slice(2)) {
  const values = {};
  for (let i = 0; i < argv.length; i += 2) {
    if (!['--config', '--engine', '--codex-home'].includes(argv[i]) || !argv[i + 1]) throw new Error('invalid_arguments');
    values[argv[i]] = argv[i + 1];
  }
  if (!values['--config'] || !values['--engine'] || !values['--codex-home']) throw new Error('invalid_arguments');
  const configPath = resolve(values['--config']);
  const writer = new RotatingNdjsonWriter(join(dirname(configPath), 'events.ndjson'));
  const server = await createHotRouter({ configPath, codexHome: values['--codex-home'], enginePath: values['--engine'],
    logger: line => process.stdout.write(line + '\n'), observe: { writeEvent: event => writer.write(event) } });
  const config = JSON.parse((await readFile(configPath, 'utf8')).replace(/^\uFEFF/, ''));
  await new Promise((accept, reject) => { server.once('error', reject); server.listen(config.port, config.host, accept); });
  process.stdout.write(JSON.stringify({ status: 'listening', host: config.host, port: config.port, hot_reload: true }) + '\n');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(() => { process.stderr.write('Managed router failed to activate; inspect configuration and credential references.\n'); process.exitCode = 1; });
}
