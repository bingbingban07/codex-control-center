import http from 'node:http';
import https from 'node:https';
import tls from 'node:tls';
import { isIP } from 'node:net';
import { createHash, timingSafeEqual } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { isAbsolute, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { pipeline } from 'node:stream/promises';

const BODY_LIMIT = 64 * 1024 * 1024;
const ERROR_LIMIT = 1024 * 1024;
const UPSTREAM_TIMEOUT = 30 * 60 * 1000;
const PROXY_CONNECT_TIMEOUT = 30_000;
const CHATGPT_BASE_URL = 'https://chatgpt.com/backend-api/codex';
const POST_PATHS = new Set(['/v1/responses', '/v1/responses/compact', '/v1/responses/input_tokens']);
const SENSITIVE_HEADER = /authorization|cookie|api[-_]?key|token|secret/i;
const METADATA_HEADER = /^(?:x-codex-|x-openai-|openai-)/i;
const RESPONSE_HEADERS = new Set(['content-type', 'content-length', 'content-encoding', 'cache-control', 'retry-after', 'x-request-id']);

class RequestError extends Error {
  constructor(status, message, code) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

function digest(value) {
  return createHash('sha256').update(value).digest();
}

function json(response, status, value) {
  if (response.destroyed || response.writableEnded) return;
  if (response.headersSent) {
    response.destroy();
    return;
  }
  const body = Buffer.from(JSON.stringify(value));
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': body.length,
    'cache-control': 'no-store',
  });
  response.end(body);
}

function sendError(response, status, message, code) {
  json(response, status, { error: { message, type: 'router_error', code } });
}

function readLimited(stream, limit) {
  return new Promise((resolveBody, rejectBody) => {
    const chunks = [];
    let size = 0;
    let settled = false;
    stream.on('data', (chunk) => {
      if (settled) return;
      size += chunk.length;
      if (size > limit) {
        settled = true;
        chunks.length = 0;
        rejectBody(new RequestError(413, 'Request body exceeds the 64 MiB limit.', 'body_too_large'));
        return; // Continue draining so the client can receive the error response.
      }
      chunks.push(chunk);
    });
    stream.once('end', () => {
      if (settled) return;
      settled = true;
      resolveBody(Buffer.concat(chunks, size));
    });
    stream.once('aborted', () => {
      if (settled) return;
      settled = true;
      rejectBody(new RequestError(400, 'Request was cancelled.', 'request_cancelled'));
    });
    stream.once('error', () => {
      if (settled) return;
      settled = true;
      rejectBody(new RequestError(400, 'Could not read request body.', 'invalid_request'));
    });
  });
}

function safeLog(logger, entry) {
  try { logger(JSON.stringify(entry)); } catch { /* Logging cannot break a model request. */ }
}

function parseProxyURL(value) {
  let proxy;
  try { proxy = new URL(value); } catch { throw new Error('Invalid ChatGPT proxy URL.'); }
  if (!['http:', 'https:'].includes(proxy.protocol) || !proxy.hostname || (proxy.pathname !== '/' && proxy.pathname !== '') || proxy.search || proxy.hash) {
    throw new Error('ChatGPT proxy must be an HTTP(S) proxy URL without a path, query, or fragment.');
  }
  try {
    if (/[\r\n]/.test(decodeURIComponent(proxy.username)) || /[\r\n]/.test(decodeURIComponent(proxy.password))) throw new Error();
  } catch { throw new Error('Invalid ChatGPT proxy authentication.'); }
  return proxy;
}

/** Build an HTTP CONNECT tunnel pinned to the official Codex TLS endpoint.
 * Each router request gets its own agent so aborting it also cancels a tunnel
 * still establishing, without disturbing another request.
 */
