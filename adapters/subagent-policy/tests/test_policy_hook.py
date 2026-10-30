from __future__ import annotations

import concurrent.futures
from contextlib import closing
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ADAPTER = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ADAPTER))
import policy_hook  # noqa: E402


class PolicyHookTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="subagent-policy-")
        self.home = Path(self.temporary.name)
        self.write_policy()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_policy(
        self,
        *,
        enabled: bool = True,
        primary_model: str = "alias-A",
        overflow_model: str = "alias-B",
        revision: int = 7,
        router_config_path: str | None = None,
    ) -> None:
        control = self.home / "control-center"
        control.mkdir(parents=True, exist_ok=True)
        policy = {
            "version": 1,
            "revision": revision,
            "enabled": enabled,
            "primary": {
                "provider": "provider-a",
                "model": primary_model,
                "effort": "max",
            },
            "overflow": {
                "provider": "provider-b",
                "model": overflow_model,
                "effort": "high",
            },
        }
        if router_config_path is not None:
            policy["router_config_path"] = router_config_path
        (control / "subagent-policy.json").write_text(
            json.dumps(policy), encoding="utf-8"
        )

    def event(
        self,
        hook_event_name: str,
        *,
        session_id: str = "parent-1",
        tool_name: str = "spawn_agent",
        tool_use_id: str = "call-1",
        tool_input: dict | None = None,
        tool_response=None,
        agent_id: str | None = None,
    ) -> dict:
        value = {
            "hook_event_name": hook_event_name,
            "session_id": session_id,
        }
        if tool_name is not None:
            value["tool_name"] = tool_name
        if tool_use_id is not None:
            value["tool_use_id"] = tool_use_id
        if tool_input is not None:
            value["tool_input"] = tool_input
        if tool_response is not None:
            value["tool_response"] = tool_response
        if agent_id is not None:
            value["agent_id"] = agent_id
        return value

    def spawn(self, session: str, call: str, *, tool: str = "spawn_agent"):
        return policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                session_id=session,
                tool_name=tool,
                tool_use_id=call,
                tool_input={
                    "message": "private prompt marker",
                    "fork_context": True,
                    "custom": {"preserved": 9},
                },
            ),
        )

    def bind(
        self,
        session: str,
        call: str,
        agent: str,
        *,
        tool: str = "spawn_agent",
        response=None,
    ):
        if response is None:
            response = {"agent_id": agent}
        return policy_hook.run_event(
            self.home,
            self.event(
                "PostToolUse",
                session_id=session,
                tool_name=tool,
                tool_use_id=call,
                tool_response=response,
            ),
        )

    def status(self) -> dict:
        return policy_hook._status_only(self.home)

    def allocation(self, agent_id: str) -> dict:
        return next(
            row for row in self.status()["allocations"]
            if row["agent_id"] == agent_id
        )

    def stored_status(self, agent_id: str) -> str:
        database = self.home / "control-center" / "subagent-policy.sqlite"
        with closing(sqlite3.connect(database)) as connection:
            row = connection.execute(
                "SELECT status FROM allocations WHERE agent_id = ?",
                (agent_id,),
            ).fetchone()
        return row[0]

    def test_primary_then_overflow_rewrite_preserves_arguments(self) -> None:
        first = self.spawn("parent-1", "call-1")
        second = self.spawn("parent-2", "call-2")
        first_args = first["hookSpecificOutput"]["updatedInput"]
        second_args = second["hookSpecificOutput"]["updatedInput"]

        self.assertEqual(first_args["model"], "alias-A")
        self.assertEqual(first_args["reasoning_effort"], "max")
        self.assertEqual(first_args["message"], "private prompt marker")
        self.assertEqual(first_args["fork_context"], True)
        self.assertEqual(first_args["custom"], {"preserved": 9})
        self.assertEqual(second_args["model"], "alias-B")
        self.assertEqual(second_args["reasoning_effort"], "high")
        self.assertEqual(self.status()["pending_count"], 2)

    def test_qualified_names_match_exact_suffix(self) -> None:
        accepted = self.spawn("parent-1", "call-1", tool="mcp__codex__spawn_agent")
        flattened = policy_hook._tool_operation(
            "mcp__codex__multi_agent_v1send_input"
        )
        dotted = self.spawn(
            "parent-2", "call-dot", tool="multi_agent_v1.spawn_agent"
        )
        ignored = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="mcp__codex__spawn_agent_extra",
                tool_input={"message": "unchanged"},
            ),
        )
        self.assertIn("updatedInput", accepted["hookSpecificOutput"])
        self.assertEqual(flattened, "send_input")
        self.assertEqual(
            dotted["hookSpecificOutput"]["updatedInput"]["model"], "alias-B"
        )
        self.assertEqual(ignored, {})

    def test_spawn_reservation_is_idempotent_for_parent_and_tool_call(self) -> None:
        first = self.spawn("parent-1", "same-call")
        again = self.spawn("parent-1", "same-call")
        self.assertEqual(
            first["hookSpecificOutput"]["updatedInput"]["model"],
            again["hookSpecificOutput"]["updatedInput"]["model"],
        )
        self.assertEqual(self.status()["pending_count"], 1)
        self.assertEqual(len(self.status()["allocations"]), 1)

    def test_simultaneous_processes_reserve_exactly_one_primary(self) -> None:
        script = ADAPTER / "policy_hook.py"

        def launch(parent: str, call: str):
            event = self.event(
                "PreToolUse",
                session_id=parent,
                tool_use_id=call,
                tool_input={"message": "concurrent"},
            )
            return subprocess.Popen(
                [
                    sys.executable,
                    str(script),
                    "--codex-home",
                    str(self.home),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                env={**os.environ, "PYTHONIOENCODING": "utf-8"},
            ), json.dumps(event)

        processes = [launch("parent-a", "call-a"), launch("parent-b", "call-b")]

        def finish(item):
            process, payload = item
            stdout, stderr = process.communicate(payload, timeout=15)
            self.assertEqual(process.returncode, 0, stderr)
            return json.loads(stdout)

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(finish, processes))
        self.assertTrue(
            all("updatedInput" in value.get("hookSpecificOutput", {}) for value in results),
            results,
        )
        models = [
            value["hookSpecificOutput"]["updatedInput"]["model"]
            for value in results
        ]
        self.assertCountEqual(models, ["alias-A", "alias-B"])

    def test_post_binds_nested_mcp_json_and_keeps_sticky_overflow(self) -> None:
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        self.spawn("parent-1", "call-b")
        self.bind(
            "parent-1",
            "call-b",
            "agent-b",
            response={
                "content": [
                    {"type": "text", "text": '{"agent_id":"agent-b"}'}
                ]
            },
        )
        self.assertEqual(self.allocation("agent-b")["slot"], "overflow")
        self.assertEqual(self.allocation("agent-b")["model"], "alias-B")

        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                session_id="parent-1",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        sent = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                session_id="parent-1",
                tool_name="mcp__agent__send_input",
                tool_use_id="resume-b",
                tool_input={"agent_id": "agent-b", "message": "continue"},
            ),
        )
        self.assertEqual(sent, {})
        self.assertEqual(self.allocation("agent-b")["model"], "alias-B")
        self.assertEqual(self.allocation("agent-b")["status"], "active")

    def test_stop_releases_primary_for_next_new_child(self) -> None:
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        stopped = policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        self.assertEqual(stopped, {})
        self.assertEqual(self.allocation("agent-a")["status"], "idle")
        next_spawn = self.spawn("parent-2", "call-b")
        self.assertEqual(
            next_spawn["hookSpecificOutput"]["updatedInput"]["model"], "alias-A"
        )

    def test_idle_primary_child_cannot_resume_while_another_owns_primary(self):
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        self.spawn("parent-1", "call-b")
        self.bind("parent-1", "call-b", "agent-b")
        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        self.spawn("parent-2", "call-c")
        result = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="send_input",
                tool_use_id="resume-a",
                tool_input={"agent_id": "agent-a", "message": "resume"},
            ),
        )
        specific = result["hookSpecificOutput"]
        self.assertEqual(specific["permissionDecision"], "deny")
        self.assertIn("Wait", specific["permissionDecisionReason"])
        self.assertEqual(self.allocation("agent-a")["status"], "idle")

    def test_spawn_failure_releases_primary_reservation(self) -> None:
        self.spawn("parent-1", "failed-call")
        self.bind(
            "parent-1",
            "failed-call",
            "never-created",
            response={"isError": True, "content": []},
        )
        self.assertEqual(self.status()["pending_count"], 0)
        next_spawn = self.spawn("parent-2", "next-call")
        self.assertEqual(
            next_spawn["hookSpecificOutput"]["updatedInput"]["model"], "alias-A"
        )

    def test_stop_before_post_bind_keeps_completed_child_idle(self) -> None:
        self.spawn("parent-1", "call-a")
        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        self.bind("parent-1", "call-a", "agent-a")
        self.assertEqual(self.allocation("agent-a")["status"], "idle")
        self.assertEqual(self.status()["active_count"], 0)
        next_spawn = self.spawn("parent-2", "call-b")
        self.assertEqual(
            next_spawn["hookSpecificOutput"]["updatedInput"]["model"], "alias-A"
        )

    def test_close_and_session_end_only_release_their_targets(self) -> None:
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        self.spawn("parent-2", "call-b")
        self.bind("parent-2", "call-b", "agent-b")
        policy_hook.run_event(
            self.home,
            self.event(
                "PostToolUse",
                session_id="parent-1",
                tool_name="mcp__agent__close_agent",
                tool_use_id="close-a",
                tool_input={"agent_id": "agent-a"},
                tool_response={"closed": True},
            ),
        )
        self.assertEqual(self.stored_status("agent-a"), "closed")
        self.assertEqual(self.allocation("agent-b")["status"], "active")
        policy_hook.run_event(
            self.home,
            self.event(
                "SessionEnd",
                session_id="parent-2",
                tool_name=None,
                tool_use_id=None,
            ),
        )
        self.assertEqual(self.stored_status("agent-b"), "closed")

    def test_failed_send_input_restores_idle_state(self) -> None:
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="send_input",
                tool_use_id="resume-a",
                tool_input={"agent_id": "agent-a"},
            ),
        )
        policy_hook.run_event(
            self.home,
            self.event(
                "PostToolUse",
                tool_name="send_input",
                tool_use_id="resume-a",
                tool_response={"success": False, "error": "failed"},
            ),
        )
        self.assertEqual(self.allocation("agent-a")["status"], "idle")

    def test_send_input_top_level_id_and_target_ignore_message_text(self):
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        by_id = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="multi_agent_v1.send_input",
                tool_use_id="resume-id",
                tool_input={
                    "id": "agent-a",
                    "message": 'quoted {"agent_id":"different-child"}',
                },
            ),
        )
        self.assertEqual(by_id, {})
        self.assertEqual(self.allocation("agent-a")["status"], "active")

        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        by_target = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="multi_agent_v1send_input",
                tool_use_id="resume-target",
                tool_input={"target": "agent-a", "message": "continue"},
            ),
        )
        self.assertEqual(by_target, {})
        self.assertEqual(self.allocation("agent-a")["status"], "active")

    def test_config_edits_keep_old_assignments_and_disabled_mode_keeps_cleanup(self):
        self.spawn("parent-1", "call-a")
        self.bind("parent-1", "call-a", "agent-a")
        self.write_policy(primary_model="alias-new", overflow_model="alias-new-b")
        self.assertEqual(self.allocation("agent-a")["model"], "alias-A")

        self.write_policy(enabled=False, primary_model="alias-new")
        result = self.spawn("parent-2", "call-b")
        self.assertEqual(result, {})
        self.assertEqual(len(self.status()["allocations"]), 1)
        policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                session_id="parent-1",
                tool_name=None,
                tool_use_id=None,
                agent_id="agent-a",
            ),
        )
        self.assertEqual(self.allocation("agent-a")["status"], "idle")

    def test_status_schema_is_fixed_and_status_does_not_fake_a_hook(self):
        self.spawn("parent-1", "call-a")
        before = self.status()
        state_path = self.home / "control-center" / "subagent-policy-status.json"
        exported = json.loads(state_path.read_text(encoding="utf-8"))
        self.assertEqual(
            list(exported),
            [
                "enabled",
                "policy_revision",
                "last_hook_event_at",
                "active_count",
                "pending_count",
                "primary_owner",
                "allocations",
            ],
        )
        self.assertEqual(exported, before)
        self.assertTrue(exported["last_hook_event_at"].endswith("Z"))
        db_path = self.home / "control-center" / "subagent-policy.sqlite"
        with closing(sqlite3.connect(db_path)) as connection:
            count_before = connection.execute(
                "SELECT COUNT(*) FROM hook_events"
            ).fetchone()[0]
        after = policy_hook._status_only(self.home)
        with closing(sqlite3.connect(db_path)) as connection:
            count_after = connection.execute(
                "SELECT COUNT(*) FROM hook_events"
            ).fetchone()[0]
        self.assertEqual(before["last_hook_event_at"], after["last_hook_event_at"])
        self.assertEqual(count_before, count_after)
        self.assertNotIn(
            "private prompt marker",
            db_path.read_bytes().decode("latin-1", errors="ignore"),
        )

    def test_status_allocation_cap_priority_and_closed_history_cap(self):
        self.spawn("parent-pending", "call-pending")
        database = self.home / "control-center" / "subagent-policy.sqlite"
        stamp = "2026-10-26T00:00:00.000Z"
        with closing(sqlite3.connect(database)) as connection:
            connection.execute("BEGIN IMMEDIATE")
            for index in range(2):
                connection.execute(
                    """
                    INSERT INTO allocations(
                        parent_id, tool_use_id, agent_id, provider, model, effort,
                        slot, status, created_at, updated_at
                    ) VALUES(?, ?, ?, 'provider-b', 'alias-B', 'high',
                             'overflow', 'active', ?, ?)
                    """,
                    (f"active-parent-{index}", f"active-call-{index}",
                     f"active-agent-{index}", stamp, stamp),
                )
            connection.execute(
                """
                INSERT INTO allocations(
                    parent_id, tool_use_id, agent_id, provider, model, effort,
                    slot, status, created_at, updated_at
                ) VALUES('pending-parent', 'pending-call', NULL,
                         'provider-b', 'alias-B', 'high', 'overflow',
                         'pending', ?, ?)
                """,
                (stamp, stamp),
            )
            for index in range(210):
                connection.execute(
                    """
                    INSERT INTO allocations(
                        parent_id, tool_use_id, agent_id, provider, model, effort,
                        slot, status, created_at, updated_at
                    ) VALUES(?, ?, ?, 'provider-b', 'alias-B', 'high',
                             'overflow', 'idle', ?, ?)
                    """,
                    (f"idle-parent-{index}", f"idle-call-{index}",
                     f"idle-agent-{index}", stamp, stamp),
                )
            connection.commit()

        status = self.status()
        self.assertEqual(len(status["allocations"]), 200)
        self.assertEqual(
            [row["status"] for row in status["allocations"][:5]],
            ["active", "active", "pending", "pending", "idle"],
        )
        self.assertEqual(status["primary_owner"], "pending:call-pending")
        self.assertEqual(status["active_count"], 2)
        self.assertEqual(status["pending_count"], 2)
        self.assertEqual(status["allocations"][4]["agent_id"], "idle-agent-209")

        with closing(sqlite3.connect(database)) as connection:
            connection.execute("BEGIN IMMEDIATE")
            for index in range(1001):
                connection.execute(
                    """
                    INSERT INTO allocations(
                        parent_id, tool_use_id, agent_id, provider, model, effort,
                        slot, status, created_at, updated_at
                    ) VALUES(?, ?, NULL, 'provider-b', 'alias-B', 'high',
                             'overflow', 'closed', ?, ?)
                    """,
                    (f"closed-parent-{index}", f"closed-call-{index}",
                     stamp, stamp),
                )
            connection.commit()
        policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="Bash",
                tool_input={"command": "untouched"},
            ),
        )
        with closing(sqlite3.connect(database)) as connection:
            closed_count = connection.execute(
                "SELECT COUNT(*) FROM allocations WHERE status = 'closed'"
            ).fetchone()[0]
        self.assertEqual(closed_count, 1000)

    def test_router_mapping_drift_denies_without_exposing_router_values(self):
        router_path = self.home / "router.json"
        router_path.write_text(
            json.dumps(
                {
                    "routes": {
                        "alias-A": {"provider": "provider-a"},
                        "alias-B": {"provider": "provider-b"},
                    }
                }
            ),
            encoding="utf-8",
        )
        self.write_policy(router_config_path=str(router_path.resolve()))
        first = self.spawn("parent-1", "call-a")
        self.assertEqual(
            first["hookSpecificOutput"]["updatedInput"]["model"], "alias-A"
        )
        self.bind("parent-1", "call-a", "agent-a")

        router_path.write_text(
            json.dumps(
                {
                    "routes": {
                        "alias-A": {"provider": "provider-secret-raw"},
                        "alias-B": {"provider": "provider-secret-raw"},
                    }
                }
            ),
            encoding="utf-8",
        )
        blocked = self.spawn("parent-2", "call-b")
        reason = blocked["hookSpecificOutput"]["permissionDecisionReason"]
        self.assertIn("Key来源映射变化，重新应用策略", reason)
        self.assertNotIn("provider-secret-raw", reason)
        self.assertNotIn(str(router_path), reason)
        self.assertEqual(len(self.status()["allocations"]), 1)

        resumed = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="multi_agent_v1send_input",
                tool_use_id="resume-a",
                tool_input={"agent_id": "agent-a"},
            ),
        )
        self.assertEqual(
            resumed["hookSpecificOutput"]["permissionDecision"], "deny"
        )

    def test_invalid_policy_denies_relevant_pre_and_notices_lifecycle(self):
        (self.home / "control-center" / "subagent-policy.json").write_text(
            '{"version":1,"enabled":true,"primary":{}}', encoding="utf-8"
        )
        denied = self.spawn("parent-1", "call-a")
        self.assertEqual(
            denied["hookSpecificOutput"]["permissionDecision"], "deny"
        )
        notice = policy_hook.run_event(
            self.home,
            self.event(
                "SubagentStop",
                tool_name=None,
                tool_use_id=None,
                agent_id="untracked",
            ),
        )
        self.assertIn("systemMessage", notice)

    def test_non_agent_tool_is_untouched(self):
        result = policy_hook.run_event(
            self.home,
            self.event(
                "PreToolUse",
                tool_name="Bash",
                tool_input={"command": "echo untouched"},
            ),
        )
        self.assertEqual(result, {})
        self.assertEqual(self.status()["allocations"], [])

    def test_explicit_reset_clears_tracking_without_faking_event_time(self):
        self.spawn("parent-1", "call-a")
        before = self.status()["last_hook_event_at"]
        policy_hook._reset_state(self.home)
        after = self.status()
        self.assertEqual(after["last_hook_event_at"], before)
        self.assertEqual(after["allocations"], [])


