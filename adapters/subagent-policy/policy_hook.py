#!/usr/bin/env python3
"""Codex command hook for globally pinning new subagents to two model slots."""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import sys
import tempfile
import tomllib
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


EVENT_CAP = 200
ALLOCATION_EXPORT_CAP = 200
CLOSED_ALLOCATION_CAP = 1000
MAX_EVENT_BYTES = 4 * 1024 * 1024
MAX_FIELD_LENGTH = 256
FAILURE_WORDS = {"error", "failed", "failure"}


@dataclass(frozen=True)
class Slot:
    provider: str
    model: str
    effort: str


@dataclass(frozen=True)
class Policy:
    enabled: bool
    revision: str | int | None
    primary: Slot | None = None
    overflow: Slot | None = None
    router_config_path: str | None = None
    error: bool = False
    mode: str = "concurrent"


class PolicyError(Exception):
    pass


class HookStorageError(Exception):
    pass


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def _policy_path(codex_home: Path) -> Path:
    return codex_home / "control-center" / "subagent-policy.json"


def _load_policy(codex_home: Path) -> Policy:
    # A chat may retain already-loaded hooks until reload. Respect the shared mode on
    # every call so original mode cannot allocate a third-party subagent accidentally.
    mode_state = codex_home / "control-center" / "enhancement-mode" / "state.json"
    if mode_state.exists():
        try:
            config = tomllib.loads((codex_home / "config.toml").read_text(encoding="utf-8-sig"))
            if not config.get("plugins", {}).get("codex-enhancements@codex-control-center", {}).get("enabled", False):
                return Policy(enabled=False, revision="original_mode")
        except (OSError, ValueError):
            return Policy(enabled=False, revision="original_mode")
    path = _policy_path(codex_home)
    if not path.exists():
        return Policy(enabled=False, revision=None)
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        return Policy(enabled=False, revision=None, error=True)

    if not isinstance(raw, dict):
        return Policy(enabled=False, revision=None, error=True)

    enabled_hint = raw.get("enabled") is True
    mode = raw.get("mode", "concurrent")
    if not isinstance(mode,str) or mode not in {"concurrent", "failure_fallback"}:
        return Policy(enabled=enabled_hint, revision=None, error=True)
    revision = raw.get("revision")
    if isinstance(revision, bool) or not isinstance(revision, (str, int)):
        revision = None

    if raw.get("version") != 1 or type(raw.get("enabled")) is not bool:
        return Policy(enabled=enabled_hint, revision=revision, error=True)
    if not enabled_hint:
        return Policy(enabled=False, revision=revision)

    router_config_path: str | None = None
    if "router_config_path" in raw:
        configured_path = raw.get("router_config_path")
        if (
            not isinstance(configured_path, str)
            or not configured_path.strip()
            or not Path(configured_path).is_absolute()
        ):
            return Policy(enabled=True, revision=revision, error=True)
        router_config_path = configured_path

    def read_slot(name: str) -> Slot:
        value = raw.get(name)
        if not isinstance(value, dict):
            raise PolicyError()
        fields: list[str] = []
        for key in ("provider", "model", "effort"):
            field_value = value.get(key)
            if (
                not isinstance(field_value, str)
                or not field_value.strip()
                or len(field_value) > MAX_FIELD_LENGTH
            ):
                raise PolicyError()
            fields.append(field_value)
        return Slot(provider=fields[0], model=fields[1], effort=fields[2])

    try:
        return Policy(
            enabled=True,
            revision=revision,
            primary=read_slot("primary"),
            overflow=read_slot("overflow"),
            mode=mode,
            router_config_path=router_config_path,
        )
    except PolicyError:
        return Policy(enabled=True, revision=revision, error=True)


def _paths(codex_home: Path) -> tuple[Path, Path]:
    state_dir = codex_home / "control-center"
    return (
        state_dir / "subagent-policy.sqlite",
        state_dir / "subagent-policy-status.json",
    )