export function createChatGPTProxyAgent(proxyValue) {
  const proxy = parseProxyURL(proxyValue instanceof URL ? proxyValue.href : proxyValue);
  const agent = new https.Agent({ keepAlive: false });
  const pending = new Set();
  const originalDestroy = agent.destroy.bind(agent);
  let destroyed = false;
  agent.destroy = () => {
    destroyed = true;
    for (const cancel of [...pending]) cancel();
    originalDestroy();
  };
  agent.createConnection = (options, callback) => {
    if (destroyed || (options.hostname || options.host) !== 'chatgpt.com' || Number(options.port || 443) !== 443) {
      queueMicrotask(() => callback(new Error('ChatGPT proxy destination was refused.')));
      return undefined;
    }
    let settled = false;
    let connectRequest;
    let tunnel;
    let secureSocket;
    let timer;
    const cancel = () => finish(new Error('ChatGPT proxy connection was cancelled.'));
    const finish = (error, socket) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      pending.delete(cancel);
      if (error) {
        secureSocket?.destroy();
        tunnel?.destroy();
        connectRequest?.destroy();
      }
      callback(error, socket);
    };
    pending.add(cancel);
    timer = setTimeout(() => finish(new Error('ChatGPT proxy connection timed out.')), PROXY_CONNECT_TIMEOUT);
    timer.unref();
    const headers = { host: 'chatgpt.com:443' };
    if (proxy.username || proxy.password) {
      const credentials = `${decodeURIComponent(proxy.username)}:${decodeURIComponent(proxy.password)}`;
      headers['proxy-authorization'] = `Basic ${Buffer.from(credentials).toString('base64')}`;
    }
    const transport = proxy.protocol === 'https:' ? https : http;
    const proxyHostname = proxy.hostname.replace(/^\[|\]$/g, '');
    try {
      connectRequest = transport.request({
        protocol: proxy.protocol,
        hostname: proxyHostname,
        port: proxy.port || (proxy.protocol === 'https:' ? 443 : 80),
        method: 'CONNECT',
        path: 'chatgpt.com:443',
        headers,
        agent: false,
        // CONNECT's Host is the destination. An HTTPS proxy's own certificate
        // must still be checked against the proxy host, not that Host header.
        servername: isIP(proxyHostname) ? '' : proxyHostname,
        rejectUnauthorized: true,
        checkServerIdentity: tls.checkServerIdentity,
      });
      connectRequest.once('error', (error) => finish(error));
      connectRequest.once('response', (upstream) => {
        upstream.resume();
        finish(new Error('ChatGPT proxy refused the tunnel.'));
      });
      connectRequest.once('connect', (upstream, socket, head) => {
        tunnel = socket;
        if (settled) { socket.destroy(); return; }
        if (upstream.statusCode !== 200) {
          finish(new Error('ChatGPT proxy refused the tunnel.'));
          return;
        }
        socket.once('error', (error) => finish(error));
        socket.once('close', () => { if (!settled) finish(new Error('ChatGPT proxy tunnel closed before TLS was ready.')); });
        if (head.length) socket.unshift(head);
        secureSocket = tls.connect({
          ...options,
          socket,
          servername: 'chatgpt.com',
          rejectUnauthorized: true,
          checkServerIdentity: tls.checkServerIdentity,
          ALPNProtocols: ['http/1.1'],
        });
        secureSocket.once('error', (error) => finish(error));
        secureSocket.once('close', () => { if (!settled) finish(new Error('ChatGPT proxy TLS connection closed.')); });
        secureSocket.once('secureConnect', () => finish(null, secureSocket));
      });
      connectRequest.end();
    } catch (error) {
      finish(error);
    }
    return undefined;
  };
  return agent;
}

