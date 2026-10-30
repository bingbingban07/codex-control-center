import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Readable } from 'node:stream';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { customToolNames, normalizeCustomResponse, CustomToolResponseTransform, compatibleRequest } from '../custom-tool-compat.mjs';

const names = customToolNames([{ type: 'namespace', name: 'functions', tools: [
  { type: 'custom', name: 'exec' }, { type: 'function', name: 'wait' },
] }]);
const code = 'text(await tools.exec_command({cmd:"Write-Output 测试"}));\n';
const call = { type: 'function_call', id: 'fc_1', call_id: 'call_1', name: 'exec', arguments: JSON.stringify({ input: code }) };

async function transform(text, sse = true, size = 1) {
  const bytes = Buffer.from(text), chunks = [];
  for (let i = 0; i < bytes.length; i += size) chunks.push(bytes.subarray(i, i + size));
  let output = '';
  for await (const chunk of Readable.from(chunks).pipe(new CustomToolResponseTransform(names, sse))) output += chunk;
  return output;
}
const frame = value => `event: ${value.type}\r\ndata: ${JSON.stringify(value)}\r\n\r\n`;
const events = text => text.split('\n\n').filter(Boolean).filter(f => f.includes('data: {')).map(f => JSON.parse(f.split('\n').find(l => l.startsWith('data: ')).slice(6)));

test('JSON repairs only declared custom names; preserves IDs and native tools', () => {
  const native = { type: 'custom_tool_call', name: 'exec', input: code, call_id: 'native' };
  const other = { ...call, name: 'wait' };
  const result = normalizeCustomResponse({ output: [call, native, other] }, names).output;
  assert.deepEqual(result[0], { type: 'custom_tool_call', id: 'fc_1', call_id: 'call_1', name: 'exec', input: code });
  assert.strictEqual(result[1], native); assert.strictEqual(result[2], other);
  assert.equal(customToolNames([{ type: 'custom', name: 'exec' }, { type: 'function', name: 'exec' }]).size, 0);
  assert.equal(normalizeCustomResponse({ output: [{ ...call, arguments: 'raw code' }] }, names).output[0].input, 'raw code');
  assert.equal(normalizeCustomResponse({ output: [{ ...call, arguments: '{"cmd":"do not infer a command"}' }] }, names).output[0].input, '{"cmd":"do not infer a command"}');
});

test('SSE fragmented UTF8 repairs added/delta/done/final events consistently', async () => {
  const args = call.arguments;
  const source = [
    { type: 'response.output_item.added', output_index: 0, sequence_number: 0, item: { ...call, arguments: '' } },
    { type: 'response.function_call_arguments.delta', item_id: 'fc_1', output_index: 0, sequence_number: 1, delta: args.slice(0, 18) },
    { type: 'response.function_call_arguments.delta', item_id: 'fc_1', output_index: 0, sequence_number: 2, delta: args.slice(18) },
    { type: 'response.function_call_arguments.done', item_id: 'fc_1', output_index: 0, sequence_number: 3, arguments: args },
    { type: 'response.output_item.done', output_index: 0, sequence_number: 4, item: call },
    { type: 'response.completed', sequence_number: 5, response: { output: [call] } },
  ].map(frame).join('') + 'data: [DONE]\r\n\r\n';
  const output = await transform(source);
  const parsed = events(output);
  assert.equal(parsed[0].item.type, 'custom_tool_call');
  assert.equal(parsed[1].type, 'response.custom_tool_call_input.delta');
  assert.equal(parsed[1].delta, code);
  assert.equal(parsed[2].type, 'response.custom_tool_call_input.done');
  assert.equal(parsed[2].input, code);
  assert.equal(parsed[3].item.input, code); assert.equal(parsed[4].response.output[0].input, code);
  assert.deepEqual(parsed.map(e => e.sequence_number), [0, 1, 2, 3, 4]);
  assert.ok(output.includes('data: [DONE]'));
});

test('native custom and ordinary function streaming stay intact; JSON buffers bounded', async () => {
  const ordinary = { type: 'response.function_call_arguments.delta', item_id: 'wait_1', delta: '{"id":1}', sequence_number: 0 };
  const native = { type: 'response.custom_tool_call_input.delta', item_id: 'native', delta: code, sequence_number: 1 };
  assert.deepEqual(events(await transform(frame(ordinary) + frame(native))), [ordinary, native]);
  const json = JSON.parse(await transform(JSON.stringify({ output: [call] }), false));
  assert.equal(json.output[0].input, code);
});

test('actual HTTP transport repairs SSE and JSON without altering the request or credentials', async t => {
  const received = [];
  const upstream = createServer((req, res) => {
    let data = '';req.on('data', c => data += c);req.on('end', () => {
      const request = JSON.parse(data);received.push(request);
      const body = request.stream ? frame({ type: 'response.output_item.done', item: call, sequence_number: 0 }) : JSON.stringify({ output: [call] });
      res.writeHead(200, { 'content-type': request.stream ? 'text/event-stream' : 'application/json', 'content-length': Buffer.byteLength(body) });res.end(body);
    });
  });
  upstream.listen(0, '127.0.0.1');await once(upstream, 'listening');
  t.after(() => { upstream.closeAllConnections();upstream.close(); });
  for (const stream of [false, true]) {
    const body = JSON.stringify({ tools: [{ type: 'custom', name: 'exec' }], stream, input: 'synthetic' });
    const output = await new Promise((resolve, reject) => {
      const req = compatibleRequest(new URL(`http://127.0.0.1:${upstream.address().port}/v1/responses`), { method: 'POST' }, async res => {
        try { assert.equal(res.headers['content-length'], undefined);let data = '';for await (const c of res) data += c;resolve(data); } catch(e) { reject(e); }
      });req.on('error', reject);req.end(body);
    });
    assert.deepEqual(received.at(-1), JSON.parse(body));
    assert.equal(stream ? events(output)[0].item.input : JSON.parse(output).output[0].input, code);
  }
});