def _connect(codex_home: Path) -> sqlite3.Connection:
    db_path, _ = _paths(codex_home)
    db_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        connection = sqlite3.connect(
            db_path, timeout=8.0, isolation_level=None
        )
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA busy_timeout = 8000")
        connection.executescript(
            """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS allocations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                parent_id TEXT NOT NULL,
                tool_use_id TEXT,
                agent_id TEXT,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                effort TEXT NOT NULL,
                slot TEXT NOT NULL CHECK (slot IN ('primary', 'overflow')),
                status TEXT NOT NULL CHECK (
                    status IN ('pending', 'active', 'idle', 'closed')
                ),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS allocations_spawn_key
                ON allocations(parent_id, tool_use_id)
                WHERE tool_use_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS allocations_agent_id
                ON allocations(agent_id)
                WHERE agent_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS allocations_slot_status
                ON allocations(slot, status);
            CREATE TABLE IF NOT EXISTS observed_children (
                parent_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                observed_status TEXT NOT NULL CHECK (
                    observed_status IN ('started', 'stopped')
                ),
                updated_at TEXT NOT NULL,
                PRIMARY KEY (parent_id, agent_id)
            );
            CREATE TABLE IF NOT EXISTS pending_actions (
                parent_id TEXT NOT NULL,
                tool_use_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                previous_status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (parent_id, tool_use_id)
            );
            CREATE TABLE IF NOT EXISTS hook_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                event_name TEXT NOT NULL
            );
            """
        )
        return connection
    except sqlite3.Error as exc:
        try:
            connection.close()  # type: ignore[possibly-undefined]
        except (NameError, sqlite3.Error):
            pass
        raise HookStorageError() from exc


def _safe_event_name(value: Any) -> str:
    known = {
        "PreToolUse",
        "PostToolUse",
        "SubagentStart",
        "SubagentStop",
        "SessionEnd",
    }
    return value if isinstance(value, str) and value in known else "Other"


def _record_event(
    connection: sqlite3.Connection, event_name: str, occurred_at: str
) -> None:
    connection.execute(
        "INSERT INTO metadata(key, value) VALUES('last_hook_event_at', ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        (occurred_at,),
    )
    connection.execute(
        "INSERT INTO hook_events(occurred_at, event_name) VALUES(?, ?)",
        (occurred_at, event_name),
    )
    connection.execute(
        "DELETE FROM hook_events WHERE id NOT IN "
        "(SELECT id FROM hook_events ORDER BY id DESC LIMIT ?)",
        (EVENT_CAP,),
    )


def _prune_closed_allocations(connection: sqlite3.Connection) -> None:
    connection.execute(
        """
        DELETE FROM allocations
        WHERE status = 'closed'
          AND id NOT IN (
              SELECT id FROM allocations
              WHERE status = 'closed'
              ORDER BY id DESC
              LIMIT ?
          )
        """,
        (CLOSED_ALLOCATION_CAP,),
    )


def _status_snapshot(
    connection: sqlite3.Connection, policy: Policy
) -> dict[str, Any]:
    allocations = [
        {
            "agent_id": row["agent_id"],
            "parent_id": row["parent_id"],
            "model": row["model"],
            "provider": row["provider"],
            "effort": row["effort"],
            "slot": row["slot"],
            "status": row["status"],
        }
        for row in connection.execute(
            """
            SELECT agent_id, parent_id, model, provider, effort, slot, status
            FROM allocations
            WHERE status IN ('active', 'pending', 'idle')
            ORDER BY
                CASE status
                    WHEN 'active' THEN 0
                    WHEN 'pending' THEN 1
                    ELSE 2
                END,
                id DESC
            LIMIT ?
            """,
            (ALLOCATION_EXPORT_CAP,),
        )
    ]
    active_count = connection.execute(
        "SELECT COUNT(*) FROM allocations WHERE status = 'active'"
    ).fetchone()[0]
    pending_count = connection.execute(
        "SELECT COUNT(*) FROM allocations WHERE status = 'pending'"
    ).fetchone()[0]
    owner = connection.execute(
        "SELECT agent_id, tool_use_id FROM allocations "
        "WHERE slot = 'primary' AND status = 'active' "
        "ORDER BY id LIMIT 1"
    ).fetchone()
    if not owner:
        owner = connection.execute(
            "SELECT agent_id, tool_use_id FROM allocations "
            "WHERE slot = 'primary' AND status = 'pending' "
            "ORDER BY id LIMIT 1"
        ).fetchone()
    primary_owner = (
        owner["agent_id"]
        if owner and owner["agent_id"]
        else f"pending:{owner['tool_use_id']}"
        if owner and owner["tool_use_id"]
        else None
    )
    event_time = connection.execute(
        "SELECT value FROM metadata WHERE key = 'last_hook_event_at'"
    ).fetchone()
    return {
        "enabled": policy.enabled,
        "policy_revision": policy.revision,
        "last_hook_event_at": event_time[0] if event_time else None,
        "active_count": int(active_count),
        "pending_count": int(pending_count),
        "primary_owner": primary_owner,
        "allocations": allocations,
    }


