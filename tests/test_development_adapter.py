"""Fixed development transport and shared business ownership, without a game."""
from copy import deepcopy
import json
from pathlib import Path
import threading
import time
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import CampaignAdmissionClosed
from tests import test_bridge_client as bridge_fakes
from tests import test_objects as object_fakes


OP = "0123456789abcdef0123456789abcdef"
SAMPLES = Path(__file__).resolve().parents[1] / "data/acceptance/20261031-campaign/23-growth-capabilities"


def development():
    result = {"schema": 1, "revision": 1, "character_id": "12345", "gate": "PASS", "reason": "", "last_command": None}
    for kind in ("growth", "equipment", "spirit"):
        result[kind] = json.loads((SAMPLES / (kind + "-snapshot.json")).read_text(encoding="utf-8-sig"))
    return result


def fields(action="equipment_enchant", **changes):
    result = dict(action=action, expected_revision=1, operation_id=OP, target_id=7,
                  desired_level=2, max_requests=1, max_money=10, max_stone=2, max_food=0, mode="enchant")
    result.update(changes)
    return result


class DevelopmentBridgeTests(unittest.TestCase):
    setUp = bridge_fakes.BridgeTests.setUp
    client = bridge_fakes.BridgeTests.client
    common = bridge_fakes.BridgeTests.common
    state = bridge_fakes.BridgeTests.state
    ack = bridge_fakes.BridgeTests.ack

    def ready(self):
        client = self.client()
        client.connect(timeout=.2)
        return client

    def responder(self, client, *, mutate=None, status="COMPLETED", command_status="INVOKED"):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            observed = development()
            if name == "development-command":
                typed = kwargs["development_fields"]
                observed["last_command"] = dict(sequence=seq, action=typed["action"],
                    operation_id=typed["operation_id"], status=command_status, reason="fixture")
            state = self.state(client, seq)
            state["development"] = observed
            state["business"] = dict(pending_attempt=False, pending_development=False)
            ack = self.ack(client, seq, name, state, status)
            ack["result"]["development"] = deepcopy(observed)
            if mutate:
                mutate(state, ack)
            bridge._atomic_json(client.session_dir / "state.json", state)
            bridge._atomic_json(client.session_dir / "ack.json", ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_sample_dtos_and_blocked_gate_are_valid(self):
        value = development()
        self.assertTrue(bridge._development_valid(value))
        value["gate"] = "controller_gate:blocked"
        value["equipment"]["gate"] = "BLOCKED"
        self.assertTrue(bridge._development_valid(value))

    def test_schema_rejects_unknown_shapes_types_outcomes_and_duplicates(self):
        changes = [lambda x: x.update(schema=True), lambda x: x.update(character_id="00123"),
            lambda x: x.update(extra=1), lambda x: x["growth"]["tasks"].append(deepcopy(x["growth"]["tasks"][0])),
            lambda x: x["spirit"]["operation"].update(status="SUCCESS"),
            lambda x: x["equipment"]["operation"].update(server=[]),
            lambda x: x["equipment"]["ui"].update(sended=1),
            lambda x: x["growth"]["tasks"][0]["template"].update(quest_id=123),
            lambda x: x["spirit"]["operation"].update(result_semantics="SUCCESS")]
        for change in changes:
            with self.subTest(change=change):
                value = development()
                change(value)
                self.assertFalse(bridge._development_valid(value))

    def test_observation_uses_four_lines_or_exact_typed_fourteen(self):
        client = self.ready()
        self.responder(client)
        client.development_observe(timeout=.2)
        self.assertEqual((client.session_dir / "command.txt").read_text().splitlines(),
            ["version=3", "nonce=" + client.nonce, "sequence=1", "command=development-observe"])
        typed = fields("equipment_observe")
        client.development_observe(fields=typed, timeout=.2)
        lines = (client.session_dir / "command.txt").read_text().splitlines()
        self.assertEqual(lines[4:], [f"{key}={typed[key]}" for key in bridge.DEVELOPMENT_FIELDS])
        self.assertEqual(len(lines), 14)
        self.assertIsNone(client.last_state["development"]["last_command"])

    def test_command_requires_exact_correlated_completion(self):
        client = self.ready()
        self.responder(client)
        wire = client.development_command(**fields(), timeout=.2)
        self.assertEqual(wire["requested_seq"], 1)
        self.assertEqual(wire["ack"]["result"]["development"], wire["state"]["development"])
        self.assertEqual(len((client.session_dir / "command.txt").read_text().splitlines()), 14)
        self.assertEqual(client._send.call_count, 1)

    def test_rejection_is_valid_only_with_rejection_receipt(self):
        for command_status in ("REJECTED", "NOT_INVOKED"):
            with self.subTest(command_status=command_status):
                client = self.ready()
                self.responder(client, status="REJECTED", command_status=command_status)
                self.assertEqual(client.development_command(**fields(), timeout=.2)["ack"]["status"], "REJECTED")
                client._finish_stopped(self.state(client, client._seq, stopped=True))

    def test_wrong_action_operation_sequence_status_or_state_is_unknown(self):
        def command(**changes):
            def change(state, ack):
                state["development"]["last_command"].update(changes)
                ack["result"]["development"]["last_command"].update(changes)
            return change
        mutations = [command(operation_id="f" * 32), command(action="growth_open"),
            command(sequence=2), command(status="QUEUED"),
            lambda s, a: a["result"]["development"].update(revision=2),
            lambda s, a: s.update(last_command_seq=0)]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                # Separate projects retain the unknown lease from each case.
                self.project = self.project / "case"
                self.project.mkdir()
                client = self.client()
                client.session_dir.mkdir(parents=True)
                client._owns_lease = True
                client.lease_path.parent.mkdir()
                bridge._atomic_json(client.lease_path, {"session_id": client.nonce})
                self.responder(client, mutate=mutation)
                with self.assertRaises(bridge.BridgeUnknown):
                    client.development_command(**fields(), timeout=.01)
                self.assertTrue(client.is_unknown)
                self.assertTrue(client.lease_path.exists())
                self.assertEqual(client._send.call_count, 1)

    def test_type_ranges_and_injection_fail_before_any_publication(self):
        client = self.ready()
        changes = [dict(action="arbitrary"), dict(expected_revision=True), dict(target_id=32768),
            dict(desired_level=256), dict(max_requests=0), dict(max_requests=101),
            dict(max_money=2**31), dict(max_stone=-1), dict(max_food=1.0),
            dict(operation_id=OP.upper()), dict(mode="fruit\ncommand=stop")]
        for change in changes:
            with self.subTest(change=change), self.assertRaises(bridge.BridgeError):
                client.development_command(**fields(**change), timeout=.01)
        with self.assertRaises(bridge.BridgeError):
            client.request("development-command")
        with self.assertRaises(bridge.BridgeError):
            client._send("development-command", development_fields=fields(action="equipment_observe"))
        with self.assertRaises(bridge.BridgeError):
            client.development_observe(fields={})
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / "command.txt").exists())
        self.assertFalse(client.is_unknown)

    def test_stale_ack_waits_for_this_sequence_without_retry(self):
        client = self.ready()
        self.responder(client)
        client.development_command(**fields(), timeout=.2)
        original = client._send.side_effect
        def stale(name, **kwargs):
            seq = original(name, **kwargs)
            path = client.session_dir / "ack.json"
            ack = bridge._read_json(path)
            ack["command_seq"] = seq - 1
            bridge._atomic_json(path, ack)
            return seq
        client._send.side_effect = stale
        with self.assertRaises(bridge.BridgeUnknown):
            client.development_command(**fields(expected_revision=2), timeout=.01)
        self.assertEqual(client._send.call_count, 2)
        self.assertEqual(client._seq, 2)
        self.assertTrue(client.lease_path.exists())

    def test_cancel_before_submit_has_no_publication(self):
        client = self.ready()
        cancel = threading.Event()
        cancel.set()
        self.assertTrue(client.development_command(**fields(), cancel=cancel)["cancelled_before_submit"])
        self.assertEqual(client._seq, 0)

    def test_unacknowledged_mutation_is_never_retried_and_retains_lease(self):
        client = self.ready()
        original = client._send
        client._send = Mock(wraps=original)
        with self.assertRaises(bridge.BridgeUnknown):
            client.development_command(**fields(), timeout=.01)
        self.assertEqual(client._send.call_count, 1)
        self.assertTrue(client.lease_path.exists())
        self.assertTrue(client.is_unknown)

    def test_128_tasks_round_trip_exceeds_old_json_bound(self):
        value = development()
        row = value["growth"]["tasks"][0]
        rows = []
        for index in range(128):
            task = deepcopy(row)
            task["quest_id"] = task["template"]["quest_id"] = index + 1
            task["template"]["name"] = "任" * 100
            task["template"]["desc"] = "务" * 100
            rows.append(task)
        value["growth"]["tasks"] = rows
        self.assertTrue(bridge._development_valid(value))
        path = self.project / "large.json"
        bridge._atomic_json(path, {"development": value})
        self.assertGreater(path.stat().st_size, 128 * 1024)
        self.assertEqual(bridge._read_json(path)["development"], value)

    def test_pending_development_survives_clean_stop_and_blocks_both_mutations(self):
        client = self.ready()
        state = self.state(client, stopped=True)
        state["business"] = dict(pending_attempt=False, pending_development=True,
            character_id="999", mutation_attempted=True, development=development())
        client._finish_stopped(state)
        guard = bridge._read_json(client.business_guard_path)
        self.assertEqual(guard["character_id"], "12345")
        replacement = self.ready()
        with self.assertRaisesRegex(bridge.BridgeError, "未决养成"):
            replacement.development_command(**fields())
        with self.assertRaisesRegex(bridge.BridgeError, "未决养成"):
            replacement.campaign_command("enter", expected_revision=1)
        self.assertEqual(replacement._seq, 0)
        self.responder(replacement)
        replacement.development_observe(timeout=.2)