export function validateConfig(config, env = process.env) {
  if (!config || typeof config !== 'object' || Array.isArray(config)) throw new Error('Config must be an object.');
  if (config.host !== '127.0.0.1') throw new Error('Router host must be 127.0.0.1.');
  if (!Number.isInteger(config.port) || config.port < 0 || config.port > 65535) throw new Error('Invalid router port.');
  if (typeof config.auth_token_file !== 'string' || !isAbsolute(config.auth_token_file)) throw new Error('auth_token_file must be an absolute path.');
  if (!config.providers || typeof config.providers !== 'object' || Array.isArray(config.providers)) throw new Error('providers must be an object.');
  if (!config.routes || typeof config.routes !== 'object' || Array.isArray(config.routes)) throw new Error('routes must be an object.');
  const providers = new Map();
  for (const [name, value] of Object.entries(config.providers)) {
    if (!value || typeof value !== 'object') throw new Error('Invalid provider configuration.');
    let base;
    try { base = new URL(value.base_url); } catch { throw new Error('Invalid provider base_url.'); }
    if (!['http:', 'https:'].includes(base.protocol) || base.username || base.password || base.search || base.hash) throw new Error('Provider URL must use HTTP(S) without credentials, query, or fragment.');
    const authMode = value.auth_mode || 'api_key';
    if (authMode === 'chatgpt') {
      // OAuth credentials may only be sent to this exact official endpoint.
      // In particular, no HTTP, port, query, fragment, userinfo, subdomain,
      // encoded-path, or trailing-slash variants are accepted.
      if (value.base_url !== CHATGPT_BASE_URL) throw new Error('ChatGPT authentication requires the exact official Codex base URL.');
      // Background ChatGPT requests use the server-held access token.
      if (value.proxy_env !== undefined && value.proxy_url !== undefined) throw new Error('Choose either proxy_env or proxy_url for ChatGPT.');
      let proxy;
      if (value.proxy_env !== undefined) {
        if (typeof value.proxy_env !== 'string' || !value.proxy_env || typeof env[value.proxy_env] !== 'string' || !env[value.proxy_env].trim()) {
          throw new Error('The configured ChatGPT proxy environment variable is missing or invalid.');
        }
        proxy = parseProxyURL(env[value.proxy_env].trim());
      } else if (value.proxy_url !== undefined) {
        if (typeof value.proxy_url !== 'string' || !value.proxy_url.trim()) throw new Error('Invalid ChatGPT proxy URL.');
        proxy = parseProxyURL(value.proxy_url.trim());
      }
      if (typeof value.auth_file !== 'string' || !isAbsolute(value.auth_file)) throw new Error('ChatGPT provider needs an absolute auth_file.');
      providers.set(name, { base, authMode, proxy, authFile: value.auth_file });
      continue;
    }
    if (authMode !== 'api_key') throw new Error('Unsupported provider authentication mode.');
    if (value.request_profile !== undefined && value.request_profile !== 'mimo') throw new Error('Unsupported provider request profile.');
    if (value.proxy_env !== undefined || value.proxy_url !== undefined) throw new Error('Explicit proxy configuration is supported only for ChatGPT providers.');
    if (typeof value.key_env !== 'string' || !value.key_env) throw new Error('API providers need key_env.');
    const key = env[value.key_env];
    if (typeof key !== 'string' || !key.trim() || /[\r\n]/.test(key)) throw new Error('A configured provider key environment variable is missing or invalid.');
    base.pathname = base.pathname.replace(/\/+$/, '');
    providers.set(name, { base, key: key.trim(), authMode, requestProfile: value.request_profile });
  }
  if (!providers.size) throw new Error('At least one provider is required.');
  const routes = new Map();
  for (const [alias, value] of Object.entries(config.routes)) {
    if (!alias || alias.length > 512 || /[\x00-\x1f\x7f]/.test(alias)) throw new Error('Invalid model alias.');
    if (!value || typeof value !== 'object' || !providers.has(value.provider)) throw new Error('Route references an unknown provider.');
    if (typeof value.model !== 'string' || !value.model.trim() || value.model.length > 512 || /[\x00-\x1f\x7f]/.test(value.model)) throw new Error('Invalid upstream model.');
    if (value.max_output_tokens !== undefined && (!Number.isSafeInteger(value.max_output_tokens) || value.max_output_tokens < 1)) throw new Error('Route output limit must be a positive integer.');
    routes.set(alias, { provider: value.provider, model: value.model, maxOutputTokens: value.max_output_tokens });
  }
  if (!routes.size) throw new Error('At least one model route is required.');
  return { providers, routes };
}

function requestHeaders(incoming, provider, nativeAuth, bodyLength) {
  const result = {
    authorization: `Bearer ${provider.key}`,
    'content-type': 'application/json',
    'content-length': bodyLength,
    accept: incoming.accept || 'application/json',
    'user-agent': incoming['user-agent'] || 'Codex-Local-Model-Router/1.0',
  };
  for (const [name, value] of Object.entries(incoming)) {
    if (value === undefined || SENSITIVE_HEADER.test(name)) continue;
    if (METADATA_HEADER.test(name) || name === 'originator') result[name] = value;
  }
  if (provider.authMode === 'chatgpt' && provider.accountId) result['chatgpt-account-id'] = provider.accountId;
  return result;
}