def _write_status(codex_home: Path, status: dict[str, Any]) -> None:
    _, status_path = _paths(codex_home)
    status_path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(
        prefix=".subagent-policy-status-", suffix=".tmp", dir=status_path.parent
    )
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(status, stream, ensure_ascii=False, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, status_path)
    except OSError as exc:
        try:
            os.unlink(temporary_name)
        except OSError:
            pass
        raise HookStorageError() from exc


def _deny(reason: str) -> dict[str, Any]:
    return {
        "systemMessage": reason,
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        },
    }


def _notice(reason: str) -> dict[str, Any]:
    return {"systemMessage": reason}


def _tool_operation(tool_name: Any) -> str | None:
    if not isinstance(tool_name, str):
        return None
    suffix = tool_name.rsplit("__", 1)[-1]
    if suffix == "Agent":
        return "spawn_agent"
    known = {"spawn_agent", "send_input", "close_agent", "resume_agent"}
    if suffix in known:
        return suffix
    # Some multi-agent function names are flattened by the native tool
    # registry instead of being separated with a double underscore.
    namespace = "multi_agent_v1"
    if suffix.startswith(namespace) and suffix[len(namespace) :] in known:
        return suffix[len(namespace) :]
    dot_namespace = "multi_agent_v1."
    if suffix.startswith(dot_namespace) and suffix[len(dot_namespace) :] in known:
        return suffix[len(dot_namespace) :]
    return None


def _valid_identifier(value: Any) -> str | None:
    if (
        isinstance(value, str)
        and value.strip()
        and len(value) <= MAX_FIELD_LENGTH
        and "\x00" not in value
    ):
        return value
    return None


def _event_ids(event: dict[str, Any]) -> tuple[str | None, str | None]:
    return (
        _valid_identifier(event.get("session_id")),
        _valid_identifier(event.get("tool_use_id")),
    )


def _walk_json(value: Any, depth: int = 0) -> list[Any]:
    if depth > 12:
        return []
    if isinstance(value, dict):
        result: list[Any] = [value]
        for child in value.values():
            result.extend(_walk_json(child, depth + 1))
        return result
    if isinstance(value, list):
        result = []
        for child in value[:1000]:
            result.extend(_walk_json(child, depth + 1))
        return result
    if isinstance(value, str) and len(value) <= 256 * 1024:
        candidate = value.strip()
        if candidate.startswith("{") or candidate.startswith("["):
            try:
                return _walk_json(json.loads(candidate), depth + 1)
            except json.JSONDecodeError:
                pass
        # MCP text blocks sometimes wrap otherwise valid JSON in a short prefix.
        match = re.search(r"[\[{].*[\]}]", candidate, flags=re.DOTALL)
        if match:
            try:
                return _walk_json(json.loads(match.group(0)), depth + 1)
            except json.JSONDecodeError:
                pass
    return []


def _response_agent_ids(response: Any) -> set[str]:
    identifiers: set[str] = set()
    for value in _walk_json(response):
        if isinstance(value, dict):
            identifier = _valid_identifier(value.get("agent_id"))
            if identifier:
                identifiers.add(identifier)
    return identifiers


