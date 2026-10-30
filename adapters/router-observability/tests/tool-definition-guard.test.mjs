import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { guardToolDefinitions, ToolDefinitionConflict } from '../tool-definition-guard.mjs';
import { compatibleRequest } from '../custom-tool-compat.mjs';

const tool = { type: 'function', name: 'collaboration__followup_task', description: 'Follow up', parameters: { type: 'object', properties: { task: { type: 'string' } }, required: ['task'], additionalProperties: false }, strict: true };
test('observed duplicate tool name is deduplicated without losing any schema or history', () => {
  const body = { tools: [tool, structuredClone(tool)], input: [{ type: 'function_call', name: tool.name, call_id: 'existing', arguments: '{"task":"synthetic"}' }], tool_choice: { type: 'function', name: tool.name } };
  const original = structuredClone(body), checked = guardToolDefinitions(body);
  assert.equal(checked.removed, 1); assert.deepEqual(checked.body.tools, [tool]);
  assert.strictEqual(checked.body.input, body.input); assert.strictEqual(checked.body.tool_choice, body.tool_choice);
  assert.deepEqual(body, original);
  assert.equal(guardToolDefinitions(checked.body).removed, 0);
});
test('recursive repeated namespaces merge unique tools and remove only exact duplicates', () => {
  const ns = tools => ({ type: 'namespace', name: 'collaboration', description: 'Task tools', tools });
  const body = { tools: [ns([tool]), ns([structuredClone(tool), { ...tool, name: 'wait' }])] };
  const checked = guardToolDefinitions(body);
  assert.equal(checked.removed, 2);
  assert.deepEqual(checked.body.tools, [ns([tool, { ...tool, name: 'wait' }])]);
  assert.equal(body.tools.length, 2); assert.equal(body.tools[0].tools.length, 1);
  assert.equal(guardToolDefinitions({ tools: [ns([tool]), { type: 'namespace', name: 'another', tools: [tool] }] }).removed, 0);
});
test('different schemas or custom/function conflicts are refused, never silently dropped', () => {
  for (const second of [{ ...tool, parameters: { type: 'object', properties: { other: { type: 'number' } } } }, { type: 'custom', name: tool.name }, { ...tool, strict: false }])
    assert.throws(() => guardToolDefinitions({ tools: [tool, second] }), ToolDefinitionConflict);
  assert.throws(() => guardToolDefinitions({ tools: [{ type: 'namespace', name: 'n', tools: [tool] }, { type: 'namespace', name: 'n', description: 'different', tools: [] }] }), ToolDefinitionConflict);
});
test('keys are compared structurally, hosted tools and unchanged requests remain intact', () => {
  const reordered = { strict: true, parameters: { additionalProperties: false, required: ['task'], properties: { task: { type: 'string' } }, type: 'object' }, description: 'Follow up', name: tool.name, type: 'function' };
  assert.equal(guardToolDefinitions({ tools: [tool, reordered] }).removed, 1);
  const body = { tools: [{ type: 'web_search' }, { type: 'custom', name: 'exec' }, tool] };
  assert.strictEqual(guardToolDefinitions(body).body, body);
});
test('flattened namespace collisions are caught without aliasing to another tool', () => {
  const inner = { ...tool, name: 'followup_task' };
  const body = { tools: [tool, { type: 'namespace', name: 'collaboration', tools: [inner] }] };
  const original = structuredClone(body);
  assert.throws(() => guardToolDefinitions(body), ToolDefinitionConflict);
  assert.deepEqual(body, original);
  assert.throws(() => guardToolDefinitions({ tools: [
    { type: 'namespace', name: 'a__b', tools: [{ ...tool, name: 'c' }] },
    { type: 'namespace', name: 'a', tools: [{ ...tool, name: 'b__c' }] },
  ] }), ToolDefinitionConflict);
});
test('pathological nesting and excessive definitions fail bounded validation', () => {
  let tools = [tool]; for (let i = 0; i < 26; i++) tools = [{ type: 'namespace', name: 'n' + i, tools }];
  assert.throws(() => guardToolDefinitions({ tools }), ToolDefinitionConflict);
  assert.throws(() => guardToolDefinitions({ tools: Array.from({ length: 20001 }, (_, i) => ({ ...tool, name: 'f' + i })) }), ToolDefinitionConflict);
});
test('HTTP transport adjusts byte length, keeps all content and blocks conflict before connection', async t => {
  let calls = 0, received;
  const upstream = createServer((req, res) => {
    calls++; let raw = ''; req.on('data', b => raw += b); req.on('end', () => {
      received = JSON.parse(raw); assert.equal(Number(req.headers['content-length']), Buffer.byteLength(raw));
      res.writeHead(200, { 'content-type': 'application/json' }); res.end('{"output":[]}');
    });
  });
  upstream.listen(0, '127.0.0.1'); await once(upstream, 'listening');
  t.after(() => { upstream.closeAllConnections(); upstream.close(); });
  const call = body => new Promise((accept, reject) => {
    const bytes = Buffer.from(JSON.stringify(body));
    const request = compatibleRequest(new URL(`http://127.0.0.1:${upstream.address().port}/v1/responses`), { method: 'POST', headers: { 'content-length': bytes.length, authorization: 'Bearer synthetic' } }, async res => {
      let raw = ''; for await (const b of res) raw += b;
      accept({ status: res.statusCode, body: JSON.parse(raw) });
    }); request.on('error', reject);
    request.write(bytes.subarray(0, 13)); request.end(bytes.subarray(13));
  });
  assert.equal((await call({ input: '完整明文', tools: [tool, tool] })).status, 200);
  assert.deepEqual(received.tools, [tool]); assert.equal(received.input, '完整明文');
  const before = calls;
  const failed = await call({ tools: [tool, { ...tool, description: 'incompatible' }] });
  assert.equal(failed.status, 400); assert.equal(failed.body.error.code, 'tool_definition_conflict');
  assert.equal(calls, before);
});
