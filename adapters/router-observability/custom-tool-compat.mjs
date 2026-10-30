import http from 'node:http';
import https from 'node:https';
import { Transform, Writable, Readable } from 'node:stream';
import { guardToolDefinitions, ToolDefinitionConflict } from './tool-definition-guard.mjs';
import { pipeline } from 'node:stream/promises';
import { StringDecoder } from 'node:string_decoder';

// Only repair names explicitly declared as custom in this request. Never guess tools,
// evaluate generated code, change permissions, or log request/response contents.
export function customToolNames(tools) {
  const custom = new Set(), ordinary = new Set();
  function visit(items) {
    for (const tool of items || []) {
      if (tool?.type === 'namespace') visit(tool.tools);
      else if (typeof tool?.name === 'string') {
        (tool.type === 'custom' ? custom : ordinary).add(tool.name);
      }
    }
  }
  visit(tools);
  for (const name of ordinary) custom.delete(name);
  return custom;
}

function unwrap(argumentsText) {
  try {
    const value = JSON.parse(argumentsText);
    if (value && typeof value === 'object' && !Array.isArray(value) &&
        Object.keys(value).length === 1 && typeof value.input === 'string') return value.input;
  } catch { /* Some providers already return the freeform input as raw text. */ }
  return argumentsText;
}

export function normalizeCustomItem(item, names) {
  if (item?.type !== 'function_call' || !names.has(item.name)) return item;
  const { arguments: args, ...rest } = item;
  return { ...rest, type: 'custom_tool_call', input: unwrap(typeof args === 'string' ? args : '') };
}

export function normalizeCustomResponse(value, names) {
  if (!value || !Array.isArray(value.output)) return value;
  return { ...value, output: value.output.map(item => normalizeCustomItem(item, names)) };
}

export class CustomToolResponseTransform extends Transform {
  constructor(names, sse) {
    super(); this.names = names; this.sse = sse; this.decoder = new StringDecoder('utf8');
    this.pending = ''; this.calls = new Map(); this.sequence = 0;
  }
  _transform(chunk, encoding, callback) {
    try {
      this.pending += this.decoder.write(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk, encoding));
      if (Buffer.byteLength(this.pending) > 64 * 1024 * 1024) throw new Error('tool_response_too_large');
      if (this.sse) this.drain();
      callback();
    } catch (error) { callback(error); }
  }
  _flush(callback) {
    try {
      this.pending += this.decoder.end();
      if (this.sse) { this.drain(); if (this.pending) this.frame(this.pending); }
      else this.push(JSON.stringify(normalizeCustomResponse(JSON.parse(this.pending), this.names)));
      callback();
    } catch (error) { callback(error); }
  }
  drain() {
    let match;
    while ((match = /\r?\n\r?\n/.exec(this.pending))) {
      const frame = this.pending.slice(0, match.index);
      this.pending = this.pending.slice(match.index + match[0].length);
      this.frame(frame);
    }
  }
  emitEvent(event, lines) {
    if (typeof event.sequence_number === 'number') event.sequence_number = this.sequence++;
    const metadata = lines.filter(line => !line.startsWith('data:') && !line.startsWith('event:'));
    if (lines.some(line => line.startsWith('event:'))) metadata.push(`event: ${event.type}`);
    this.push([...metadata, `data: ${JSON.stringify(event)}`, '', ''].join('\n'));
  }
  frame(frame) {
    const lines = frame.split(/\r?\n/);
    const data = lines.filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n');
    let event;
    try { event = JSON.parse(data); } catch { this.push(frame + '\n\n'); return; }
    const item = event.item;
    if (event.type === 'response.output_item.added' && item?.type === 'function_call' && this.names.has(item.name)) {
      this.calls.set(item.id, { arguments: '', emitted: false });
      event = { ...event, item: normalizeCustomItem(item, this.names) };
    }
    const state = this.calls.get(event.item_id);
    if (state && event.type === 'response.function_call_arguments.delta') {
      state.arguments += event.delta || '';
      if (Buffer.byteLength(state.arguments) > 16 * 1024 * 1024) throw new Error('tool_input_too_large');
      return;
    }
    if (state && event.type === 'response.function_call_arguments.done') {
      const input = unwrap(typeof event.arguments === 'string' ? event.arguments : state.arguments);
      this.emitEvent({ ...event, type: 'response.custom_tool_call_input.delta', delta: input, arguments: undefined }, lines);
      event = { ...event, type: 'response.custom_tool_call_input.done', input, arguments: undefined };
      state.emitted = true;
    }
    if (event.type === 'response.output_item.done') {
      if (item?.type === 'function_call' && this.names.has(item.name)) event = { ...event, item: normalizeCustomItem(item, this.names) };
      this.calls.delete(item?.id);
    }
    if (event.response) event = { ...event, response: normalizeCustomResponse(event.response, this.names) };
    this.emitEvent(event, lines);
  }
}