def _input_agent_id(tool_input: Any) -> str | None:
    if not isinstance(tool_input, dict):
        return None
    identifiers: set[str] = set()
    # These are the top-level target fields used by native agent tools. Do not
    # inspect nested messages or task text, which may contain arbitrary IDs.
    for key in (
        "agent_id",
        "agentId",
        "target_agent_id",
        "targetAgentId",
        "id",
        "target",
    ):
        identifier = _valid_identifier(tool_input.get(key))
        if identifier:
            identifiers.add(identifier)
    return next(iter(identifiers)) if len(identifiers) == 1 else None


def _response_failed(response: Any) -> bool:
    for value in _walk_json(response):
        if not isinstance(value, dict):
            continue
        if value.get("isError") is True:
            return True
        if value.get("success") is False or value.get("ok") is False:
            return True
        status = value.get("status")
        if isinstance(status, str) and status.lower() in FAILURE_WORDS:
            return True
        error = value.get("error")
        if error not in (None, "", False, {}, []):
            return True
    return False


def _slot_values(policy: Policy, slot_name: str) -> Slot:
    slot = policy.primary if slot_name == "primary" else policy.overflow
    if slot is None:
        raise PolicyError()
    return slot


def _router_mapping_matches(policy: Policy, slot: Slot) -> bool:
    if policy.router_config_path is None:
        return True
    try:
        router = json.loads(
            Path(policy.router_config_path).read_text(encoding="utf-8")
        )
        routes = router.get("routes") if isinstance(router, dict) else None
        route = routes.get(slot.model) if isinstance(routes, dict) else None
        return isinstance(route, dict) and route.get("provider") == slot.provider
    except (OSError, UnicodeError, json.JSONDecodeError):
        return False


def _key_mapping_deny() -> dict[str, Any]:
    return _deny("Key来源映射变化，重新应用策略后再试。")


def _fallback_permit(connection, parent_id):
    row=connection.execute("SELECT value FROM metadata WHERE key = ?",("fallback-permit:"+parent_id,)).fetchone()
    if not row: return None
    try:
        value=json.loads(row[0])
        return value if isinstance(value,dict) else None
    except (ValueError,TypeError): return None


def _grant_fallback(connection, policy, parent_id, row, source):
    if policy.mode!="failure_fallback" or row is None or row["slot"]!="primary" or policy.primary is None:
        return
    if row["model"]!=policy.primary.model or row["provider"]!=policy.primary.provider:
        return
    previous=_fallback_permit(connection,parent_id)
    if previous and previous.get("source")==source and previous.get("revision")==policy.revision:
        return  # Replayed post-tool events cannot replenish a consumed permission.
    value={"source":source,"revision":policy.revision,"used_by":None}
    connection.execute("INSERT INTO metadata(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                       ("fallback-permit:"+parent_id,json.dumps(value,separators=(",",":"))))


def _closed_agent_errored(response):
    return any(isinstance(node,dict) and isinstance(node.get("previous_status"),dict)
               and "errored" in node["previous_status"] for node in _walk_json(response))


def _pre_spawn(
    connection: sqlite3.Connection,
    event: dict[str, Any],
    policy: Policy,
    now: str,
) -> dict[str, Any]:
    parent_id, tool_use_id = _event_ids(event)
    if not parent_id or not tool_use_id:
        return _deny(
            "Subagent policy could not reserve a slot because the hook call "
            "identifiers were missing."
        )
    tool_input = event.get("tool_input")
    if not isinstance(tool_input, dict):
        return _deny(
            "Subagent policy could not reserve a slot because spawn arguments "
            "were unavailable."
        )

    existing = connection.execute(
        "SELECT * FROM allocations WHERE parent_id = ? AND tool_use_id = ?",
        (parent_id, tool_use_id),
    ).fetchone()
    if existing:
        slot_name = existing["slot"]
        assigned = Slot(
            provider=existing["provider"],
            model=existing["model"],
            effort=existing["effort"],
        )
    else:
        if policy.mode=="failure_fallback":
            wants_backup=policy.overflow is not None and tool_input.get("model")==policy.overflow.model
            slot_name="overflow" if wants_backup else "primary"
            if wants_backup:
                permit=_fallback_permit(connection,parent_id)
                if not permit or permit.get("revision")!=policy.revision or permit.get("used_by") is not None:
                    return _deny("备用模型只允许在本会话默认子代理明确失败后使用一次；请先关闭 errored 子代理并保留失败结果。")
        else:
            primary_busy = connection.execute(
                "SELECT 1 FROM allocations WHERE slot = 'primary' "
                "AND status IN ('pending', 'active') LIMIT 1"
            ).fetchone()
            slot_name = "overflow" if primary_busy else "primary"
        assigned = _slot_values(policy, slot_name)
    if not _router_mapping_matches(policy, assigned):
        return _key_mapping_deny()
    if not existing:
        if policy.mode=="failure_fallback" and slot_name=="overflow":
            permit["used_by"]=tool_use_id
            connection.execute("UPDATE metadata SET value=? WHERE key=?",(json.dumps(permit,separators=(",",":")),"fallback-permit:"+parent_id))
        connection.execute(
            """
            INSERT INTO allocations(
                parent_id, tool_use_id, agent_id, provider, model, effort,
                slot, status, created_at, updated_at
            ) VALUES(?, ?, NULL, ?, ?, ?, ?, 'pending', ?, ?)
            """,
            (
                parent_id,
                tool_use_id,
                assigned.provider,
                assigned.model,
                assigned.effort,
                slot_name,
                now,
                now,
            ),
        )

    updated_input = dict(tool_input)
    updated_input["model"] = assigned.model
    updated_input["reasoning_effort"] = assigned.effort
    return {
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "allow",
            "updatedInput": updated_input,
        }
    }


