import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { test } from 'node:test';
import { RotatingNdjsonWriter, createObservedRouter } from '../observer.mjs';

const SSE_BODY = 'data: first\n\ndata: [DONE]\n\n';

async function makeMockRouter() {
  const directory = await mkdtemp(join(tmpdir(), 'router-observer-test-'));
  const modulePath = join(directory, 'mock-router.mjs');
  const source = `
import http from 'node:http';
export async function createRouter(_config, options = {}) {
  return http.createServer((request, response) => {
    const chunks = [];
    request.on('data', (chunk) => chunks.push(chunk));
    request.on('end', () => {
      if (request.headers['x-test-mode'] === 'cancel') {
        response.writeHead(200, { 'content-type': 'text/event-stream' });
        response.write('data: first\\\\n\\\\n');
        const timer = setTimeout(() => response.write('data: late\\\\n\\\\n'), 30_000);
        timer.unref();
        response.once('close', () => clearTimeout(timer));
        return;
      }
      options.logger?.(JSON.stringify({ model_alias: 'mock-model', provider: 'mock-provider', status: 200 }));
      response.writeHead(200, { 'content-type': 'text/event-stream' });
      response.end(${JSON.stringify(SSE_BODY)});
    });
  });
}
`;
  await writeFile(modulePath, source, 'utf8');
  return { directory, modulePath };
}

async function listen(server) {
  await new Promise((ready, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      server.removeListener('error', reject);
      ready();
    });
  });
  return server.address().port;
}

async function close(server) {
  server.closeAllConnections();
  await new Promise((ready) => server.close(ready));
}

function waitForEvent(timeoutMs = 3_000) {
  let resolveEvent;
  let rejectEvent;
  const promise = new Promise((ready, reject) => {
    resolveEvent = ready;
    rejectEvent = reject;
  });
  const timer = setTimeout(() => rejectEvent(new Error('observer event timed out')), timeoutMs);
  timer.unref();
  return {
    promise,
    emit(value) {
      clearTimeout(timer);
      resolveEvent(value);
    },
  };
}

test('mock SSE response and request body remain unchanged', async (t) => {
  const mock = await makeMockRouter();
  t.after(() => rm(mock.directory, { recursive: true, force: true }));
  const waiter = waitForEvent();
  let routerLog = '';
  const server = await createObservedRouter({}, {
    routerPath: mock.modulePath,
    logger: (entry) => { routerLog += `${entry}\n`; },
    writeEvent: waiter.emit,
  });
  t.after(() => close(server));

  const port = await listen(server);
  const response = await fetch(`http://127.0.0.1:${port}/v1/responses`, {
    method: 'POST',
    body: 'mock-request-body-must-not-be-consumed',
  });
  const body = await response.text();
  const event = await waiter.promise;

  assert.equal(response.status, 200);
  assert.equal(body, SSE_BODY);
  assert.equal(event.status, 200);
  // The engine reports the served model; the observer must not keep the model permanently null.
  assert.equal(event.model, 'mock-model');
  assert.equal(event.provider, 'mock-provider');
  assert.equal(event.cancelled, false);
  assert.equal(typeof event.request_id, 'string');
  assert.ok(event.total_ms >= 0);
  assert.match(routerLog, /mock-model/);
  // The observation record never carries the request body.
  assert.ok(!JSON.stringify(event).includes('mock-request-body-must-not-be-consumed'));
});

test('health and model listing probes are not recorded as model requests', async (t) => {
  const mock = await makeMockRouter();
  t.after(() => rm(mock.directory, { recursive: true, force: true }));
  const events = [];
  const server = await createObservedRouter({}, {
    routerPath: mock.modulePath,
    writeEvent: (event) => { events.push(event); },
  });
  t.after(() => close(server));

  const port = await listen(server);
  await fetch(`http://127.0.0.1:${port}/health`);
  await fetch(`http://127.0.0.1:${port}/v1/models`);
  await new Promise((ready) => setTimeout(ready, 30));
  assert.equal(events.length, 0);

  await fetch(`http://127.0.0.1:${port}/v1/responses`, { method: 'POST', body: 'x' });
  await new Promise((ready) => setTimeout(ready, 30));
  assert.equal(events.length, 1);
  assert.equal(events[0].model, 'mock-model');
});

test('event logging failure does not interrupt SSE forwarding', async (t) => {
  const mock = await makeMockRouter();
  t.after(() => rm(mock.directory, { recursive: true, force: true }));
  const server = await createObservedRouter({}, {
    routerPath: mock.modulePath,
    writeEvent: () => { throw new Error('simulated log failure'); },
  });
  t.after(() => close(server));

  const port = await listen(server);
  const response = await fetch(`http://127.0.0.1:${port}/v1/responses`, { method: 'POST', body: 'x' });
  const body = await response.text();

  assert.equal(response.status, 200);
  assert.equal(body, SSE_BODY);
});

test('client cancellation emits one bounded metadata record without breaking shutdown', async (t) => {
  const mock = await makeMockRouter();
  t.after(() => rm(mock.directory, { recursive: true, force: true }));
  const events = [];
  const waiter = waitForEvent();
  const server = await createObservedRouter({}, {
    routerPath: mock.modulePath,
    writeEvent: (event) => {
      events.push(event);
      waiter.emit(event);
    },
  });
  t.after(() => close(server));

  const port = await listen(server);
  const controller = new AbortController();
  const response = await fetch(`http://127.0.0.1:${port}/v1/responses`, {
    method: 'POST',
    headers: { 'x-test-mode': 'cancel' },
    body: 'x',
    signal: controller.signal,
  });
  const reader = response.body.getReader();
  const first = await reader.read();
  assert.equal(first.done, false);
  controller.abort();
  const event = await waiter.promise;
  await new Promise((ready) => setTimeout(ready, 25));

  assert.equal(events.length, 1);
  assert.equal(event.cancelled, true);
  assert.equal(event.status, 499);
  assert.equal(event.model, null);
});

test('NDJSON writer rotates to a bounded set of files and never throws on write failure', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'router-events-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const eventsPath = join(directory, 'events.ndjson');
  const writer = new RotatingNdjsonWriter(eventsPath, { maxBytes: 1024, maxBackups: 2 });

  for (let index = 0; index < 20; index += 1) {
    assert.equal(writer.write({ index, padding: 'x'.repeat(80) }), true);
  }

  const files = (await readdir(directory)).sort();
  assert.deepEqual(files, ['events.ndjson', 'events.ndjson.1', 'events.ndjson.2']);
  for (const file of files) {
    const content = await readFile(join(directory, file), 'utf8');
    for (const line of content.trim().split('\n')) JSON.parse(line);
    assert.ok(Buffer.byteLength(content) < 2048);
  }

  const failing = new RotatingNdjsonWriter(join(directory, 'missing', '..', 'locked', 'events.ndjson'));
  const originalMkdirError = failing.write({ impossible: true });
  assert.equal(typeof originalMkdirError, 'boolean');
});

test('observer module can be imported by absolute file URL', async () => {
  const moduleUrl = new URL('../observer.mjs', import.meta.url).href;
  const loaded = await import(moduleUrl);
  assert.equal(typeof loaded.createObservedRouter, 'function');
});

