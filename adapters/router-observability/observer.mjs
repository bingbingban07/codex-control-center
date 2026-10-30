import { randomUUID } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
import {
  appendFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
} from 'node:fs';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const DEFAULT_MAX_BYTES = 2 * 1024 * 1024;
const DEFAULT_BACKUPS = 3;
const MIN_MAX_BYTES = 1024;
const MAX_MAX_BYTES = 16 * 1024 * 1024;
const MAX_METADATA_LENGTH = 128;
// The engine reports the served model after the response was flushed. The observer waits a bounded
// number of event loop turns for the metadata of THIS request instead of guessing.
const MAX_METADATA_TURNS = 20;

// Health and model-listing probes are not model requests. They are answered by the router
// itself, so recording them would pollute the request log with successful "未采集" entries.
const PROBE_PATHS = new Set([
  '/health',
  '/healthz',
  '/live',
  '/liveness',
  '/ready',
  '/readiness',
  '/status',
  '/metrics',
  '/models',
  '/model_list',
  '/v1/models',
  '/v1/model_list',
]);

function boundedInteger(value, fallback, minimum, maximum) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed)) return fallback;
  return Math.min(maximum, Math.max(minimum, parsed));
}

function safeStdout(entry) {
  try {
    process.stdout.write(`${entry}\n`);
  } catch {
    // Observation must never break forwarding.
  }
}

export class RotatingNdjsonWriter {
  constructor(path, options = {}) {
    if (typeof path !== 'string' || !path.trim()) throw new TypeError('events path is required');
    this.path = resolve(path);
    this.maxBytes = boundedInteger(options.maxBytes, DEFAULT_MAX_BYTES, MIN_MAX_BYTES, MAX_MAX_BYTES);
    this.maxBackups = boundedInteger(options.maxBackups, DEFAULT_BACKUPS, 0, 5);
  }

  write(value) {
    try {
      const line = `${JSON.stringify(value)}\n`;
      const bytes = Buffer.byteLength(line);
      this.#rotateIfNeeded(bytes);
      mkdirSync(dirname(this.path), { recursive: true });
      appendFileSync(this.path, line, 'utf8');
      return true;
    } catch {
      return false;
    }
  }

  #rotateIfNeeded(incomingBytes) {
    if (!existsSync(this.path)) return;
    const currentBytes = statSync(this.path).size;
    if (currentBytes + incomingBytes <= this.maxBytes) return;

    if (this.maxBackups === 0) {
      rmSync(this.path, { force: true });
      return;
    }

    const oldest = `${this.path}.${this.maxBackups}`;
    rmSync(oldest, { force: true });
    for (let index = this.maxBackups - 1; index >= 1; index -= 1) {
      const source = `${this.path}.${index}`;
      if (existsSync(source)) renameSync(source, `${this.path}.${index + 1}`);
    }
    renameSync(this.path, `${this.path}.1`);
  }
}

function buildRequestEvent(request, response, startedAt, startedTime, cancelled, context) {
  return {
    event: 'router_request',
    request_id: randomUUID(),
    time: startedTime,
    total_ms: Math.max(0, Math.round((performance.now() - startedAt) * 1000) / 1000),
    status: cancelled && !response.writableEnded ? 499 : response.statusCode,
    model: context && context.model ? context.model : null,
    provider: context && context.provider ? context.provider : null,
    cancelled: Boolean(cancelled),
  };
}

function requestPath(request) {
  const url = typeof request?.url === 'string' && request.url.length > 0 ? request.url : '/';
  const index = url.indexOf('?');
  const path = index === -1 ? url : url.slice(0, index);
  const trimmed = path.length > 1 ? path.replace(/\/+$/, '') : path;
  return trimmed.length > 0 ? trimmed : '/';
}

/// <summary>True only for inference traffic; probes and listings are excluded.</summary>
export function isModelRequest(request) {
  const method = String(request?.method || 'GET').toUpperCase();
  if (method === 'GET' || method === 'HEAD') return false;
  return !PROBE_PATHS.has(requestPath(request));
}

function boundedText(value) {
  if (typeof value !== 'string') return '';
  const trimmed = value.trim();
  return trimmed.length > MAX_METADATA_LENGTH ? trimmed.slice(0, MAX_METADATA_LENGTH) : trimmed;
}

function firstText(source, keys) {
  for (const key of keys) {
    const value = source?.[key];
    if (typeof value === 'string' && value.trim().length > 0) return boundedText(value);
  }
  return '';
}