def _pre_send_input(
    connection: sqlite3.Connection,
    event: dict[str, Any],
    policy: Policy,
    now: str,
) -> dict[str, Any]:
    if not policy.enabled:
        # Keep lifecycle state useful while enforcement is toggled off.
        parent_id, tool_use_id = _event_ids(event)
        agent_id = _input_agent_id(event.get("tool_input"))
        if parent_id and tool_use_id and agent_id:
            row = connection.execute(
                "SELECT * FROM allocations WHERE parent_id = ? AND agent_id = ?",
                (parent_id, agent_id),
            ).fetchone()
            if row and row["status"] != "closed":
                previous = row["status"]
                connection.execute(
                    "UPDATE allocations SET status = 'active', updated_at = ? "
                    "WHERE id = ?",
                    (now, row["id"]),
                )
                connection.execute(
                    """
                    INSERT OR IGNORE INTO pending_actions(
                        parent_id, tool_use_id, agent_id, operation,
                        previous_status, created_at
                    ) VALUES(?, ?, ?, 'send_input', ?, ?)
                    """,
                    (parent_id, tool_use_id, agent_id, previous, now),
                )
        return {}

    parent_id, tool_use_id = _event_ids(event)
    agent_id = _input_agent_id(event.get("tool_input"))
    if not parent_id or not tool_use_id or not agent_id:
        return _deny(
            "Subagent policy cannot resume this agent because its parent, call, "
            "or agent identifier is missing or ambiguous."
        )
    row = connection.execute(
        "SELECT * FROM allocations WHERE parent_id = ? AND agent_id = ?",
        (parent_id, agent_id),
    ).fetchone()
    if not row or row["status"] == "closed":
        return _deny(
            "This subagent has no active policy assignment in this chat. "
            "Restart the chat with the policy enabled before using it."
        )
    assigned = Slot(
        provider=row["provider"],
        model=row["model"],
        effort=row["effort"],
    )
    if not _router_mapping_matches(policy, assigned):
        return _key_mapping_deny()
    if row["slot"] == "primary" and policy.mode=="concurrent":
        other_primary = connection.execute(
            "SELECT agent_id FROM allocations WHERE slot = 'primary' "
            "AND status IN ('pending', 'active') AND id <> ? LIMIT 1",
            (row["id"],),
        ).fetchone()
        if other_primary:
            return _deny(
                "This primary-model subagent is idle while another subagent "
                "owns the primary slot. Wait for that task to finish before "
                "resuming this one."
            )

    previous = row["status"]
    connection.execute(
        "UPDATE allocations SET status = 'active', updated_at = ? WHERE id = ?",
        (now, row["id"]),
    )
    connection.execute(
        """
        INSERT OR IGNORE INTO pending_actions(
            parent_id, tool_use_id, agent_id, operation, previous_status,
            created_at
        ) VALUES(?, ?, ?, 'send_input', ?, ?)
        """,
        (parent_id, tool_use_id, agent_id, previous, now),
    )
    return {}