// The existing engine sends its validated request body with end(body). Wrap only the
// response to /responses; native custom calls, non-custom tools and errors pass through.
export function compatibleRequest(url, options, callback, report = () => {}) {
  let names = new Set();
  let upstreamRequest;
  const chunks = []; let size = 0;
  const onResponse = upstream => {
    const type = String(upstream.headers['content-type'] || '');
    const encoding = upstream.headers['content-encoding'];
    if (!names.size || !url.pathname.endsWith('/responses') || upstream.statusCode < 200 ||
        upstream.statusCode >= 300 || (encoding && encoding !== 'identity') ||
        (!type.includes('json') && !type.includes('text/event-stream'))) { callback(upstream); return; }
    const transform = new CustomToolResponseTransform(names, type.includes('text/event-stream'));
    transform.statusCode = upstream.statusCode;
    transform.headers = { ...upstream.headers };
    delete transform.headers['content-length'];
    transform.on('close', () => { if (!upstream.complete) upstream.destroy(); });
    callback(transform);
    void pipeline(upstream, transform).catch(() => { /* Engine observes stream failure. */ });
  };
  // Do not connect to any upstream until the entire bounded body is checked.
  const request = new Writable({ autoDestroy: false,
    write(chunk, encoding, done) {
      size += chunk.length;
      if (size > 64 * 1024 * 1024) { done(new Error('tool_request_too_large')); return; }
      chunks.push(Buffer.from(chunk)); done();
    },
    final(done) {
      let body = Buffer.concat(chunks); chunks.length = 0;
      if (url.pathname.endsWith('/responses') || url.pathname.includes('/responses/')) {
        try {
          const parsed = JSON.parse(body.toString('utf8'));
          const checked = guardToolDefinitions(parsed);
          names = customToolNames(checked.body.tools);
          if (checked.removed) { body = Buffer.from(JSON.stringify(checked.body)); report({ removed: checked.removed }); }
        } catch (error) {
          if (error instanceof ToolDefinitionConflict) {
            report({ conflict: true });
            const rejected = Readable.from([Buffer.from(JSON.stringify({ error: { message: error.message, code: error.code, type: 'invalid_request_error' } }))]);
            rejected.statusCode = 400; rejected.headers = { 'content-type': 'application/json' };
            callback(rejected); done(); return;
          }
          // Malformed JSON remains the engine's responsibility; never swallow a
          // guard failure or log the request body.
          if (!(error instanceof SyntaxError)) { done(error); return; }
        }
      }
      const headers = { ...options.headers };
      for (const key of Object.keys(headers)) if (key.toLowerCase() === 'content-length') delete headers[key];
      delete headers['transfer-encoding']; headers['content-length'] = body.length;
      upstreamRequest = (url.protocol === 'https:' ? https : http).request(url, { ...options, headers }, onResponse);
      upstreamRequest.on('error', error => request.destroy(error));
      upstreamRequest.end(body); done();
    },
    destroy(error, done) { chunks.length = 0; upstreamRequest?.destroy(); done(error); },
  });
  return request;
}