function responseHeaders(incoming) {
  const result = {};
  for (const [name, value] of Object.entries(incoming)) {
    if (value === undefined || SENSITIVE_HEADER.test(name)) continue;
    if (RESPONSE_HEADERS.has(name) || METADATA_HEADER.test(name)) result[name] = value;
  }
  return result;
}

function redact(value, secrets) {
  let result = String(value);
  for (const secret of secrets) if (secret) result = result.split(secret).join('[redacted]');
  return result;
}

function parseSseFailure(buffer) {
  const text = buffer.toString('utf8');
  for (const line of text.split(/\r?\n/)) {
    if (!line.startsWith('data:')) continue;
    try {
      const event = JSON.parse(line.slice(5).trim());
      if (event && event.type === 'error' && typeof event.message === 'string') return event.message;
      if (event && event.response && event.response.error && typeof event.response.error.message === 'string') return event.response.error.message;
    } catch { /* Try the next SSE data line. */ }
  }
  return '';
}

function bufferedFailure(response, buffer, secrets) {
  const message = redact(parseSseFailure(buffer), secrets);
  const safe = {
    message: message || 'Upstream stream ended without a successful terminal event.',
    type: 'upstream_error',
    code: 'upstream_stream_failed'
  };
  json(response, 502, { error: safe });
}

async function upstreamError(response, upstream, secrets) {
  const chunks = [];
  let size = 0;
  for await (const chunk of upstream) {
    size += chunk.length;
    if (size > ERROR_LIMIT) {
      upstream.destroy();
      break;
    }
    chunks.push(chunk);
  }
  const status = upstream.statusCode || 502;
  if (status >= 300 && status < 400) {
    sendError(response, 502, 'Upstream redirect was refused.', 'upstream_redirect');
    return;
  }
  let error;
  try { error = JSON.parse(Buffer.concat(chunks).toString('utf8')).error; } catch { /* Non-JSON errors are deliberately summarized. */ }
  const fallback = `Upstream request failed (HTTP ${status}).`;
  const safe = { message: fallback, type: 'upstream_error', code: 'upstream_error' };
  if (error && typeof error === 'object') {
    for (const field of ['message', 'type', 'code', 'param']) {
      if (typeof error[field] === 'string') safe[field] = redact(error[field], secrets).slice(0, 16000);
    }
  }
  if (upstream.headers['retry-after']) response.setHeader('retry-after', upstream.headers['retry-after']);
  json(response, status >= 400 && status <= 599 ? status : 502, { error: safe });
}

async function proxyRequest(request, response, pathname, alias, route, provider, nativeAuth, body, secrets, logger, requestTransport) {
  const target = new URL(provider.base);
  target.pathname = `${provider.base.pathname}${pathname.slice('/v1'.length)}`;
  const transport = requestTransport || ((url, options, callback) => (url.protocol === 'https:' ? https : http).request(url, options, callback));
  const proxyAgent = provider.proxy ? createChatGPTProxyAgent(provider.proxy) : undefined;
  let upstreamRequest;
  let upstreamResponse;
  let timedOut = false;
  let logged = false;
  const log = (status) => {
    if (logged) return;
    logged = true;
    safeLog(logger, { model_alias: alias, provider: route.provider, status });
  };
  const abort = () => {
    upstreamResponse?.destroy();
    upstreamRequest?.destroy();
    proxyAgent?.destroy();
  };
  const onClientClose = () => {
    if (!response.writableEnded) {
      abort();
      log(499);
    }
  };
  response.once('close', onClientClose);
  const timer = setTimeout(() => {
    timedOut = true;
    sendError(response, 504, 'Upstream request exceeded the 30 minute limit.', 'upstream_timeout');
    abort();
    log(504);
  }, UPSTREAM_TIMEOUT);
  timer.unref();
  try {
    upstreamResponse = await new Promise((resolveResponse, rejectResponse) => {
      upstreamRequest = transport(target, {
        method: 'POST',
        headers: requestHeaders(request.headers, provider, nativeAuth, body.length),
        ...(proxyAgent ? { agent: proxyAgent } : {}),
      }, resolveResponse);
      upstreamRequest.once('error', rejectResponse);
      upstreamRequest.end(body);
      if (response.destroyed) upstreamRequest.destroy();
    });
    const status = upstreamResponse.statusCode || 502;
    if (status < 200 || status >= 300) {
      await upstreamError(response, upstreamResponse, secrets);
      log(response.statusCode);
      return;
    }
    if (response.destroyed) {
      abort();
      return;
    }
    response.writeHead(status, responseHeaders(upstreamResponse.headers));
    response.flushHeaders();
    await pipeline(upstreamResponse, response);
    log(status);
  } catch (error) {
    safeLog(logger, { model_alias: alias, provider: route.provider, kind: 'unhandled_error', message: redact(String(error?.message || error), secrets).slice(0, 500) });
    if (!response.destroyed && !timedOut) {
      sendError(response, 502, 'Could not complete the upstream request.', 'upstream_connection_error');
      log(502);
    }
  } finally {
    clearTimeout(timer);
    response.removeListener('close', onClientClose);
    proxyAgent?.destroy();
  }
}