def _bind_spawn_result(
    connection: sqlite3.Connection,
    event: dict[str, Any],
    parent_id: str | None,
    tool_use_id: str | None,
    now: str,
) -> str | None:
    if not parent_id or not tool_use_id:
        return None
    row = connection.execute(
        "SELECT * FROM allocations WHERE parent_id = ? AND tool_use_id = ?",
        (parent_id, tool_use_id),
    ).fetchone()
    if not row:
        return None
    response = event.get("tool_response")
    if _response_failed(response):
        if row["status"] == "pending":
            connection.execute(
                "UPDATE allocations SET status = 'closed', updated_at = ? "
                "WHERE id = ?",
                (now, row["id"]),
            )
        return None
    agent_ids = _response_agent_ids(response)
    if not agent_ids:
        return None
    if len(agent_ids) != 1:
        return "Subagent policy could not bind a spawn result because it "
        "contained multiple agent identifiers; the reservation remains pending."
    agent_id = next(iter(agent_ids))
    if row["agent_id"] and row["agent_id"] != agent_id:
        return "Subagent policy kept the existing child assignment because the "
        "spawn result did not match its original agent identifier."
    other = connection.execute(
        "SELECT id FROM allocations WHERE agent_id = ? AND id <> ? LIMIT 1",
        (agent_id, row["id"]),
    ).fetchone()
    if other:
        return "Subagent policy could not bind a duplicate agent identifier; "
        "the reservation remains pending."

    observed = connection.execute(
        "SELECT observed_status FROM observed_children "
        "WHERE parent_id = ? AND agent_id = ?",
        (parent_id, agent_id),
    ).fetchone()
    if row["agent_id"] == agent_id and row["status"] != "pending":
        bound_status = row["status"]
    elif observed and observed["observed_status"] == "stopped":
        bound_status = "idle"
    else:
        bound_status = "active"
    connection.execute(
        "UPDATE allocations SET agent_id = ?, status = ?, updated_at = ? "
        "WHERE id = ?",
        (agent_id, bound_status, now, row["id"]),
    )
    connection.execute(
        "DELETE FROM observed_children WHERE parent_id = ? AND agent_id = ?",
        (parent_id, agent_id),
    )
    return None


def _post_event(
    connection: sqlite3.Connection,
    event: dict[str, Any],
    operation: str | None,
    now: str,
    policy: Policy,
) -> str | None:
    parent_id, tool_use_id = _event_ids(event)
    if operation == "spawn_agent":
        if parent_id and tool_use_id and _response_failed(event.get("tool_response")):
            row=connection.execute("SELECT * FROM allocations WHERE parent_id=? AND tool_use_id=?",(parent_id,tool_use_id)).fetchone()
            _grant_fallback(connection,policy,parent_id,row,"call:"+tool_use_id)
        return _bind_spawn_result(
            connection, event, parent_id, tool_use_id, now
        )
    if operation == "send_input":
        if not parent_id or not tool_use_id:
            return None
        action = connection.execute(
            "SELECT * FROM pending_actions WHERE parent_id = ? AND tool_use_id = ?",
            (parent_id, tool_use_id),
        ).fetchone()
        if not action:
            return None
        if _response_failed(event.get("tool_response")):
            connection.execute(
                "UPDATE allocations SET status = ?, updated_at = ? "
                "WHERE parent_id = ? AND agent_id = ? AND status = 'active'",
                (
                    action["previous_status"],
                    now,
                    parent_id,
                    action["agent_id"],
                ),
            )
        connection.execute(
            "DELETE FROM pending_actions WHERE parent_id = ? AND tool_use_id = ?",
            (parent_id, tool_use_id),
        )
        return None
    if operation == "close_agent":
        errored=_closed_agent_errored(event.get("tool_response"))
        if not errored and _response_failed(event.get("tool_response")):
            return None
        agent_id = _input_agent_id(event.get("tool_input"))
        if not parent_id or not agent_id:
            return None
        if errored:
            row=connection.execute("SELECT * FROM allocations WHERE parent_id=? AND agent_id=?",(parent_id,agent_id)).fetchone()
            if row and row["status"]!="closed":_grant_fallback(connection,policy,parent_id,row,"agent:"+agent_id)
        connection.execute(
            "UPDATE allocations SET status = 'closed', updated_at = ? "
            "WHERE parent_id = ? AND agent_id = ? AND status <> 'closed'",
            (now, parent_id, agent_id),
        )
        connection.execute(
            "DELETE FROM pending_actions WHERE parent_id = ? AND agent_id = ?",
            (parent_id, agent_id),
        )
    return None