class DevelopmentOwnerTests(unittest.TestCase):
    setUp = object_fakes.ControllerTests.setUp
    cleanup_controller = object_fakes.ControllerTests.cleanup_controller
    ready = object_fakes.ControllerTests.ready

    def development_ready(self):
        client = self.ready()
        client.last_state["campaign"] = {"character_id": "12345", "revision": 1}
        client.last_state["development"] = development()
        client.campaign_observe = Mock(side_effect=lambda **kw: client.request("campaign-observe"))
        client.campaign_command = Mock(side_effect=lambda *args, **kw: client.request("campaign-command"))
        client.development_observe = Mock(side_effect=lambda **kw: client.request("development-observe"))
        client.development_command = Mock(side_effect=lambda *args, **kw: client.request("development-command"))
        return client, self.controller.acquire_campaign("development")

    def test_same_owner_snapshots_are_copied_and_typed_observation_is_forwarded(self):
        client, token = self.development_ready()
        typed = fields("equipment_observe")
        snapshot = self.controller.observe_development(token, fields=typed)
        client.development_observe.assert_called_once_with(fields=typed, timeout=3.0)
        snapshot["development"]["revision"] = 99
        self.assertEqual(self.controller.development_snapshot(token)["development"]["revision"], 1)
        with self.assertRaises(bridge.BridgeError):
            self.controller.acquire_campaign("second-owner")
        self.controller.release_campaign(token)

    def test_role_mismatch_closes_admission_before_command(self):
        client, token = self.development_ready()
        client.last_state["development"]["character_id"] = "54321"
        with self.assertRaisesRegex(bridge.BridgeError, "身份不一致"):
            self.controller.submit_development(token, **fields())
        client.development_command.assert_not_called()
        self.controller.release_campaign(token)

    def test_pause_is_nonblocking_and_shared_across_campaign_and_development(self):
        client, token = self.development_ready()
        entered, release = threading.Event(), threading.Event()
        def command(*args, **kwargs):
            entered.set()
            release.wait(1)
            return {"state": client.last_state}
        client.development_command.side_effect = command
        thread = threading.Thread(target=lambda: self.controller.submit_development(token, **fields()))
        thread.start()
        self.assertTrue(entered.wait(1))
        started = time.monotonic()
        self.assertTrue(self.controller.pause_campaign(token))
        self.assertLess(time.monotonic() - started, .1)
        release.set()
        thread.join(1)
        self.assertFalse(thread.is_alive())
        with self.assertRaises(CampaignAdmissionClosed):
            self.controller.submit_development(token, **fields())
        with self.assertRaises(CampaignAdmissionClosed):
            self.controller.submit_campaign(token, "enter", expected_revision=1)
        self.controller.observe_development(token)
        self.assertEqual(client.development_command.call_count, 1)
        self.assertTrue(self.controller.resume_campaign(token))
        self.controller.submit_development(token, **fields())
        self.assertEqual(client.development_command.call_count, 2)
        self.controller.release_campaign(token)

    def test_failure_closes_admission_and_stop_prevents_new_commands(self):
        client, token = self.development_ready()
        client.development_command.side_effect = bridge.BridgeUnknown("fixture")
        with self.assertRaises(bridge.BridgeUnknown):
            self.controller.submit_development(token, **fields())
        with self.assertRaises(CampaignAdmissionClosed):
            self.controller.submit_development(token, **fields())
        self.assertTrue(self.controller.resume_campaign(token))
        self.controller.stop_campaign(token)
        with self.assertRaises(CampaignAdmissionClosed):
            self.controller.submit_development(token, **fields())
        self.assertTrue(self.controller.development_snapshot(token)["stopping"])
        self.controller.release_campaign(token)


if __name__ == "__main__":
    unittest.main()