/** Construct a router without listening. API keys come only from options.env/process.env.
 * options.requestTransport is an in-process test injection; JSON/CLI cannot enable it.
 */
export async function createRouter(config, options = {}) {
  const env = options.env || process.env;
  const logger = options.logger || ((entry) => process.stdout.write(`${entry}\n`));
  const { providers, routes } = validateConfig(config, env);
  const token = (await readFile(config.auth_token_file, 'utf8')).trim();
  if (token.length < 24 || /\s/.test(token)) throw new Error('Local auth token must be at least 24 characters without whitespace.');
  const tokenHash = digest(token);
  const secrets = [token, ...[...providers.values()].map((provider) => provider.key)].filter(Boolean);
  const handle = async (request, response) => {
    const pathname = new URL(request.url, 'http://127.0.0.1').pathname;
    if (request.method === 'GET' && pathname === '/health') {
      json(response, 200, { status: 'ok', features: { chatgpt: true } });
      return;
    }
    if (pathname !== '/v1' && !pathname.startsWith('/v1/')) {
      sendError(response, 404, 'Endpoint not found.', 'not_found');
      return;
    }
    const authorization = request.headers.authorization;
    const bearerMatch = typeof authorization === 'string' ? /^Bearer ([A-Za-z0-9._~+/-]+=*)$/i.exec(authorization) : null;
    const candidate = bearerMatch?.[1] || '';
    const localBearerMatch = typeof authorization === 'string' ? /^Bearer (\S+)$/i.exec(authorization) : null;
    const localBearer = timingSafeEqual(digest(localBearerMatch?.[1] || ''), tokenHash);
    const localHeader = request.headers['x-local-router-key'];
    const validLocalHeader = typeof localHeader === 'string' && timingSafeEqual(digest(localHeader), tokenHash);
    if (!localBearer && !validLocalHeader) {
      response.setHeader('www-authenticate', 'Bearer');
      sendError(response, 401, 'Invalid local router token.', 'invalid_api_key');
      return;
    }
    if (pathname !== '/v1/models' && !POST_PATHS.has(pathname)) {
      sendError(response, 404, 'Endpoint not found.', 'not_found');
      return;
    }
    if (request.method === 'GET' && pathname === '/v1/models') {
      json(response, 200, { object: 'list', data: [...routes.keys()].map((id) => ({ id, object: 'model', created: 0, owned_by: 'local-router' })) });
      return;
    }
    if (request.method !== 'POST' || !POST_PATHS.has(pathname)) {
      sendError(response, 404, 'Endpoint not found.', 'not_found');
      return;
    }
    const raw = await readLimited(request, BODY_LIMIT);
    let input;
    try { input = JSON.parse(raw.toString('utf8')); } catch { throw new RequestError(400, 'Request body must contain valid JSON.', 'invalid_json'); }
    if (!input || typeof input !== 'object' || Array.isArray(input) || typeof input.model !== 'string') throw new RequestError(400, 'Request must specify a model alias.', 'invalid_model');
    const alias = input.model;
    const route = routes.get(alias);
    if (!route) throw new RequestError(400, 'Unknown model alias. Select a configured model.', 'model_not_found');
    let provider = providers.get(route.provider);
    let nativeAuth;
    let requestSecrets = secrets;
    if (provider.authMode === 'chatgpt' && !validLocalHeader) {
      throw new RequestError(401, 'ChatGPT routes require the local router authentication header.', 'chatgpt_auth_required');
    }
    if (provider.authMode === 'chatgpt') {
      let tokens;
      try { tokens = JSON.parse(await readFile(provider.authFile, 'utf8')).tokens; } catch { /* Report only the affected route. */ }
      if (!tokens || typeof tokens.access_token !== 'string' || !tokens.access_token || /\s/.test(tokens.access_token) || typeof tokens.account_id !== 'string' || !/^[\x21-\x7e]{1,512}$/.test(tokens.account_id)) {
        throw new RequestError(401, 'Plus login is unavailable. Sign in through the Plus instance; API-key models remain independent.', 'plus_login_required');
      }
      provider = { ...provider, key: tokens.access_token, accountId: tokens.account_id };
      requestSecrets = [...secrets, provider.key, provider.accountId];
      // Provider-private reasoning IDs and encrypted content cannot be replayed across providers.
      // Keep visible messages and tool call_id associations; let this model reason anew.
      if (Array.isArray(input.input)) {
        input.input = input.input.filter((item) => item?.type !== 'reasoning').map((item) => {
          if (!item || typeof item !== 'object') return item;
          const { id, ...rest } = item;
          return rest;
        });
      }
    }
    input.model = route.model;
    if (provider.requestProfile === 'mimo' && pathname === '/v1/responses') {
      // MiMo's Responses gateway rejects the OpenAI-hosted web_search tool.
      // Keep function/custom/namespace tools and all call associations intact.
      if (Array.isArray(input.tools)) {
        input.tools = input.tools.filter((tool) => tool?.type !== 'web_search' && tool?.type !== 'web_search_preview');
      }
      // MiMo is stateless: Codex sends the full visible history each round.
      delete input.previous_response_id;
      delete input.background;
      delete input.context_management;
    }
    if (pathname === '/v1/responses' && route.maxOutputTokens !== undefined) {
      // This upstream returns 503 when the requested budget exceeds its route limit.
      // Preserve smaller caller budgets and default omitted budgets to the tested limit.
      if (input.max_output_tokens === undefined || (typeof input.max_output_tokens === 'number' && input.max_output_tokens > route.maxOutputTokens)) {
        input.max_output_tokens = route.maxOutputTokens;
      }
    }
    const body = Buffer.from(JSON.stringify(input));
    await proxyRequest(request, response, pathname, alias, route, provider, nativeAuth, body, requestSecrets, logger, options.requestTransport);
  };
  const server = http.createServer((request, response) => {
    void handle(request, response).catch((error) => {
      if (error instanceof RequestError) sendError(response, error.status, error.message, error.code);
      else sendError(response, 500, 'Local router could not process the request.', 'router_error');
    });
  });
  server.requestTimeout = 0;
  server.headersTimeout = 65_000;
  server.keepAliveTimeout = 10_000;
  server.maxHeadersCount = 100;
  server.on('clientError', (_error, socket) => {
    if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n');
  });
  return server;
}

/** Construct and bind the router to its validated loopback address. */
export async function startRouter(config, options = {}) {
  const server = await createRouter(config, options);
  await new Promise((resolveListen, rejectListen) => {
    server.once('error', rejectListen);
    server.listen(config.port, config.host, () => {
      server.removeListener('error', rejectListen);
      resolveListen();
    });
  });
  return server;
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length !== 2 || args[0] !== '--config') throw new Error('Usage: node router.mjs --config <absolute-config.json>');
  const config = JSON.parse(await readFile(resolve(args[1]), 'utf8'));
  const server = await startRouter(config);
  process.stdout.write(`${JSON.stringify({ status: 'listening', host: '127.0.0.1', port: server.address().port })}\n`);
  let shuttingDown = false;
  const stop = () => {
    if (shuttingDown) return;
    shuttingDown = true;
    server.close(() => process.exit(0));
    setTimeout(() => { server.closeAllConnections(); process.exit(0); }, 5_000).unref();
  };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write('Local model router failed to start: ' + String(error?.message || error).replace(/[\r\n]/g, ' ').slice(0, 500) + '\n');
    process.exitCode = 1;
  });
}