def _subagent_lifecycle(
    connection: sqlite3.Connection,
    event: dict[str, Any],
    event_name: str,
    now: str,
) -> None:
    parent_id = _valid_identifier(event.get("session_id"))
    agent_id = _valid_identifier(event.get("agent_id"))
    if not parent_id or not agent_id:
        return
    observed_status = "started" if event_name == "SubagentStart" else "stopped"
    row = connection.execute(
        "SELECT * FROM allocations WHERE parent_id = ? AND agent_id = ?",
        (parent_id, agent_id),
    ).fetchone()
    if row:
        if row["status"] != "closed":
            status = "active" if observed_status == "started" else "idle"
            connection.execute(
                "UPDATE allocations SET status = ?, updated_at = ? WHERE id = ?",
                (status, now, row["id"]),
            )
        return
    connection.execute(
        """
        INSERT INTO observed_children(parent_id, agent_id, observed_status, updated_at)
        VALUES(?, ?, ?, ?)
        ON CONFLICT(parent_id, agent_id) DO UPDATE SET
            observed_status = excluded.observed_status,
            updated_at = excluded.updated_at
        """,
        (parent_id, agent_id, observed_status, now),
    )


def _session_end(
    connection: sqlite3.Connection, event: dict[str, Any], now: str
) -> None:
    parent_id = _valid_identifier(event.get("session_id"))
    if not parent_id:
        return
    connection.execute(
        "UPDATE allocations SET status = 'closed', updated_at = ? "
        "WHERE parent_id = ? AND status <> 'closed'",
        (now, parent_id),
    )
    connection.execute(
        "DELETE FROM observed_children WHERE parent_id = ?", (parent_id,)
    )
    connection.execute(
        "DELETE FROM pending_actions WHERE parent_id = ?", (parent_id,)
    )
    connection.execute("DELETE FROM metadata WHERE key=?",("fallback-permit:"+parent_id,))


def _hook_config_notice(event_name: str, operation: str | None) -> bool:
    if event_name in {"SubagentStart", "SubagentStop", "SessionEnd"}:
        return True
    if event_name == "PostToolUse":
        return operation in {
            "spawn_agent",
            "send_input",
            "close_agent",
            "resume_agent",
        }
    return event_name == "PreToolUse" and operation in {
        "spawn_agent",
        "send_input",
    }


def run_event(codex_home: str | Path, event: dict[str, Any]) -> dict[str, Any]:
    home = Path(codex_home).expanduser()
    policy = _load_policy(home)
    event_name = _safe_event_name(event.get("hook_event_name"))
    operation = _tool_operation(event.get("tool_name"))
    relevant_pre = event_name == "PreToolUse" and operation in {
        "spawn_agent",
        "send_input",
    }
    connection = _connect(home)
    result: dict[str, Any] = {}
    try:
        connection.execute("BEGIN IMMEDIATE")
        now = _now()
        _record_event(connection, event_name, now)

        if relevant_pre:
            if policy.error:
                result = _deny(
                    "Subagent policy configuration is invalid. Correct the "
                    "policy file before spawning or resuming subagents."
                )
            elif not policy.enabled:
                if operation == "send_input":
                    result = _pre_send_input(connection, event, policy, now)
            elif operation == "spawn_agent":
                result = _pre_spawn(connection, event, policy, now)
            elif operation == "send_input":
                result = _pre_send_input(connection, event, policy, now)
        elif event_name == "PostToolUse":
            notice = _post_event(connection, event, operation, now, policy)
            if notice:
                result = _notice(notice)
        elif event_name in {"SubagentStart", "SubagentStop"}:
            _subagent_lifecycle(connection, event, event_name, now)
        elif event_name == "SessionEnd":
            _session_end(connection, event, now)

        if (
            policy.error
            and _hook_config_notice(event_name, operation)
            and not result
        ):
            result = _notice(
                "Subagent policy configuration is invalid; existing "
                "assignments were retained."
            )

        _prune_closed_allocations(connection)
        status = _status_snapshot(connection, policy)
        # Keep the writer lock until the atomic export is replaced, so an
        # older concurrent snapshot cannot overwrite a newer one.
        _write_status(home, status)
        connection.commit()
    except sqlite3.Error as exc:
        connection.rollback()
        raise HookStorageError() from exc
    finally:
        connection.close()
    return result