class FailureFallbackTests(unittest.TestCase):
    write_policy=PolicyHookTests.write_policy
    tearDown=PolicyHookTests.tearDown
    event=PolicyHookTests.event
    spawn=PolicyHookTests.spawn
    bind=PolicyHookTests.bind
    def setUp(self):
        PolicyHookTests.setUp(self)
        path=self.home/"control-center"/"subagent-policy.json"
        policy=json.loads(path.read_text(encoding="utf-8"));policy["mode"]="failure_fallback"
        path.write_text(json.dumps(policy),encoding="utf-8")

    def backup(self, call="backup-1", parent="parent-1"):
        return policy_hook.run_event(self.home,self.event("PreToolUse",session_id=parent,tool_use_id=call,tool_input={"model":"alias-B","message":"complete plaintext task","fork_context":False}))

    def assert_allowed_model(self,result,model):
        self.assertEqual(result["hookSpecificOutput"]["updatedInput"]["model"],model)

    def assert_denied(self,result):
        self.assertEqual(result["hookSpecificOutput"]["permissionDecision"],"deny")

    def fail_primary(self,call="primary-1"):
        self.spawn("parent-1",call)
        self.bind("parent-1",call,"",response={"isError":True,"message":"local model startup failed"})

    def test_default_parallel_requests_do_not_use_backup(self):
        self.assert_allowed_model(self.spawn("parent-1","normal-1"),"alias-A")
        self.assert_allowed_model(self.spawn("parent-1","normal-2"),"alias-A")
        self.assert_denied(self.backup())

    def test_one_backup_permission_and_idempotent_replays(self):
        self.fail_primary()
        self.assert_allowed_model(self.backup(),"alias-B")
        self.assert_allowed_model(self.backup(),"alias-B")
        self.bind("parent-1","primary-1","",response={"isError":True})
        self.assert_denied(self.backup("backup-2"))
        self.bind("parent-1","backup-1","",response={"isError":True})
        self.assert_denied(self.backup("backup-3"))

    def test_async_errored_close_allows_backup_in_same_parent_only(self):
        self.spawn("parent-1","primary-1");self.bind("parent-1","primary-1","failed-agent")
        self.assert_denied(self.backup())
        event=self.event("PostToolUse",tool_name="close_agent",tool_use_id="close-1",tool_input={"target":"failed-agent"},tool_response={"previous_status":{"errored":"HTTP 404 {\"error\":\"not available\"}"}})
        policy_hook.run_event(self.home,event)
        self.assert_denied(self.backup("other-parent",parent="parent-2"))
        self.assert_allowed_model(self.backup(),"alias-B")
        policy_hook.run_event(self.home,event)
        self.assert_denied(self.backup("backup-2"))

    def test_running_close_and_completed_turn_do_not_authorize_backup(self):
        self.spawn("parent-1","primary-1");self.bind("parent-1","primary-1","normal-agent")
        policy_hook.run_event(self.home,self.event("SubagentStop",agent_id="normal-agent"))
        self.assert_denied(self.backup())
        policy_hook.run_event(self.home,self.event("PostToolUse",tool_name="close_agent",tool_use_id="close-1",tool_input={"target":"normal-agent"},tool_response={"previous_status":"running"}))
        self.assert_denied(self.backup())

    def test_default_agents_can_resume_while_another_default_is_busy(self):
        self.spawn("parent-1","primary-1");self.bind("parent-1","primary-1","one")
        self.spawn("parent-1","primary-2");self.bind("parent-1","primary-2","two")
        policy_hook.run_event(self.home,self.event("SubagentStop",agent_id="one"))
        result=policy_hook.run_event(self.home,self.event("PreToolUse",tool_name="send_input",tool_use_id="resume-1",tool_input={"target":"one","message":"continue"}))
        self.assertNotEqual(result.get("hookSpecificOutput",{}).get("permissionDecision"),"deny")

    def test_session_end_invalidates_unused_backup_permission(self):
        self.fail_primary();policy_hook.run_event(self.home,self.event("SessionEnd"))
        self.assert_denied(self.backup())


if __name__ == "__main__":
    unittest.main()