/// <summary>
/// Extracts the served model and provider from a router engine log line. Only the two names are
/// kept; request bodies and credentials are never read or stored.
/// </summary>
export function parseEngineLogRecord(line) {
  if (typeof line !== 'string' || line.length === 0 || line.length > 64_000) return null;
  let parsed;
  try {
    parsed = JSON.parse(line);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  const model = firstText(parsed, ['model', 'model_alias', 'model_id']);
  if (!model) return null;
  return { model, provider: firstText(parsed, ['provider', 'provider_id']) };
}

/// <summary>Per request metadata holder shared by the router engine logger and the observer.</summary>
export function createRequestContext() {
  return { model: null, provider: null };
}

const boundRequests = new WeakSet();

/// <summary>
/// Keeps the request context reachable for the whole request lifecycle. A body that arrives in a
/// later packet is handled in another async context, so the context is re-established on the body
/// and connection events. These listeners never read the body, so nothing is consumed.
/// </summary>
export function bindRequestContext(request, response, storage, context) {
  if (!request || !response || !storage || !context || boundRequests.has(request)) return;
  boundRequests.add(request);
  // The router engine streams the upstream response with pipeline(), which attaches several
  // listeners to the same response. The default limit would report a leak that does not exist.
  if (typeof response.setMaxListeners === 'function') response.setMaxListeners(64);
  request.once('end', () => storage.enterWith(context));
  request.once('aborted', () => storage.enterWith(context));
  response.once('close', () => storage.enterWith(context));
}

/// <summary>Copies engine reported metadata into the context of the request that is handled.</summary>
export function applyEngineLogRecord(context, record) {
  if (!context || !record) return false;
  if (!context.model && record.model) context.model = record.model;
  if (!context.provider && record.provider) context.provider = record.provider;
  return Boolean(context.model);
}

export function attachObserver(server, options = {}) {
  const writeEvent = typeof options.writeEvent === 'function' ? options.writeEvent : () => {};
  const storage = options.storage ?? new AsyncLocalStorage();
  const contexts = options.contexts ?? new WeakMap();
  const createContext = typeof options.createContext === 'function' ? options.createContext : () => createRequestContext();

  // The context has to exist before the router engine handles the request: the engine reports the
  // served model through its logger while that request is being processed, and the logger must be
  // able to reach the context of exactly this request.
  server.prependListener('request', (request, response) => {
    const context = createContext(request);
    contexts.set(request, context);
    storage.enterWith(context);
    bindRequestContext(request, response, storage, context);
  });

  const onRequest = (request, response) => {
    if (!isModelRequest(request)) return;
    const startedAt = performance.now();
    const startedTime = new Date().toISOString();
    let ended = false;
    let context = contexts.get(request);
    if (!context) {
      context = createContext(request);
      contexts.set(request, context);
    }

    const finish = (cancelled) => {
      if (ended) return;
      ended = true;
      // The engine reports the served model after the response was flushed, so the metadata of
      // this request may still be pending. The request body is never read.
      const publish = (attempt) => {
        if (context.model || attempt >= MAX_METADATA_TURNS) {
          const event = buildRequestEvent(request, response, startedAt, startedTime, cancelled, context);
          try {
            writeEvent(event);
          } catch {
            // Event logging is best-effort and cannot affect the response.
          }
          return;
        }
        setImmediate(() => publish(attempt + 1));
      };
      publish(0);
    };

    response.once('finish', () => finish(false));
    response.once('close', () => finish(!response.writableEnded));
  };

  server.on('request', onRequest);
  return () => server.removeListener('request', onRequest);
}

export async function createObservedRouter(config, options = {}) {
  const {
    routerPath,
    eventWriter,
    writeEvent,
    logger = safeStdout,
    ...routerOptions
  } = options;

  if (typeof routerPath !== 'string' || !routerPath.trim()) {
    throw new TypeError('routerPath is required');
  }

  const routerUrl = pathToFileURL(isAbsolute(routerPath) ? routerPath : resolve(routerPath)).href;
  const routerModule = await import(routerUrl);
  if (typeof routerModule.createRouter !== 'function') {
    throw new TypeError('The configured router module does not export createRouter.');
  }

  const storage = new AsyncLocalStorage();
  const contexts = new WeakMap();
  const server = await routerModule.createRouter(config, { ...routerOptions, logger: line => {
    const record = parseEngineLogRecord(line);
    if (record) applyEngineLogRecord(storage.getStore(), record);
    logger(line);
  } });
  const sink = writeEvent || (eventWriter ? (event) => eventWriter.write(event) : () => {});
  attachObserver(server, { writeEvent: sink, storage, contexts });
  return server;
}

function parseArguments(argv) {
  const result = {
    router: process.env.CODEX_ROUTER_MODULE || '',
    config: process.env.CODEX_ROUTER_CONFIG || '',
    events: process.env.CODEX_ROUTER_EVENTS || '',
    maxBytes: process.env.CODEX_ROUTER_EVENTS_MAX_BYTES || DEFAULT_MAX_BYTES,
    backups: process.env.CODEX_ROUTER_EVENTS_BACKUPS || DEFAULT_BACKUPS,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    const next = argv[index + 1];
    if (['--router', '--config', '--events', '--max-bytes', '--backups'].includes(argument)) {
      if (next === undefined) throw new Error(`Missing value for ${argument}.`);
      const key = { '--router': 'router', '--config': 'config', '--events': 'events', '--max-bytes': 'maxBytes', '--backups': 'backups' }[argument];
      result[key] = next;
      index += 1;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }

  return result;
}

export async function startFromCommandLine(argv = process.argv.slice(2)) {
  const options = parseArguments(argv);
  if (!options.router || !options.config) {
    throw new Error('Usage: node observer.mjs --router <router.mjs> --config <router.json> [--events <events.ndjson>]');
  }

  const config = JSON.parse(readFileSync(resolve(options.config), 'utf8'));
  const eventsPath = options.events || join(dirname(resolve(options.router)), 'events.ndjson');
  const eventWriter = new RotatingNdjsonWriter(eventsPath, {
    maxBytes: options.maxBytes,
    maxBackups: options.backups,
  });

  const server = await createObservedRouter(config, {
    routerPath: options.router,
    eventWriter,
    logger: safeStdout,
  });

  const host = config?.router?.host || config?.host || '127.0.0.1';
  const port = config?.router?.port ?? config?.port;
  await new Promise((listenReady, rejectListen) => {
    server.once('error', rejectListen);
    server.listen(port, host, () => {
      server.removeListener('error', rejectListen);
      listenReady();
    });
  });

  process.stdout.write(`${JSON.stringify({
    status: 'listening',
    host,
    port: server.address().port,
    observer: true,
    events_path: eventsPath,
  })}\n`);
  return server;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  startFromCommandLine().catch((error) => {
    process.stderr.write(`Router observer failed to start: ${String(error?.message || error).replace(/[\r\n]/g, ' ').slice(0, 500)}\n`);
    process.exitCode = 1;
  });
}

