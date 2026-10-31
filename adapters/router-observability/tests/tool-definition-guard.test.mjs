import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { guardToolDefinitions, ToolDefinitionConflict, toolStructureSummary } from '../tool-definition-guard.mjs';
import { compatibleRequest } from '../custom-tool-compat.mjs';

const tool = { type: 'function', name: 'collaboration__followup_task', description: 'Follow up', parameters: { type: 'object', properties: { task: { type: 'string' } }, required: ['task'], additionalProperties: false }, strict: true };
test('GPT to standard Responses switch removes stale AdditionalTools only, preserving current tools and all executions', () => {
  const cua = { ...tool, name: 'mcp__cua_repl__js' };
  const message = { type: 'message', role: 'user', content: [{ type: 'input_text', text: 'synthetic context' }] };
  const call = { type: 'function_call', name: cua.name, call_id: 'prior-call', arguments: '{"task":"synthetic"}' };
  const output = { type: 'function_call_output', call_id: 'prior-call', output: 'preserve exact output' };
  const search = { type: 'tool_search_output', call_id: 'search', tools: [cua], status: 'completed', execution: 'client' };
  const body = { tools: [cua], input: [message, { type: 'additional_tools', role: 'developer', tools: [cua, { ...tool, name: 'no_longer_available' }] }, call, output, search], tool_choice: { type: 'function', name: cua.name } };
  const original = structuredClone(body), checked = guardToolDefinitions(body);
  assert.equal(checked.legacyRemoved, 1); assert.equal(checked.removed, 0);
  assert.deepEqual(checked.body.tools, [cua]);
  assert.deepEqual(checked.body.input, [message, call, output, search]);
  assert.strictEqual(checked.body.input[1], call); assert.strictEqual(checked.body.input[2], output);
  assert.strictEqual(checked.body.tool_choice,body.tool_choice); assert.deepEqual(body,original);
  assert.strictEqual(guardToolDefinitions(checked.body).body,checked.body);
  const summary=toolStructureSummary(body);
  assert.equal(summary.legacy_tool_items,1); assert.equal(summary.legacy_active_name_overlap,1);
});
test('Responses Lite without top-level tools keeps its AdditionalTools catalog byte-for-byte', () => {
  const body={input:[{type:'additional_tools',role:'developer',tools:[tool]}]};
  assert.strictEqual(guardToolDefinitions(body).body,body);
  const disabled={tools:[],input:body.input};
  assert.deepEqual(guardToolDefinitions(disabled).body.input,[]);
  assert.deepEqual(guardToolDefinitions(disabled).body.tools,[]);
});
test('upstream duplicate errors are preserved and reported as a boolean without retry or retained body',async t=>{
  let connections=0;const reports=[];
  const payload=JSON.stringify({error:{message:'Invalid request: duplicate tool name "synthetic-private-tool"',code:'invalid_request'}});
  const server=createServer((req,res)=>{connections++;req.resume();req.on('end',()=>{res.writeHead(400,{'content-type':'application/json'});res.end(payload);});});
  server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(()=>{server.closeAllConnections();server.close();});
  const result=await new Promise((accept,reject)=>{
    const req=compatibleRequest(new URL(`http://127.0.0.1:${server.address().port}/v1/responses`),{method:'POST'},async res=>{
      try{let body='';for await(const b of res)body+=b;accept({status:res.statusCode,body});}catch(error){reject(error);}
    },r=>reports.push(r));req.on('error',reject);req.end(JSON.stringify({input:'NEVER_RETAIN_SYNTHETIC_INPUT',tools:[]}));
  });
  assert.equal(result.status,400);assert.equal(result.body,payload);assert.equal(connections,1);
  assert.ok(reports.some(r=>r.upstream_duplicate_tool_error===true));
  assert.ok(!JSON.stringify(reports).includes('synthetic-private-tool'));
  assert.ok(!JSON.stringify(reports).includes('NEVER_RETAIN_SYNTHETIC_INPUT'));
});
test('diagnostics count duplicate leaf names across namespaces without retaining content', () => {
  const secret = 'NEVER_RETAIN_SYNTHETIC_CONTENT';
  const summary = toolStructureSummary({ input: secret, tools: [
    { type: 'namespace', name: 'one', tools: [{ ...tool, name: secret }] },
    { type: 'namespace', name: 'two', tools: [{ ...tool, name: secret }] },
    { type: 'function', function: { name: secret, description: secret, parameters: {} } },
  ] });
  assert.equal(summary.repeated_leaf_names, 2); assert.equal(summary.repeated_flat_names, 0);
  assert.equal(summary.nested_function_shape, 1); assert.equal(summary.functions, 3);
  assert.ok(!JSON.stringify(summary).includes(secret));
});
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
  const historical={type:'additional_tools',role:'developer',tools:[tool]};
  assert.equal((await call({tools:[tool],input:[historical,{type:'function_call_output',call_id:'keep',output:'full content'}]})).status,200);
  assert.deepEqual(received.input,[{type:'function_call_output',call_id:'keep',output:'full content'}]);
});
