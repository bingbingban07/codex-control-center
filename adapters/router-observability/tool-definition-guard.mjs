import { isDeepStrictEqual } from 'node:util';

export class ToolDefinitionConflict extends Error {
  constructor() { super('工具列表存在同一命名空间内的冲突声明；请求未发往模型。请刷新该聊天的工具或重新加载对应插件后重试。'); this.code = 'tool_definition_conflict'; }
}

// Namespace identity is a path, never an underscore-flattened string. Remove only
// identical definitions; merge repeated namespace containers without losing tools.
// A different schema under the same identity cannot safely be guessed or renamed.
export function guardToolDefinitions(body) {
  if (!Array.isArray(body?.tools)) return { body, removed: 0 };
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
  return { body: removed ? { ...body, tools } : body, removed };
}
