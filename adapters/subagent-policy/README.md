# Codex native subagent policy hook

## Model identity and native validation

The `model` argument is the complete catalog slug, not `provider + "/" + model`.
Provider is a separate route validation field. For example, use
`MiniMax-M3.1-Flash` and `commandcode/deepseek-v4.1-flash` verbatim; do not add
another provider prefix. Managed AGENTS instructions include exact JSON arguments.

Codex's spawn description and `Unknown model` error list only the first five
picker-visible candidates. Native validation searches the full model catalog.
An omitted name alone does not prove the model is unsupported. Check the actual
submitted slug first. A creation error with no agent ID cannot be closed.

`tests/local_codex_smoke.py` uses the installed Codex CLI, an isolated CODEX_HOME,
seven synthetic catalog entries and a loopback Responses server. It verifies
native creation of both exact aliases at positions six and seven, hook rewriting,
agent IDs and lifecycle events. It never contacts paid model endpoints.

The `failure_fallback` mode uses the primary model for every concurrent child;
the configured overflow is permitted once only after a confirmed primary failure.
The primary-slot lifecycle below describes `concurrent` mode.

This adapter pins native subagents to two model assignments. One shared
primary slot is reserved for the first working agent; later spawns use the
configured overflow assignment. Existing children keep their original
provider, model, and effort.

## Policy and state

The UI writes the policy at:

    <CODEX_HOME>/control-center/subagent-policy.json

Example:

~~~json
{
  "version": 1,
  "revision": 12,
  "enabled": true,
  "router_config_path": "C:\\Users\\you\\.codex\\router.json",
  "primary": {
    "provider": "provider-a",
    "model": "alias-A",
    "effort": "max"
  },
  "overflow": {
    "provider": "provider-b",
    "model": "alias-B",
    "effort": "max"
  }
}
~~~

The revision and router path are optional. When router_config_path is present,
each spawn and resume verifies router.json.routes[model].provider against the
selected provider. If the route is missing or changed, the operation is denied
with “Key来源映射变化，重新应用策略”. The hook does not choose another route
or include router contents in its message.

SQLite state lives at
<CODEX_HOME>/control-center/subagent-policy.sqlite. The atomically exported UI
snapshot lives at
<CODEX_HOME>/control-center/subagent-policy-status.json and has these fields:

~~~json
{
  "enabled": true,
  "policy_revision": 12,
  "last_hook_event_at": "2026-10-26T00:00:00.000Z",
  "active_count": 1,
  "pending_count": 1,
  "primary_owner": "pending:call-id",
  "allocations": [
    {
      "agent_id": "agent-id",
      "parent_id": "session-id",
      "model": "alias-A",
      "provider": "provider-a",
      "effort": "max",
      "slot": "primary",
      "status": "active"
    }
  ]
}
~~~

last_hook_event_at changes only after a real hook event. The --status command
does not simulate an event or change assignments. A pending primary reservation
appears as pending:<tool_use_id>, so the UI does not show that slot as free.
The export contains at most 200 active, pending, and most recent idle
assignments, in that priority order; closed rows are omitted. SQLite retains
sticky active and idle children, and prunes closed history to the newest 1,000
rows. Snapshot replacement occurs under the SQLite writer lock before commit,
preventing an older concurrent event from publishing last.

The adapter does not persist prompt text, message bodies, auth values, or
router contents. Its compact hook-event history is capped at 200 rows.

## Hook registration

Core/UI generates hooks.json. Use synchronous command hooks with a 10-second
timeout:

- PreToolUse and PostToolUse use matcher
  (spawn_agent|send_input|close_agent|resume_agent)$|^Agent$.
- SubagentStart, SubagentStop, and SessionEnd have no matcher.
- Provide the absolute Python and script paths. On Windows, set commandWindows
  to the fully quoted command, for example:

~~~json
{
  "command": "\"C:\\Python314\\python.exe\" \"C:\\path\\to\\policy_hook.py\" --codex-home \"C:\\Users\\you\\.codex\"",
  "commandWindows": "\"C:\\Python314\\python.exe\" \"C:\\path\\to\\policy_hook.py\" --codex-home \"C:\\Users\\you\\.codex\"",
  "timeout": 10
}
~~~

The CLI sends CODEX_HOME explicitly to the hook. Namespaced names are matched
by known suffix, including spawn_agent, multi_agent_v1send_input,
multi_agent_v1.send_input, and mcp__...__close_agent. PreToolUse changes only
model and reasoning_effort, preserving all other arguments, including
fork_context.

PostToolUse binds a spawn reservation to agent_id parsed from the tool response,
including JSON text nested in MCP content. Start/Stop events that arrive first
are recorded until the matching child ID is returned; concurrent spawns are
never mapped by event order.

## Lifecycle

- The first spawn reserves primary atomically across processes. A pending
  primary reservation also occupies the slot; later spawns use overflow.
- SubagentStop releases the working slot but keeps the child assignment. An
  idle primary child can resume through send_input only if primary is free or
  already owned by that child. Otherwise the hook denies until the current
  primary task completes. Overflow children stay pinned to overflow.
- close_agent and SessionEnd release only the targeted tracked child or parent
  session. Failed spawns release their reservation.
- resume_agent does not allocate until actual work is sent with send_input.
- Agents from chats started before the policy was enabled cannot be counted
  globally. Start new chats with the policy enabled; untracked send_input calls
  are denied while enabled.
- Editing the policy leaves existing assignments intact. An invalid enabled
  policy denies relevant spawn/resume calls and emits a safe lifecycle message.
- The UI may call --reset only after the user confirms all agents have
  finished. The hook never resets state automatically.

## Enforcement limits

Handled PreToolUse calls return Codex's supported deny response. If Python
cannot start, the hook cannot emit a deny; the UI should report that the hook
is not connected and must not claim it is a security boundary. Codex documents
tool hooks as a guardrail, not complete enforcement. SubagentStop is a turn
stopping point, and other matching hooks can affect continuation. This adapter
makes no coverage or precedence guarantee for unsupported events or other hook
registrations.

## Tests

Run the standard-library suite:

~~~powershell
python -m unittest discover -s adapters/subagent-policy/tests -v
~~~

Coverage includes rewriting, sticky assignments, lifecycle ordering, router
mapping changes, malformed configuration, status export, and simultaneous
process reservations. Native CLI interception is verified separately with an
isolated CODEX_HOME and a fake localhost Responses endpoint.