def _status_only(codex_home: Path) -> dict[str, Any]:
    policy = _load_policy(codex_home)
    connection = _connect(codex_home)
    try:
        connection.execute("BEGIN IMMEDIATE")
        status = _status_snapshot(connection, policy)
        _write_status(codex_home, status)
        connection.commit()
    except sqlite3.Error as exc:
        connection.rollback()
        raise HookStorageError() from exc
    finally:
        connection.close()
    return status


def _reset_state(codex_home: Path) -> None:
    policy = _load_policy(codex_home)
    connection = _connect(codex_home)
    try:
        connection.execute("BEGIN IMMEDIATE")
        connection.execute("DELETE FROM allocations")
        connection.execute("DELETE FROM observed_children")
        connection.execute("DELETE FROM pending_actions")
        connection.execute("DELETE FROM hook_events")
        status = _status_snapshot(connection, policy)
        _write_status(codex_home, status)
        connection.commit()
    except sqlite3.Error as exc:
        connection.rollback()
        raise HookStorageError() from exc
    finally:
        connection.close()


def _cli_fallback(event: dict[str, Any] | None, storage_error: bool) -> dict[str, Any]:
    if event:
        event_name = _safe_event_name(event.get("hook_event_name"))
        operation = _tool_operation(event.get("tool_name"))
        if event_name == "PreToolUse" and operation in {
            "spawn_agent",
            "send_input",
        }:
            return _deny(
                "Subagent policy state is unavailable, so this agent operation "
                "was denied. Check the local hook and state database."
            )
        if event_name in {
            "PostToolUse",
            "SubagentStart",
            "SubagentStop",
            "SessionEnd",
        }:
            return _notice(
                "Subagent policy state could not be updated. Check the local "
                "hook and state database."
            )
    if storage_error:
        return {}
    return _notice("Subagent policy hook received invalid event data.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codex-home", required=True, type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--status", action="store_true")
    mode.add_argument(
        "--reset",
        action="store_true",
        help="Clear tracked allocations after the user confirms all agents are finished.",
    )
    args = parser.parse_args(argv)

    if args.status:
        try:
            status = _status_only(args.codex_home)
            print(json.dumps(status, ensure_ascii=False, separators=(",", ":")))
            return 0
        except HookStorageError:
            print("Unable to read subagent policy state.", file=sys.stderr)
            return 1

    if args.reset:
        try:
            _reset_state(args.codex_home)
            print('{"cleaned":true}')
            return 0
        except HookStorageError:
            print("Unable to reset subagent policy state.", file=sys.stderr)
            return 1

    event: dict[str, Any] | None = None
    try:
        raw = sys.stdin.buffer.read(MAX_EVENT_BYTES + 1)
        if len(raw) > MAX_EVENT_BYTES:
            raise ValueError()
        candidate = json.loads(raw.decode("utf-8"))
        if not isinstance(candidate, dict):
            raise ValueError()
        event = candidate
        result = run_event(args.codex_home, event)
    except (UnicodeError, json.JSONDecodeError, ValueError):
        result = _cli_fallback(None, storage_error=False)
    except HookStorageError:
        result = _cli_fallback(event, storage_error=True)
    except Exception:
        # Keep exception details, event bodies, and tool output out of hook logs.
        result = _cli_fallback(event, storage_error=True)
    print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
