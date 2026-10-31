import { isDeepStrictEqual } from 'node:util';

// Bounded, content-free diagnostics. Never retain names, descriptions, schemas,
// input/history, argument values or authentication data.
export function toolStructureSummary(body) {
  const summary = { fields: Object.keys(body || {}).filter(k => /^[a-z_]{1,64}$/i.test(k) && !/auth|secret|credential/i.test(k)).slice(0,60),
    input_types: {}, content_types: {}, legacy_tool_items: 0, legacy_named_tools: 0, legacy_active_name_overlap: 0,
    metadata_fields: Object.keys(body?.client_metadata || {}).filter(k => /^[a-z_]{1,64}$/i.test(k) && !/auth|secret|credential/i.test(k)).slice(0,40),
    tools_type: Array.isArray(body?.tools) ? 'array' : body?.tools === undefined ? 'absent' : typeof body.tools,
    top_level: Array.isArray(body?.tools) ? body.tools.length : 0,
    functions: 0, custom: 0, namespaces: 0, hosted: 0, other: 0,
    nested_function_shape: 0, repeated_leaf_names: 0, repeated_flat_names: 0, max_depth: 0 };
  const leaves = new Set(), flat = new Set(); let visited = 0;
  const knownTypes = new Set(['message','function_call','function_call_output','custom_tool_call','custom_tool_call_output','reasoning','input_text','output_text','text','input_image','tool_definitions','tool_definition','namespace','function','custom','additional_tools','tool_search_output']);
  const count = (map, value) => { const key = knownTypes.has(value) ? value : 'other'; map[key] = (map[key] || 0) + 1; };
  if (Array.isArray(body?.input)) for (const item of body.input.slice(0,20000)) {
    count(summary.input_types, item?.type);
    if (Array.isArray(item?.content)) for (const c of item.content.slice(0,20000)) count(summary.content_types,c?.type);
  }
  function walk(items, path = []) {
    if (!Array.isArray(items) || path.length > 24) return;
    summary.max_depth = Math.max(summary.max_depth, path.length);
    for (const item of items) {
      if (++visited > 20000) return;
      if (!item || typeof item !== 'object') { summary.other++; continue; }
      if (item.type === 'namespace') { summary.namespaces++; walk(item.tools, [...path, typeof item.name === 'string' ? item.name : '']); continue; }
      if (item.type === 'function') summary.functions++;
      else if (item.type === 'custom') summary.custom++;
      else { summary.hosted++; continue; }
      let name = item.name;
      if (typeof name !== 'string' && typeof item.function?.name === 'string') { name = item.function.name; summary.nested_function_shape++; }
      if (typeof name !== 'string') continue;
      if (leaves.has(name)) summary.repeated_leaf_names++; leaves.add(name);
      const projected = [...path, name].join('__');
      if (flat.has(projected)) summary.repeated_flat_names++; flat.add(projected);
    }
  }
  walk(body?.tools);
  let legacyVisited = 0;
  function legacy(items, path = []) {
    if (!Array.isArray(items) || path.length > 24) return;
    for (const item of items) {
      if (++legacyVisited > 20000) return;
      if (item?.type === 'namespace') legacy(item.tools,[...path,item.name]);
      else if (['function','custom'].includes(item?.type) && typeof item.name === 'string') {
        summary.legacy_named_tools++;
        if (flat.has([...path,item.name].join('__'))) summary.legacy_active_name_overlap++;
      }
    }
  }
  if (Array.isArray(body?.input)) for (const item of body.input.slice(0,20000)) {
    if (item?.type === 'additional_tools' && Array.isArray(item.tools)) { summary.legacy_tool_items++; legacy(item.tools); }
  }
  return summary;
}

export class ToolDefinitionConflict extends Error {
  constructor() { super('工具列表存在同一命名空间内的冲突声明；请求未发往模型。请刷新该聊天的工具或重新加载对应插件后重试。'); this.code = 'tool_definition_conflict'; }
}

// Namespace identity is a path, never an underscore-flattened string. Remove only
// identical definitions; merge repeated namespace containers without losing tools.
// A different schema under the same identity cannot safely be guessed or renamed.
export function guardToolDefinitions(body) {
  if (!Array.isArray(body?.tools)) return { body, removed: 0 };
  // Responses Lite carries prompt-only declarations in input.additional_tools,
  // while standard Responses uses the current top-level tools list. Codex can
  // retain an old declaration when a chat switches protocol/model. Do not replay
  // that stale tool catalog alongside the authoritative current tools: gateways
  // may register both and report a duplicate. Never resurrect removed tools or
  // discard actual messages, calls, call outputs or tool_search_output results.
  let legacyRemoved = 0;
  const input = Array.isArray(body.input) ? body.input.filter(item => {
    if (item?.type !== 'additional_tools' || !Array.isArray(item.tools)) return true;
    legacyRemoved++; return false;
  }) : body.input;
  let removed = 0, examined = 0;
  function normalize(items, depth = 0) {
    if (depth > 24) throw new ToolDefinitionConflict();
    const result = [], names = new Map();
    for (const item of items) {
      if (++examined > 20000) throw new ToolDefinitionConflict();
      if (!item || typeof item !== 'object' || typeof item.name !== 'string' || !['namespace', 'function', 'custom'].includes(item.type)) { result.push(item); continue; }
      if (item.type === 'namespace' && !Array.isArray(item.tools)) throw new ToolDefinitionConflict();
      const key = item.name;
      const previous = names.get(key);
      if (!previous) { const copy = { ...item }; names.set(key, copy); result.push(copy); continue; }
      if (item.type === 'namespace' && previous.type === 'namespace') {
        const { tools: oldTools, ...oldMeta } = previous;
        const { tools: newTools, ...newMeta } = item;
        if (!isDeepStrictEqual(oldMeta, newMeta)) throw new ToolDefinitionConflict();
        previous.tools = [...oldTools, ...newTools]; removed++; continue;
      }
      if (!isDeepStrictEqual(previous, item)) throw new ToolDefinitionConflict();
      removed++;
    }
    for (const item of result) if (item?.type === 'namespace') item.tools = normalize(item.tools, depth + 1);
    return result;
  }
  const tools = normalize(body.tools);
  // Some compatibility gateways flatten namespaces as namespace__tool. Two
  // distinct identities that flatten to the same token cannot be disambiguated
  // without changing tool routing. Refuse locally instead of silently deleting
  // or renaming one definition and risking a call to the wrong tool.
  const projected = new Set();
  function checkProjected(items, path = []) {
    for (const item of items) {
      if (item?.type === 'namespace') checkProjected(item.tools, [...path, item.name]);
      else if (['function', 'custom'].includes(item?.type) && typeof item.name === 'string') {
        const name = [...path, item.name].join('__');
        if (projected.has(name)) throw new ToolDefinitionConflict();
        projected.add(name);
      }
    }
  }
  checkProjected(tools);
  return { body: removed || legacyRemoved ? { ...body, tools, ...(legacyRemoved ? { input } : {}) } : body, removed, legacyRemoved };
}
