"""Protocol and ownership fakes. No native executable or live game is used."""
import hashlib
from contextlib import contextmanager
from copy import deepcopy
import json
import errno
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.mono_runtime import ProcessIdentity, JS_SHA256, MONO_SHA256, MANAGED_HASHES


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name)
        self.identity = ProcessIdentity(100, 123456, 200, 300)
        assets = self.project / "jiangshen_assistant" / "runtime" / "win32"
        assets.mkdir(parents=True)
        files = {}
        for name in bridge.ASSET_NAMES:
            raw = ("fake asset " + name).encode("ascii")
            (assets / name).write_bytes(raw)
            files[name] = hashlib.sha256(raw).hexdigest()
        self.manifest = {"protocol": 3, "native_protocol": 2, "bridge_id": bridge.BRIDGE_ID,
                         "campaign_interface": bridge.CAMPAIGN_INTERFACE,
                         "files": files, "build_id": "fake-build", "heartbeat_seconds": 5,
                         "lease_seconds": 20, "recovery_seconds": 10,
                         "session_root": str(self.project / "data" / "object_sessions"),
                         "supported_game": {"js_exe_sha256": JS_SHA256, "mono_sha256": MONO_SHA256,
                                            "managed": dict(MANAGED_HASHES)}}
        bridge._atomic_json(assets / "manifest.json", self.manifest)

    def client(self):
        value = bridge.BridgeClient(self.project, self.identity, Mock(), status_reader=lambda *_: "same")
        def spawn(*args, **kwargs):
            bridge._atomic_json(value.session_dir / "state.json", self.state(value))
            return SimpleNamespace(pid=987, stdout=iter(()))
        value._popen = Mock(side_effect=spawn)
        return value

    def common(self, client):
        return {"protocol": 3, "bridge_id": bridge.BRIDGE_ID, "nonce": client.nonce,
                "session_id": client.nonce, "pid": self.identity.pid,
                "process_creation_filetime": self.identity.creation_time, "verified_native_tid": self.identity.tid,
                "updated_utc": bridge._now()}

    def state(self, client, seq=0, action=None, stopped=False):
        return {**self.common(client), "lifecycle": "stopped" if stopped else "ready", "ready": not stopped,
                "main_thread_verified": True, "component_destroyed": stopped, "last_command_seq": seq,
                "main_instance_id": 321, "gate": "PASS", "reconnect_allowed": stopped,
                "snapshot": {"safe": True, "main_enabled": True, "gate": "PASS", "main_instance_id": 321},
                "campaign": {"interface": bridge.CAMPAIGN_INTERFACE},
                "action": action or {"command_seq": 0, "outcome": "NONE", "restore_verified": False, "mutation_attempted": False}}

    def ack(self, client, seq, name, state, status="COMPLETED"):
        return {**self.common(client), "command_seq": seq, "command_name": name, "status": status,
                "result": {"state": state["lifecycle"], "component_destroyed": state["component_destroyed"],
                           "snapshot": state["snapshot"], "action": state["action"],
                           "restore_verified": state["action"]["restore_verified"]}}

    def publish(self, client, seq, name, *, status="COMPLETED", action=None, stopped=False):
        state = self.state(client, seq, action, stopped)
        bridge._atomic_json(client.session_dir / "state.json", state)
        bridge._atomic_json(client.session_dir / "ack.json", self.ack(client, seq, name, state, status))
        return state

    def test_passive_construction_does_not_stage_or_start_host(self):
        client = self.client()
        client._popen.assert_not_called()
        self.assertFalse(client.root.exists())

    def test_stages_fixed_hashes_and_uses_same_immutable_bin_on_clean_reconnect(self):
        first = self.client()
        self.assertEqual(first.connect(timeout=0.2)["lifecycle"], "ready")
        first._finish_stopped(self.state(first, stopped=True))
        self.assertFalse(first.lease_path.exists())
        second = self.client()
        second.connect(timeout=0.2)
        self.assertNotEqual(first.nonce, second.nonce)
        self.assertEqual(first.bin_dir, second.bin_dir)
        self.assertEqual(second._popen.call_args.args[0][0], str(first.bin_dir / "bridge_host.exe"))

    def test_altered_packaged_asset_refuses_before_bootstrap_and_releases_lease(self):
        client = self.client()
        (client.assets / "bridge.dll").write_bytes(b"changed")
        with self.assertRaises(bridge.BridgeError):
            client.connect(timeout=0.01)
        client._popen.assert_not_called()
        self.assertFalse(client.lease_path.exists())

    def test_unknown_prior_session_blocks_new_build_and_helper(self):
        first = self.client()
        first.connect(timeout=0.2)
        first.mark_unknown("prior_gui_crash")
        second = self.client()
        with self.assertRaises(bridge.BridgeUnknown):
            second.connect(timeout=0.01)
        second._popen.assert_not_called()
        self.assertEqual(json.loads(first.lease_path.read_text())["session_id"], first.nonce)

    def test_atomic_claim_blocks_reclaim_even_for_clean_prior_ledger(self):
        first = self.client()
        first.connect(timeout=0.2)
        first._write_ledger("stopped", component_destroyed=True)
        first.lease_path.with_suffix(".claim").write_text("another contender")
        second = self.client()
        with self.assertRaises(bridge.BridgeUnknown):
            second.connect(timeout=0.01)
        second._popen.assert_not_called()
        self.assertEqual(json.loads(first.lease_path.read_text())["session_id"], first.nonce)

    def test_exact_command_sequence_ack_and_action_result_correlation(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        written = []
        def accepted(name):
            seq = send(name)
            written.append((seq, (client.session_dir / "command.txt").read_text()))
            action = {"command_seq": seq, "outcome": "PASS", "restore_verified": True,
                      "restore_method_invoked": True, "mutation_attempted": True}
            self.publish(client, seq, name, action=action)
            return seq
        client._send = accepted
        result = client.verify()
        self.assertEqual(result["ack"]["result"]["action"]["command_seq"], 1)
        self.assertEqual(written[0][1], f"version=3\nnonce={client.nonce}\nsequence=1\ncommand=disable-restore\n")
        self.assertEqual(len(written), 1)

    def test_previous_pass_cannot_prove_new_action(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        def stale(name):
            seq = send(name)
            action = {"command_seq": seq-1, "outcome": "PASS", "restore_verified": True}
            self.publish(client, seq, name, action=action)
            return seq
        client._send = Mock(side_effect=stale)
        with self.assertRaises(bridge.BridgeUnknown):
            client.verify()
        self.assertEqual(client._send.call_count, 1)
        self.assertTrue(client.lease_path.exists())

    def test_wrong_nonce_ack_rejected(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        def wrong(name):
            seq = send(name)
            state = self.publish(client, seq, name)
            ack = self.ack(client, seq, name, state)
            ack["nonce"] = "0" * 32
            bridge._atomic_json(client.session_dir / "ack.json", ack)
            return seq
        client._send = Mock(side_effect=wrong)
        original = bridge._open_shared_read
        with patch.object(bridge, "_open_shared_read", wraps=original) as opened:
            with self.assertRaises(bridge.BridgeUnknown):
                client.heartbeat()
        self.assertEqual(sum(call.args[0] == client.session_dir / "ack.json" for call in opened.call_args_list), 1)
        self.assertEqual(client._send.call_count, 1)
        self.assertTrue(client.lease_path.exists())

    def test_unacked_action_not_overwritten_by_stop_or_retried(self):
        client = self.client()
        client.connect(timeout=0.2)
        cancel = threading.Event()
        send = client._send
        def submitted(name):
            seq = send(name)
            cancel.set()
            return seq
        client._send = submitted
        with self.assertRaises(bridge.BridgeUnknown):
            client.request("disable-restore", timeout=0.02, cancel=cancel)
        self.assertEqual(client._seq, 1)
        self.assertIn("command=disable-restore", (client.session_dir / "command.txt").read_text())
        self.assertTrue(client.lease_path.exists())

    def test_stop_supersedes_only_accepted_action_and_waits_for_destroyed(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        cancel = threading.Event()
        def apply(name):
            seq = send(name)
            cancel.set()
            action = {"command_seq": 1, "outcome": "PENDING" if name != "stop" else "PASS",
                      "restore_verified": name == "stop", "mutation_attempted": True,
                      "restore_method_invoked": name == "stop"}
            self.publish(client, seq, name, status="ACCEPTED" if name != "stop" else "COMPLETED",
                         action=action, stopped=name == "stop")
            return seq
        client._send = Mock(side_effect=apply)
        result = client.verify(cancel)
        self.assertEqual(result["command"], "stop")
        self.assertEqual([c.args[0] for c in client._send.call_args_list], ["disable-restore", "stop"])
        self.assertTrue(client.terminal)
        self.assertFalse(client.lease_path.exists())

    def test_access_failure_retains_lease_but_proven_pid_reuse_releases(self):
        client = self.client()
        client.connect(timeout=0.2)
        client._status_reader = lambda *_: "unknown"
        self.assertFalse(client.target_exited())
        self.assertTrue(client.lease_path.exists())
        client._status_reader = lambda *_: "gone"
        self.assertTrue(client.target_exited())
        self.assertFalse(client.lease_path.exists())

    def test_fresh_ack_waits_for_matching_state_instead_of_old_pass(self):
        client = self.client()
        client.connect(timeout=0.2)
        old = self.state(client)
        send = client._send
        def missing_state(name):
            seq = send(name)
            fresh = self.state(client, seq, {"command_seq": seq, "outcome": "PASS", "restore_verified": True})
            bridge._atomic_json(client.session_dir / "ack.json", self.ack(client, seq, name, fresh))
            bridge._atomic_json(client.session_dir / "state.json", old)
            return seq
        client._send = missing_state
        with self.assertRaises(bridge.BridgeUnknown):
            client.request("disable-restore", timeout=0.02)
        self.assertEqual(client._ledger["state"], "unknown")

    def test_autonomous_clean_stop_releases_lease_without_claiming_command_success(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        def autonomous_stop(name):
            seq = send(name)
            stopped = self.state(client, seq-1, stopped=True)
            stopped["updated_utc"] = "2020-01-01T00:00:00+00:00"
            bridge._atomic_json(client.session_dir / "state.json", stopped)
            return seq
        client._send = autonomous_stop
        result = client.request("disable-restore", timeout=0.2)
        self.assertTrue(client.terminal)
        self.assertFalse(client.lease_path.exists())
        self.assertEqual(result["interrupted_command"], "disable-restore")
        self.assertNotIn("ack", result)

    def test_missing_action_or_unrestored_mutation_cannot_release_lease(self):
        client = self.client()
        for action in (None, {}, {"outcome": "UNKNOWN", "mutation_attempted": True, "restore_verified": False},
                       {"outcome": "PASS", "mutation_attempted": True, "restore_verified": False}):
            state = self.state(client, stopped=True)
            state["action"] = action
            self.assertFalse(bridge._is_clean(state))

    def test_cancelled_action_waiting_behind_another_request_only_sends_stop(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        names = []
        def stopped(name):
            names.append(name)
            seq = send(name)
            self.publish(client, seq, name, stopped=True)
            return seq
        client._send = stopped
        cancel = threading.Event()
        results = []
        client._request_lock.acquire()
        worker = threading.Thread(target=lambda: results.append(client.verify(cancel)))
        worker.start()
        cancel.set()
        client._request_lock.release()
        worker.join(1.0)
        self.assertFalse(worker.is_alive())
        self.assertEqual(names, ["stop"])
        self.assertTrue(client.terminal)

    def test_host_argv_pins_process_creation_identity(self):
        client = self.client()
        client.connect(timeout=0.2)
        argv = client._popen.call_args.args[0]
        self.assertEqual(argv[-1], str(self.identity.creation_time))
        self.assertEqual(argv[-2], client.nonce)

    def test_publication_failure_keeps_sequence_and_first_unknown_cause(self):
        client = self.client()
        client.connect(timeout=0.2)
        original = os.replace
        denied = PermissionError(errno.EACCES, "own sharing-denial fixture")
        denied.winerror = 32
        publications = []
        def replace(source, target):
            if Path(target).name == "command.txt":
                publications.append(Path(source).read_bytes())
                raise denied
            return original(source, target)
        with patch.object(bridge.os, "replace", side_effect=replace), patch.object(bridge, "_ATOMIC_REPLACE_TIMEOUT", 0.02):
            with self.assertRaises(PermissionError) as caught:
                client.request("snapshot", timeout=0.05)
            self.assertIs(caught.exception, denied)
            count = len(publications)
            with self.assertRaises(bridge.BridgeUnknown):
                client.request("snapshot", timeout=0.05)
            self.assertEqual(len(publications), count)
        self.assertGreater(count, 1)
        self.assertEqual(len(set(publications)), 1)
        self.assertIn(b"sequence=1\n", publications[0])
        self.assertEqual(client._seq, 1)
        ledger = bridge._read_json(client.ledger_path)
        self.assertEqual(ledger["reason"], str(denied))
        self.assertEqual(ledger["first_unknown"]["winerror"], 32)
        self.assertEqual(ledger["first_unknown"]["command_seq"], 1)
        self.assertEqual(ledger["first_unknown"]["stage"], "command_publish")
        self.assertIn("结果未知", ledger["last_unknown"]["message"])
        self.assertTrue(client.lease_path.exists())

    def test_transient_ack_read_contention_completes_same_command_without_resend(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        def completed(name):
            seq = send(name)
            self.publish(client, seq, name)
            return seq
        client._send = Mock(side_effect=completed)
        ack_path = client.session_dir / "ack.json"
        original = bridge._open_shared_read
        ack_opens = []
        @contextmanager
        def read(path):
            if path == ack_path:
                ack_opens.append(str(path))
                if len(ack_opens) < 3:
                    error = PermissionError(errno.EACCES, "own brief ack contention", str(path))
                    error.winerror = 32
                    raise error
            with original(path) as stream:
                yield stream
        with patch.object(bridge.os, "name", "nt"), patch.object(bridge, "_open_shared_read", side_effect=read):
            result = client.request("snapshot", timeout=0.3)
        self.assertEqual(result["ack"]["status"], "COMPLETED")
        self.assertEqual(len(ack_opens), 3)
        self.assertEqual(client._send.call_count, 1)
        self.assertEqual(client._seq, 1)
        self.assertFalse(client._unknown)
        self.assertEqual((client.session_dir / "command.txt").read_text(),
                         f"version=3\nnonce={client.nonce}\nsequence=1\ncommand=snapshot\n")
        self.assertEqual(bridge._read_json(client.ledger_path)["state"], "ready")

    def test_exhausted_ack_read_contention_keeps_first_unknown_and_never_resends(self):
        client = self.client()
        client.connect(timeout=0.2)
        send = client._send
        def completed(name):
            seq = send(name)
            self.publish(client, seq, name)
            return seq
        client._send = Mock(side_effect=completed)
        ack_path = client.session_dir / "ack.json"
        first = PermissionError(errno.EACCES, "own first ack contention", str(ack_path))
        first.winerror = 32
        later = PermissionError(errno.EACCES, "own later ack contention", str(ack_path))
        later.winerror = 33
        original = bridge._open_shared_read
        ack_opens = []
        @contextmanager
        def read(path):
            if path == ack_path:
                ack_opens.append(str(path))
                raise first if len(ack_opens) == 1 else later
            with original(path) as stream:
                yield stream
        with patch.object(bridge.os, "name", "nt"), patch.object(bridge, "_READ_RETRY_TIMEOUT", 0.03), \
                patch.object(bridge, "_open_shared_read", side_effect=read):
            with self.assertRaises(PermissionError) as caught:
                client.request("snapshot", timeout=0.3)
            self.assertIs(caught.exception, first)
            count = len(ack_opens)
            command = (client.session_dir / "command.txt").read_bytes()
            with self.assertRaises(bridge.BridgeUnknown):
                client.request("snapshot", timeout=0.3)
            self.assertEqual(len(ack_opens), count)
            self.assertEqual((client.session_dir / "command.txt").read_bytes(), command)
        client.mark_unknown("subsequent diagnostic")
        self.assertGreater(count, 1)
        self.assertEqual(client._send.call_count, 2)  # Second call rejects before publication/sequence allocation.
        self.assertEqual(client._seq, 1)
        ledger = bridge._read_json(client.ledger_path)
        detail = ledger["first_unknown"]
        self.assertEqual(detail["message"], str(first))
        self.assertEqual(detail["exception_type"], "PermissionError")
        self.assertEqual(detail["winerror"], 32)
        self.assertEqual(detail["errno"], errno.EACCES)
        self.assertEqual(detail["filename"], str(ack_path))
        self.assertEqual(detail["stage"], "ack_wait")
        self.assertEqual(detail["read_stage"], "open")
        self.assertEqual(detail["read_attempts"], count)
        self.assertGreaterEqual(detail["read_elapsed_ms"], 25)
        self.assertEqual(ledger["last_unknown"]["message"], "subsequent diagnostic")
        self.assertTrue(client.lease_path.exists())


class CampaignBridgeTests(unittest.TestCase):
    setUp = BridgeTests.setUp
    client = BridgeTests.client
    common = BridgeTests.common
    state = BridgeTests.state
    ack = BridgeTests.ack
    def campaign(self, *, settled=False):
        return {"schema": 1, "interface": bridge.CAMPAIGN_INTERFACE, "revision": 3, "available": True, "gate": "PASS", "phase": "RESULT" if settled else "FORMATION",
                "character_id": "18446744073709551615", "campaign_id": 12, "troop_id": 7,
                "result_kind": "NORMAL" if settled else "", "eligible_troops": [
                    {"troop_id": 7, "canattack": True, "visible": True, "killed": False, "challenge_level": 6}],
                "tutorial": {"kind": "NONE", "step": "", "gate": ""},
                "attempt": {"attempt_id": "a" * 32 if settled else "", "character_id": "18446744073709551615" if settled else "", "command_seq": 1 if settled else 0,
                            "campaign_id": 12 if settled else 0, "troop_id": 7 if settled else 0,
                            "status": "SETTLED" if settled else "NONE", "report_id": "18446744073709551615" if settled else "0",
                            "outcome": "WIN" if settled else "UNKNOWN", "evidence": "captured_authoritative_receipt_applied" if settled else "not_started"}}

    def endpoint(self, client, *, status="COMPLETED", corrupt=None):
        send = client._send
        def apply(name, **kwargs):
            seq = send(name, **kwargs)
            state = self.state(client, seq)
            state["campaign"] = self.campaign()
            state["business"] = {"mutation_attempted": name == "campaign-command", "pending_attempt": False, "resource_rollback": False,
                                 "attempt": state["campaign"]["attempt"]}
            ack = self.ack(client, seq, name, state, status)
            ack["result"]["campaign"] = state["campaign"]
            if name == "campaign-command":
                fields = kwargs["campaign_fields"]
                ack["result"]["campaign_command"] = {"command_seq": seq, "action": fields["action"], "attempt_id": fields["attempt_id"],
                                                        "invoked": status == "COMPLETED", "reason": "" if status == "COMPLETED" else "normal_troop_selection_gate"}
            if corrupt:
                corrupt(state, ack)
            bridge._atomic_json(client.session_dir / "state.json", state)
            bridge._atomic_json(client.session_dir / "ack.json", ack)
            return seq
        client._send = Mock(side_effect=apply)

    def test_fixed_campaign_wire_and_exact_submission_receipt(self):
        client = self.client(); client.connect(timeout=0.2); self.endpoint(client)
        result = client.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="a" * 32)
        self.assertEqual(result["requested_seq"], 1)
        self.assertTrue(result["ack"]["result"]["campaign_command"]["invoked"])
        self.assertEqual((client.session_dir / "command.txt").read_text(),
                         f"version=3\nnonce={client.nonce}\nsequence=1\ncommand=campaign-command\naction=start\nexpected_revision=3\ncampaign_id=12\ntroop_id=7\nattempt_id={'a' * 32}\n")
        self.assertEqual(client._send.call_count, 1)

    def test_campaign_arguments_rejected_without_poisoning_or_publication(self):
        client = self.client(); client.connect(timeout=0.2)
        invalid = [dict(action="eval", expected_revision=3), dict(action="start", expected_revision=True, campaign_id=12, troop_id=7, attempt_id="a" * 32),
                   dict(action="start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="bad\ncommand=stop"),
                   dict(action="prepare", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="a" * 32),
                   dict(action="select", expected_revision=3, campaign_id=0, troop_id=7), dict(action="select", expected_revision=3, campaign_id=12, troop_id=256)]
        for kwargs in invalid:
            with self.subTest(kwargs=kwargs), self.assertRaises(bridge.BridgeError):
                client.campaign_command(**kwargs)
        self.assertEqual(client._seq, 0); self.assertFalse(client.is_unknown)
        self.assertFalse((client.session_dir / "command.txt").exists())

    def test_readonly_campaign_observe_preserves_u64_identity(self):
        client = self.client(); client.connect(timeout=0.2); self.endpoint(client)
        result = client.campaign_observe()
        self.assertEqual(result["state"]["campaign"]["character_id"], "18446744073709551615")
        self.assertIn("command=campaign-observe\n", (client.session_dir / "command.txt").read_text())

    def test_stale_campaign_action_receipt_is_unknown_and_never_retried(self):
        client = self.client(); client.connect(timeout=0.2)
        self.endpoint(client, corrupt=lambda state, ack: ack["result"]["campaign_command"].update(command_seq=0))
        with self.assertRaises(bridge.BridgeUnknown):
            client.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="a" * 32)
        self.assertEqual(client._send.call_count, 1); self.assertTrue(client.lease_path.exists())

    def test_campaign_rejection_keeps_exact_noninvocation_receipt(self):
        client = self.client(); client.connect(timeout=0.2); self.endpoint(client, status="REJECTED")
        wire = client.campaign_command("select", expected_revision=3, campaign_id=12, troop_id=7)
        self.assertFalse(wire["ack"]["result"]["campaign_command"]["invoked"])
        self.assertFalse(client.is_unknown)

    def test_cancel_before_campaign_submit_never_writes_command(self):
        client = self.client(); client.connect(timeout=0.2)
        cancel = threading.Event(); cancel.set()
        wire = client.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="a" * 32, cancel=cancel)
        self.assertTrue(wire["cancelled_before_submit"]); self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / "command.txt").exists())

    def test_provisional_or_inconsistent_result_cannot_claim_settled(self):
        value = self.campaign(settled=True)
        self.assertTrue(bridge._campaign_valid(value))
        for changes in ({"evidence": "initial_iswin"}, {"outcome": "UNKNOWN"}, {"report_id": "0"}, {"attempt_id": ""}, {"command_seq": 0}):
            damaged = json.loads(json.dumps(value)); damaged["attempt"].update(changes)
            with self.subTest(changes=changes): self.assertFalse(bridge._campaign_valid(damaged))

    def test_queued_receipt_proves_both_outcomes_but_progress_only_proves_win(self):
        for outcome in ('WIN', 'LOSS'):
            snap = self.campaign(settled=True)
            snap['attempt'].update(outcome=outcome, evidence='captured_queued_authoritative_receipt_applied')
            self.assertTrue(bridge._campaign_valid(snap))
            snap['attempt']['evidence'] = 'anchored_mainline_progress_applied'
            self.assertEqual(bridge._campaign_valid(snap), outcome == 'WIN')
            snap['attempt']['evidence'] = 'target_already_cleared'
            self.assertFalse(bridge._campaign_valid(snap))

    def test_real_parser_allows_only_known_nonblocking_hint_and_dialogue_waits(self):
        value = self.campaign(); value["tutorial"] = {"kind": "HINT", "step": "new-skill", "gate": ""}
        self.assertTrue(bridge._campaign_valid(value))
        value["tutorial"]["step"] = "buy-skill"
        self.assertFalse(bridge._campaign_valid(value))
        value["phase"] = "SCENARIO"
        for gate in ("PASS", "WAITING_TEXT", "WAITING_MOVEMENT", "WAITING_NEXT", "WAITING_UI", "WAITING_SCENARIO_END"):
            value["tutorial"] = {"kind": "SCENARIO", "step": "12:1", "gate": gate}
            with self.subTest(gate=gate): self.assertTrue(bridge._campaign_valid(value))

    def test_business_stop_preserves_pending_facts_without_no_action_claim(self):
        client = self.client(); client.connect(timeout=0.2)
        state = self.state(client, stopped=True)
        state["business"] = {"mutation_attempted": True, "pending_attempt": True, "resource_rollback": False, "attempt": {"attempt_id": "a" * 32, "status": "WAITING"}}
        client._finish_stopped(state)
        ledger = bridge._read_json(client.ledger_path)
        self.assertFalse(ledger["no_action"]); self.assertTrue(ledger["no_verification_action"])
        self.assertEqual(ledger["business"], state["business"]); self.assertFalse(client.lease_path.exists())

    def test_old_business_protocol_manifest_rejected_before_bootstrap(self):
        client = self.client(); self.manifest["protocol"] = 2
        bridge._atomic_json(client.assets / "manifest.json", self.manifest)
        with self.assertRaises(bridge.BridgeError): client.connect(timeout=0.1)
        client._popen.assert_not_called(); self.assertFalse(client.lease_path.exists())

    def test_clean_reconnect_retains_unresolved_business_admission_block(self):
        first = self.client(); first.connect(timeout=0.2)
        state = self.state(first, stopped=True)
        state["business"] = {"mutation_attempted": True, "pending_attempt": True, "resource_rollback": False,
                             "attempt": {"attempt_id": "a" * 32, "status": "WAITING"}}
        first._finish_stopped(state)
        self.assertFalse(first.lease_path.exists())
        self.assertTrue(first.business_guard_path.exists())
        second = self.client(); second.connect(timeout=0.2)
        with self.assertRaises(bridge.BridgeError):
            second.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
        self.assertFalse(second.is_unknown)
        self.endpoint(second)
        self.assertEqual(second.campaign_observe()["command"], "campaign-observe")
        ledger = bridge._read_json(second.ledger_path)
        self.assertEqual(ledger["prior_unresolved_business"]["session_id"], first.nonce)

    def test_business_guard_blocks_whole_process_and_settled_history_does_not_block(self):
        client = self.client(); client.root.mkdir(parents=True); client.lease_path.parent.mkdir()
        guard = {"protocol": 3, "pid": self.identity.pid, "process_creation_filetime": self.identity.creation_time,
                 "character_id": "777", "business": {"pending_attempt": False, "mutation_attempted": True}}
        bridge._atomic_json(client.business_guard_path, guard)
        client.connect(timeout=0.2)
        self.assertIsNone(client._prior_business)
        self.assertTrue(client.business_guard_path.exists())
        client._prior_business = {**guard, "business": {"pending_attempt": True, "character_id": "777", "attempt": {"character_id": "777"}}}
        self.endpoint(client)
        client.campaign_observe()
        # A different role cannot bypass unresolved work in this process.
        with self.assertRaises(bridge.BridgeError):
            client.campaign_command("select", expected_revision=3, campaign_id=12, troop_id=7)
        client._prior_business["character_id"] = "18446744073709551615"
        with self.assertRaises(bridge.BridgeError):
            client.campaign_command("select", expected_revision=3, campaign_id=12, troop_id=7)
        self.assertEqual(client._seq, 1)
        self.assertFalse(client.is_unknown)

    def test_guard_rejects_old_schema_or_missing_business_without_bootstrap(self):
        for damage in ({"protocol": 2}, {"business": None}, {"process_creation_filetime": 1}):
            with self.subTest(damage=damage):
                client = self.client(); client.root.mkdir(parents=True, exist_ok=True); client.lease_path.parent.mkdir(exist_ok=True)
                guard = {"protocol": 3, "pid": self.identity.pid, "process_creation_filetime": self.identity.creation_time,
                         "business": {"pending_attempt": True}, **damage}
                bridge._atomic_json(client.business_guard_path, guard)
                with self.assertRaises(bridge.BridgeUnknown): client.connect(timeout=0.1)
                client._popen.assert_not_called()
                self.assertTrue(client.business_guard_path.exists())

    def test_clean_no_action_stop_never_creates_unresolved_business_guard(self):
        client = self.client(); client.connect(timeout=0.2)
        client._finish_stopped(self.state(client, stopped=True))
        self.assertFalse(client.business_guard_path.exists())
        self.assertTrue(bridge._read_json(client.ledger_path)["no_action"])

    def pending_business(self, origin="18446744073709551615"):
        attempt = self.campaign(settled=True)["attempt"]
        attempt.update(character_id=origin, status="UNKNOWN", report_id="0", outcome="UNKNOWN", evidence="character_identity_changed")
        return {"character_id": origin, "mutation_attempted": True, "pending_attempt": True, "resource_rollback": False, "attempt": attempt}

    def test_original_attempt_role_survives_switch_stop_and_reconnect(self):
        first = self.client(); first.connect(timeout=0.2)
        state = self.state(first, stopped=True)
        state["campaign"] = self.campaign(); state["campaign"]["character_id"] = "777"
        state["campaign"]["attempt"] = self.pending_business()["attempt"]
        self.assertTrue(bridge._campaign_valid(state["campaign"]))
        state["business"] = self.pending_business()
        first._finish_stopped(state)
        guard = bridge._read_json(first.business_guard_path)
        self.assertEqual(guard["character_id"], "18446744073709551615")
        self.assertEqual(guard["business"]["attempt"]["character_id"], "18446744073709551615")
        second = self.client(); second.connect(timeout=0.2); self.endpoint(second)
        second.campaign_observe()  # The original A role is active again.
        wire_before = (second.session_dir / "command.txt").read_bytes()
        with self.assertRaises(bridge.BridgeError):
            second.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
        self.assertEqual(second._seq, 1)
        second._finish_stopped(self.state(second, seq=1, stopped=True))
        self.assertTrue(second.business_guard_path.exists())
        third = self.client(); third.connect(timeout=0.2); self.endpoint(third); third.campaign_observe()
        with self.assertRaises(bridge.BridgeError):
            third.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="c" * 32)
        self.assertEqual(third._seq, 1)
        self.assertEqual((second.session_dir / "command.txt").read_bytes(), wire_before)
        self.assertFalse(second.is_unknown)

    def test_ledger_recovery_uses_attempt_role_instead_of_current_snapshot(self):
        first = self.client(); first.connect(timeout=0.2)
        state = self.state(first, stopped=True); state["campaign"] = self.campaign()
        state["campaign"]["character_id"] = "777"; state["business"] = self.pending_business()
        bridge._atomic_json(first.session_dir / "state.json", state)
        first._write_ledger("stopped", component_destroyed=True, no_action=False, business=state["business"], campaign=state["campaign"])
        self.assertFalse(first.business_guard_path.exists())
        second = self.client(); second.connect(timeout=0.2); self.endpoint(second); second.campaign_observe()
        self.assertEqual(second._prior_business["character_id"], "18446744073709551615")
        with self.assertRaises(bridge.BridgeError):
            second.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
        self.assertEqual(second._seq, 1)

    def test_missing_or_conflicting_attempt_attribution_fails_closed(self):
        for damage in ("missing", "conflicting"):
            with self.subTest(damage=damage):
                client = self.client(); client.connect(timeout=0.2); self.endpoint(client); client.campaign_observe()
                business = self.pending_business("777")
                if damage == "missing": business["attempt"].pop("character_id")
                else: business["character_id"] = "888"
                client._prior_business = {"character_id": "777", "business": business}
                with self.assertRaises(bridge.BridgeError):
                    client.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
                self.assertEqual(client._seq, 1)
                # A clean helper remains stoppable; business uncertainty is not
                # treated as failed restoration of the verification transaction.
                client._finish_stopped(self.state(client, seq=1, stopped=True))
                self.assertTrue(client.terminal)

    def test_readonly_cleanup_keeps_an_unchanged_guard_byte_for_byte(self):
        client = self.client(); client.connect(timeout=0.2)
        guard = {'protocol': 3, 'pid': self.identity.pid,
                 'process_creation_filetime': self.identity.creation_time,
                 'session_id': 'b' * 32, 'character_id': '777',
                 'business': self.pending_business('777'), 'prior_unresolved_business': None,
                 'retained_at': '2020-01-01T00:00:00Z'}
        bridge._atomic_json(client.business_guard_path, guard)
        client._prior_business = bridge._merge_pending_business(guard)
        before = client.business_guard_path.read_bytes()
        client._finish_stopped(self.state(client, stopped=True))
        self.assertEqual(client.business_guard_path.read_bytes(), before)

    def test_pending_attempt_parser_requires_frozen_role_but_allows_current_role_to_change(self):
        snap = self.campaign(); snap["character_id"] = "777"; snap["attempt"] = self.pending_business()["attempt"]
        self.assertTrue(bridge._campaign_valid(snap))
        snap["attempt"].pop("character_id")
        self.assertFalse(bridge._campaign_valid(snap))

    def test_public_generic_business_request_never_bypasses_typed_admission(self):
        client = self.client(); client.connect(timeout=0.2); self.endpoint(client); client.campaign_observe()
        client._prior_business = {"character_id": "18446744073709551615", "business": self.pending_business()}
        fields = dict(action="start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
        previous = (client.session_dir / "command.txt").read_bytes()
        for invoke in (lambda: client.request("campaign-command", _campaign_fields=fields),
                       lambda: client._request("campaign-command", _campaign_fields=fields),
                       lambda: client._send("campaign-command", campaign_fields=fields)):
            with self.assertRaises(bridge.BridgeError): invoke()
            self.assertEqual(client._seq, 1)
            self.assertEqual((client.session_dir / "command.txt").read_bytes(), previous)
        self.assertFalse(client.is_unknown)

    def test_send_boundary_validates_exact_fields_before_sequence_or_publication(self):
        client = self.client(); client.connect(timeout=0.2)
        fields = dict(action="start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
        invalid = [dict(fields, expected_revision=True), dict(fields, method="Send"), dict(fields, troop_id=256), dict(fields, action="eval")]
        missing = dict(fields); missing.pop("attempt_id"); invalid.append(missing)
        for damaged in invalid:
            with self.subTest(fields=damaged), self.assertRaises(bridge.BridgeError):
                client._send("campaign-command", campaign_fields=damaged)
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / "command.txt").exists())
        self.assertFalse(client.is_unknown)

    def test_typed_validation_is_inside_request_lock_and_rechecked_at_send(self):
        client = self.client(); client.connect(timeout=0.2); self.endpoint(client)
        original = bridge._validate_campaign_command_fields
        locked = []
        def validate(fields):
            locked.append(client._request_lock.locked())
            return original(fields)
        with patch.object(bridge, "_validate_campaign_command_fields", side_effect=validate):
            client.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b" * 32)
        self.assertGreaterEqual(len(locked), 2)
        self.assertTrue(all(locked))

    def pending_records(self, value):
        records = []
        while value is not None:
            records.append(value)
            value = value.get("prior_unresolved_business")
            self.assertLess(len(records), 65)
        return records

    def test_existing_guard_merges_newer_crashed_session_before_reclaim_and_readonly_stop(self):
        first = self.client(); first.root.mkdir(parents=True); first.lease_path.parent.mkdir()
        old_business = self.pending_business("777"); old_business["attempt"]["attempt_id"] = "b" * 32
        old_guard = {"protocol": 3, "pid": self.identity.pid, "process_creation_filetime": self.identity.creation_time,
                     "session_id": "b" * 32, "character_id": "777", "business": old_business}
        bridge._atomic_json(first.business_guard_path, old_guard)
        first.connect(timeout=0.2)
        # A legacy owner already submitted A despite the older B guard, then
        # crashed before _finish_stopped. Its helper's final clean receipt is
        # newer than the still-existing B guard; its physical lease is retained.
        state = self.state(first, seq=1, stopped=True); state["campaign"] = self.campaign()
        state["business"] = self.pending_business()
        first._seq = 1
        bridge._atomic_json(first.session_dir / "state.json", state)
        first._write_ledger("stopped", component_destroyed=True, no_action=False, business=state["business"], campaign=state["campaign"])
        self.assertEqual(bridge._read_json(first.business_guard_path)["character_id"], "777")
        second = self.client(); second.connect(timeout=0.2); self.endpoint(second); second.campaign_observe()
        records = self.pending_records(second._prior_business)
        self.assertEqual(len(records), 2)
        self.assertEqual({r["business"]["attempt"]["character_id"] for r in records}, {"777", "18446744073709551615"})
        previous = (second.session_dir / "command.txt").read_bytes()
        with self.assertRaises(bridge.BridgeError):
            second.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="c" * 32)
        self.assertEqual(second._seq, 1); self.assertEqual((second.session_dir / "command.txt").read_bytes(), previous)
        self.assertFalse(second.is_unknown)
        second._finish_stopped(self.state(second, seq=1, stopped=True))
        self.assertEqual(len(self.pending_records(bridge._read_json(second.business_guard_path))), 2)
        third = self.client(); third.connect(timeout=0.2); self.endpoint(third)
        third.campaign_observe(); third.heartbeat()
        with self.assertRaises(bridge.BridgeError):
            third.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="d" * 32)
        self.assertEqual(third._seq, 2)
        bridge._atomic_json(third.session_dir / "state.json", self.state(third, seq=2, stopped=True))
        third.stop()
        self.assertTrue(third.terminal)
        self.assertEqual({r["business"]["attempt"]["character_id"] for r in self.pending_records(bridge._read_json(third.business_guard_path))},
                         {"777", "18446744073709551615"})

    def test_pending_merge_retains_conflicts_and_deduplicates_repeated_recovery(self):
        a = {"session_id": "a" * 32, "business": self.pending_business(), "retained_at": "old"}
        b = {"session_id": "a" * 32, "business": self.pending_business("777"), "retained_at": "new"}
        merged = bridge._merge_pending_business(a, b)
        for _ in range(12):
            merged = bridge._merge_pending_business(merged, dict(a, retained_at="another-read"), b)
        records = self.pending_records(merged)
        self.assertEqual(len(records), 2)
        self.assertEqual({r["character_id"] for r in records}, {"777", "18446744073709551615"})
        self.assertEqual(a["business"]["attempt"]["character_id"], "18446744073709551615")
        self.assertIsNone(bridge._merge_pending_business({"business": {"pending_attempt": False}}))

    def test_guard_for_another_process_identity_is_not_loaded(self):
        client = self.client(); client.root.mkdir(parents=True); client.lease_path.parent.mkdir()
        other_path = client.lease_path.parent / f"999-{self.identity.creation_time}.business.json"
        bridge._atomic_json(other_path, {"protocol": 3, "pid": 999, "process_creation_filetime": self.identity.creation_time,
                                        "business": self.pending_business()})
        client.connect(timeout=0.2)
        self.assertIsNone(client._prior_business)
        self.endpoint(client)
        self.assertEqual(client.campaign_command("select", expected_revision=3, campaign_id=12, troop_id=7)["ack"]["status"], "COMPLETED")

    def test_legacy_next_manifest_and_ready_state_cannot_claim_gui_skip_interface(self):
        client = self.client(); manifest = dict(self.manifest); manifest.pop("campaign_interface")
        bridge._atomic_json(client.assets / "manifest.json", manifest)
        with self.assertRaises(bridge.BridgeError): client.connect(timeout=0.1)
        client._popen.assert_not_called(); self.assertFalse(client.lease_path.exists())
        bridge._atomic_json(client.assets / "manifest.json", self.manifest)
        stale = self.client()
        def spawn(*args, **kwargs):
            state = self.state(stale); state["campaign"] = {"schema": 1}
            bridge._atomic_json(stale.session_dir / "state.json", state)
            return SimpleNamespace(pid=987, stdout=iter(()))
        stale._popen = Mock(side_effect=spawn)
        with self.assertRaises(bridge.BridgeUnknown): stale.connect(timeout=0.1)
        self.assertTrue(stale.lease_path.exists())

    def test_legacy_clean_receipt_without_marker_still_inherits_pending_business(self):
        client = self.client(); client.root.mkdir(parents=True); client.lease_path.parent.mkdir()
        old = "e" * 32; directory = client.root / old; directory.mkdir()
        business = self.pending_business()
        state = self.state(client, seq=1, stopped=True)
        state.update(nonce=old, session_id=old, business=business, campaign=self.campaign())
        state["campaign"].pop("interface")
        bridge._atomic_json(directory / "state.json", state)
        bridge._atomic_json(directory / "session.json", {"protocol": 3, "session_id": old, **self.identity.as_dict(), "bootstrap_started": True,
                                                         "state": "stopped", "component_destroyed": True, "no_action": False, "business": business})
        bridge._atomic_json(client.lease_path, {"session_id": old})
        client.connect(timeout=0.2); self.endpoint(client); client.campaign_observe()
        self.assertEqual(client._prior_business["business"]["attempt"]["character_id"], "18446744073709551615")
        with self.assertRaises(bridge.BridgeError):
            client.campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="a" * 32)

    def test_bounded_context_diagnostics_are_accepted_by_real_parser(self):
        value = self.campaign()
        context = {"current_root_instance_id": 321, "current_root_level": "Campaign", "current_root_active": True,
                   "global_input_enabled": False, "selection_gate": "PASS", "counts": {"campaign": {"total": 4, "visible": 0, "matching": 1}},
                   "campaign_candidates": [{"instance_id": 123, "visible": False, "owner_matches_current": True,
                                              "hierarchy_matches_current": True, "owner_root_level": "Campaign"}], "candidates_truncated": False}
        value["ui_context"] = context
        self.assertTrue(bridge._campaign_valid(value))
        for bad in (dict(context, campaign_candidates=context["campaign_candidates"] * 9), dict(context, current_root_instance_id=True),
                    dict(context, player_name="private"), dict(context, counts={"campaign": {"total": 513, "visible": 0, "matching": 1}})):
            value["ui_context"] = bad
            self.assertFalse(bridge._campaign_valid(value))

    def selection_diagnostics(self):
        return {"character_campaign_id": 57, "character_level": 12, "campaign": {
            "type": 0, "enabled": True, "unlocked": True, "finished": False, "m_bDebug": False,
            "m_bPostInit": False, "m_bBorning": False, "m_bBornWant": False, "m_bScenarioVisible": False,
            "m_bSignActiving": False}, "troops": [{
                "troop_id": 3, "idcampaign": 57, "idtroop": 3, "chanllege_level": 6, "click_mode": 0,
                "limit_count": -1, "finish_count": 0, "finish_max": 1, "isNewbie": 1,
                "Newbie_Skill": 249, "Newbie_formation": 2**32 - 1, "mesh_script_count": 1,
                "m_scenarioClickF": 11, "m_scenarioVisible": 12, "m_scenarioAttackF": 13,
                "m_scenarioVictoryF": 14, "m_scenarioFailure": 15, "m_scenarioSystem": 2**32 - 1,
                "visible": True, "canattack": True, "killed": False, "clicked": False, "show_arrow": False,
                "mesh_present": True, "mesh_active": True, "mesh_root_matches": True,
                "mesh_script_enabled": True, "mesh_script_owner_matches": True, "select_gate": "PASS"}],
            "truncated": False}

    def test_fixed_selection_diagnostics_accept_signed_count_and_uint_template_ids(self):
        value = self.campaign()
        for count in (-32768, -2, -1, 0, 32767):
            diagnostic = self.selection_diagnostics()
            diagnostic["troops"][0]["limit_count"] = count
            value["selection_diagnostics"] = diagnostic
            self.assertTrue(bridge._campaign_valid(value))
        value["selection_diagnostics"] = dict(self.selection_diagnostics(), campaign=None, troops=[])
        self.assertTrue(bridge._campaign_valid(value))

    def test_selection_diagnostics_reject_unbounded_unknown_or_wrong_typed_fields(self):
        good = self.selection_diagnostics()
        mutations = [
            lambda d: d.update(character_campaign_id=True),
            lambda d: d.update(character_campaign_id=65536),
            lambda d: d.update(troops=d["troops"] * 9),
            lambda d: d.update(troops=d["troops"] * 2),
            lambda d: d.update(arbitrary_path="private"),
            lambda d: d["campaign"].update(player_name="private"),
            lambda d: d["campaign"].update(enabled=1),
            lambda d: d["troops"][0].update(limit_count=-32769),
            lambda d: d["troops"][0].update(limit_count=32768),
            lambda d: d["troops"][0].update(click_mode=256),
            lambda d: d["troops"][0].update(m_scenarioVictoryF=2**32),
            lambda d: d["troops"][0].update(mesh_script_count=513),
            lambda d: d["troops"][0].update(mesh_active=1),
            lambda d: d["troops"][0].update(select_gate="invoke_anything"),
            lambda d: d["troops"][0].pop("Newbie_Skill"),
        ]
        for mutate in mutations:
            diagnostic = deepcopy(good)
            mutate(diagnostic)
            value = self.campaign()
            value["selection_diagnostics"] = diagnostic
            self.assertFalse(bridge._campaign_valid(value))

    def test_selection_diagnostics_round_trip_without_authorizing_an_action(self):
        client = self.client(); client.connect(timeout=0.2)
        def diagnostic(state, ack):
            state["campaign"]["selection_diagnostics"] = self.selection_diagnostics()
        self.endpoint(client, corrupt=diagnostic)
        result = client.campaign_observe()
        self.assertEqual(result["ack"]["result"]["campaign"]["selection_diagnostics"], self.selection_diagnostics())
        self.assertEqual(client._send.call_count, 1)
        self.assertEqual(client._seq, 1)
        self.assertIn("command=campaign-observe", (client.session_dir / "command.txt").read_text())
        self.assertFalse(result["state"]["business"]["mutation_attempted"])

    def settlement_diagnostics(self):
        return {"anchor_scope": "this_helper_instance", "character_id": "18446744073709551615",
                "observer": {"status": "NONE", "anchors_available": False, **{key: None for key in
                    ("zero_observed", "battle_observed", "receipt_captured", "campaign_match", "battle_match",
                     "character_match", "character_id_match", "report_token_match")}},
                "battle": {"instance_id": -123, "parent_instance_id": 124, "owner_root_instance_id": 125,
                    "previous_root_instance_id": 126, "visible": False, "parent_matches_campaign": True,
                    "parent_owned_by_current": True, "previous_root_matches_current": True, "owner_root_level": "Battle",
                    "idcampaign": 57, "idtroop": 3, "report_id": "18446744073709551615", "view_type": 3,
                    "replay": False, "cross": False, "surrender": False, "waiting": False, "use_end": False,
                    "fighting": False, "playback_remaining": -0.125, "pending_present": True,
                    "packet": {"result": 2**32 - 1, "type": 2**32 - 1, "idcampaign": 2**32 - 1,
                               "idtroop": 2**32 - 1, "iswin": False, "report_id": "0"}},
                "result": {"instance_id": 456, "report_id": "123", "battle_match": True, "root_match": True, "battle_view": False},
                "target": {"idcampaign": 57, "idtroop": 3, "finish_count": 1, "finish_max": 1,
                           "killed": True, "canattack": False, "campaign_finished": True,
                           "data_present": True, "matches_battle_target": True, "data_anchor_match": None}}

    def test_settlement_diagnostics_fixed_nullable_raw_evidence(self):
        value = self.campaign()
        good = self.settlement_diagnostics()
        value["settlement_diagnostics"] = good
        self.assertTrue(bridge._campaign_valid(value))
        self.assertEqual(value["attempt"]["outcome"], "UNKNOWN")
        for remaining in (None, -3.4028235e38, 0, 3.4028235e38):
            good["battle"]["playback_remaining"] = remaining
            self.assertTrue(bridge._campaign_valid(value))
        value["settlement_diagnostics"] = dict(good, battle=None, result=None, target=None)
        self.assertTrue(bridge._campaign_valid(value))
        good["observer"].update(status="UNKNOWN", anchors_available=True, zero_observed=True,
                                battle_observed=True, receipt_captured=False, character_match=False)
        value["settlement_diagnostics"] = good
        self.assertTrue(bridge._campaign_valid(value))

    def test_settlement_diagnostics_strict_negatives(self):
        mutations = [
            lambda d: d.update(arbitrary_path="x"), lambda d: d.update(anchor_scope="old_helper"),
            lambda d: d.update(character_id="18446744073709551616"), lambda d: d.pop("target"),
            lambda d: d["observer"].update(zero_observed=False),
            lambda d: d["observer"].update(anchors_available=True),
            lambda d: d["observer"].update(status=[]),
            lambda d: d["observer"].update(receipt_captured=1),
            lambda d: d["battle"].update(report_id="01"),
            lambda d: d["battle"].update(report_id="18446744073709551616"),
            lambda d: d["battle"].update(instance_id=True),
            lambda d: d["battle"].update(playback_remaining=True),
            lambda d: d["battle"].update(playback_remaining=float("nan")),
            lambda d: d["battle"].update(playback_remaining=float("inf")),
            lambda d: d["battle"].update(owner_root_level=[]),
            lambda d: d["battle"].update(pending_present=False),
            lambda d: d["battle"]["packet"].update(type=2**32),
            lambda d: d["battle"]["packet"].update(result=-1),
            lambda d: d["battle"]["packet"].update(iswin=1),
            lambda d: d["result"].update(report_id=123),
            lambda d: d["target"].update(finish_count=256),
            lambda d: d["target"].update(campaign_finished=1),
            lambda d: d["target"].update(data_anchor_match=True),
        ]
        for mutate in mutations:
            diagnostic = self.settlement_diagnostics(); mutate(diagnostic)
            value = self.campaign(); value["settlement_diagnostics"] = diagnostic
            self.assertFalse(bridge._campaign_valid(value), diagnostic)
        value["settlement_diagnostics"] = None
        self.assertFalse(bridge._campaign_valid(value))

    def test_settlement_diagnostics_round_trip_does_not_start_or_settle(self):
        client = self.client(); client.connect(timeout=0.2)
        def diagnostic(state, ack):
            state["campaign"]["settlement_diagnostics"] = self.settlement_diagnostics()
        self.endpoint(client, corrupt=diagnostic)
        result = client.campaign_observe()
        self.assertEqual(result["ack"]["result"]["campaign"]["settlement_diagnostics"], self.settlement_diagnostics())
        self.assertEqual(result["ack"]["result"]["campaign"]["attempt"]["outcome"], "UNKNOWN")
        self.assertEqual(client._send.call_count, 1)
        self.assertFalse(result["state"]["business"]["mutation_attempted"])

    def test_accepted_gui_queue_does_not_complete_without_final_ack(self):
        client = self.client(); client.connect(timeout=0.2); self.endpoint(client, status="ACCEPTED")
        with self.assertRaises(bridge.BridgeUnknown):
            client.campaign_command("continue", expected_revision=3, campaign_id=12, troop_id=7, timeout=0.02)
        self.assertEqual(client._send.call_count, 1); self.assertEqual(client._seq, 1)
        self.assertTrue(client.lease_path.exists())


class JsonReadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="js-owned-json-read-test-")
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "ack.json"
        self.data = b'{"complete":true}'
        self.path.write_bytes(self.data)

    def contention(self, code, label="own read contention"):
        error = PermissionError(errno.EACCES, label, str(self.path))
        error.winerror = code
        return error

    def test_open_contention_retries_only_same_path_then_succeeds(self):
        original = bridge._open_shared_read
        for code in (32, 33):
            with self.subTest(winerror=code):
                error = self.contention(code)
                paths = []
                @contextmanager
                def read(path):
                    paths.append(path)
                    if len(paths) < 3:
                        raise error
                    with original(path) as stream:
                        yield stream
                with patch.object(bridge.os, "name", "nt"), \
                        patch.object(bridge, "_open_shared_read", side_effect=read):
                    self.assertEqual(bridge._read_json(self.path), {"complete": True})
                self.assertEqual(paths, [self.path] * 3)
                self.assertEqual(self.path.read_bytes(), self.data)

    def test_read_contention_closes_failed_handle_before_reopening(self):
        for code in (32, 33):
            with self.subTest(winerror=code):
                error = self.contention(code)
                events = []
                reads = []
                @contextmanager
                def read(path):
                    number = len(reads) + 1
                    events.append(("open", number, path))
                    stream = Mock()
                    def read_bytes(size):
                        reads.append(size)
                        if number < 3:
                            raise error
                        return self.data
                    stream.read.side_effect = read_bytes
                    try:
                        yield stream
                    finally:
                        events.append(("close", number, path))
                with patch.object(bridge.os, "name", "nt"), \
                        patch.object(bridge, "_open_shared_read", side_effect=read):
                    self.assertEqual(bridge._read_json(self.path), {"complete": True})
                self.assertEqual(reads, [bridge._MAX_JSON + 1] * 3)
                self.assertEqual(events, [(kind, number, self.path) for number in (1, 2, 3)
                                          for kind in ("open", "close")])

    def test_read_retry_deadline_preserves_first_error_fields(self):
        first = self.contention(32, "own original read failure")
        later = self.contention(33, "own subsequent read failure")
        clock = [100.0]
        sleeps = []
        paths = []
        def open_read(path):
            paths.append(path)
            raise first if len(paths) == 1 else later
        def sleep(seconds):
            sleeps.append(seconds)
            clock[0] += seconds
        with patch.object(bridge.os, "name", "nt"), \
                patch.object(bridge.time, "monotonic", side_effect=lambda: clock[0]), \
                patch.object(bridge.time, "sleep", side_effect=sleep), \
                patch.object(bridge, "_open_shared_read", side_effect=open_read):
            with self.assertRaises(PermissionError) as caught:
                bridge._read_json(self.path)
        self.assertIs(caught.exception, first)
        self.assertEqual((first.errno, first.winerror, first.filename), (errno.EACCES, 32, str(self.path)))
        self.assertEqual(first.read_stage, "open")
        self.assertEqual(first.read_attempts, len(paths))
        self.assertGreater(len(paths), 1)
        self.assertLessEqual(len(paths), 27)
        self.assertAlmostEqual(first.read_elapsed_ms, 250, places=3)
        self.assertAlmostEqual(sum(sleeps), bridge._READ_RETRY_TIMEOUT, places=6)
        self.assertTrue(all(0 < delay <= 0.01 for delay in sleeps))

    def test_read_stage_deadline_preserves_first_error_and_closes_every_handle(self):
        first = self.contention(33)
        clock = [10.0]
        events = []
        @contextmanager
        def read(path):
            events.append("open")
            stream = Mock()
            stream.read.side_effect = first
            try:
                yield stream
            finally:
                events.append("close")
        def sleep(seconds):
            clock[0] += seconds
        with patch.object(bridge.os, "name", "nt"), \
                patch.object(bridge.time, "monotonic", side_effect=lambda: clock[0]), \
                patch.object(bridge.time, "sleep", side_effect=sleep), \
                patch.object(bridge, "_open_shared_read", side_effect=read):
            with self.assertRaises(PermissionError) as caught:
                bridge._read_json(self.path)
        self.assertIs(caught.exception, first)
        self.assertEqual(first.read_stage, "read")
        self.assertEqual(events, [kind for _ in range(first.read_attempts) for kind in ("open", "close")])
        self.assertAlmostEqual(first.read_elapsed_ms, 250, places=3)

    def test_noncontention_errors_never_retry_at_open_or_read(self):
        for code in (None, 5, 2, 87):
            for stage in ("open", "read"):
                with self.subTest(winerror=code, stage=stage):
                    error = self.contention(code)
                    stream = Mock()
                    stream.read.side_effect = error
                    @contextmanager
                    def read(path):
                        if stage == "open":
                            raise error
                        yield stream
                    with patch.object(bridge.os, "name", "nt"), patch.object(bridge.time, "sleep") as sleep, \
                            patch.object(bridge, "_open_shared_read", side_effect=read) as opened:
                        with self.assertRaises(PermissionError) as caught:
                            bridge._read_json(self.path)
                    self.assertIs(caught.exception, error)
                    self.assertEqual(opened.call_count, 1)
                    sleep.assert_not_called()

    def test_recognized_error_on_nonwindows_does_not_retry(self):
        for code in (32, 33):
            error = self.contention(code)
            with patch.object(bridge.os, "name", "posix"), patch.object(bridge.time, "sleep") as sleep, \
                    patch.object(bridge, "_open_shared_read", side_effect=error) as opened:
                with self.assertRaises(PermissionError) as caught:
                    bridge._read_json(self.path)
            self.assertIs(caught.exception, error)
            self.assertEqual(opened.call_count, 1)
            sleep.assert_not_called()

    def test_close_failure_is_not_retried(self):
        error = self.contention(32)
        @contextmanager
        def read(path):
            stream = Mock()
            stream.read.return_value = self.data
            yield stream
            raise error
        with patch.object(bridge.os, "name", "nt"), patch.object(bridge.time, "sleep") as sleep, \
                patch.object(bridge, "_open_shared_read", side_effect=read) as opened:
            with self.assertRaises(PermissionError) as caught:
                bridge._read_json(self.path)
        self.assertIs(caught.exception, error)
        self.assertEqual(opened.call_count, 1)
        sleep.assert_not_called()

    def test_json_parse_type_size_and_decode_failures_never_retry(self):
        original = bridge._open_shared_read
        for data in (b"bad-json", b"[]", b"\xff", b"x" * (bridge._MAX_JSON + 1)):
            with self.subTest(data_length=len(data)):
                self.path.write_bytes(data)
                with patch.object(bridge.time, "sleep") as sleep, \
                        patch.object(bridge, "_open_shared_read", wraps=original) as opened:
                    with self.assertRaises(bridge.BridgeUnknown):
                        bridge._read_json(self.path)
                self.assertEqual(opened.call_count, 1)
                sleep.assert_not_called()

    def hold_exclusive(self):
        import ctypes
        api = AtomicFileTests.kernel()
        handle = api.CreateFileW(str(self.path), 0x80000000, 0, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            self.fail("Unable to hold own JSON fixture: " + str(ctypes.get_last_error()))
        return api, handle

    @unittest.skipUnless(os.name == "nt", "Windows share semantics")
    def test_real_short_win32_exclusive_hold_recovers_same_read(self):
        api, handle = self.hold_exclusive()
        released = threading.Event()
        closed = []
        def release():
            time.sleep(0.06)
            closed.append(bool(api.CloseHandle(handle)))
            released.set()
        original = bridge._open_shared_read
        attempts = []
        failures = []
        @contextmanager
        def read(path):
            attempts.append(path)
            try:
                with original(path) as stream:
                    yield stream
            except OSError as error:
                failures.append(error)
                raise
        worker = threading.Thread(target=release)
        worker.start()
        started = time.monotonic()
        try:
            with patch.object(bridge, "_open_shared_read", side_effect=read):
                self.assertEqual(bridge._read_json(self.path), {"complete": True})
        finally:
            worker.join(1.0)
            if not released.is_set():
                api.CloseHandle(handle)
        elapsed = time.monotonic() - started
        self.assertEqual(closed, [True])
        self.assertGreater(len(attempts), 1)
        self.assertTrue(all(path == self.path for path in attempts))
        self.assertIn(failures[0].winerror, (32, 33))
        self.assertGreaterEqual(elapsed, 0.04)
        self.assertLess(elapsed, 0.6)
        self.assertEqual(self.path.read_bytes(), self.data)

    @unittest.skipUnless(os.name == "nt", "Windows share semantics")
    def test_real_long_win32_exclusive_hold_is_bounded_and_preserves_first_error(self):
        api, handle = self.hold_exclusive()
        original = bridge._open_shared_read
        failures = []
        @contextmanager
        def read(path):
            try:
                with original(path) as stream:
                    yield stream
            except OSError as error:
                failures.append(error)
                raise
        started = time.monotonic()
        try:
            with patch.object(bridge, "_open_shared_read", side_effect=read):
                with self.assertRaises(OSError) as caught:
                    bridge._read_json(self.path)
            elapsed = time.monotonic() - started
            self.assertIs(caught.exception, failures[0])
            self.assertIn(caught.exception.winerror, (32, 33))
            self.assertEqual(caught.exception.errno, errno.EACCES)
            self.assertEqual(caught.exception.filename, str(self.path))
            self.assertEqual(caught.exception.read_stage, "open")
            self.assertEqual(caught.exception.read_attempts, len(failures))
            self.assertGreater(len(failures), 1)
            self.assertGreaterEqual(caught.exception.read_elapsed_ms, 240)
            self.assertGreaterEqual(elapsed, 0.24)
            self.assertLess(elapsed, 0.7)
        finally:
            api.CloseHandle(handle)
        self.assertEqual(bridge._read_json(self.path), {"complete": True})
        self.assertEqual(self.path.read_bytes(), self.data)


class AtomicFileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="js-owned-io-test-")
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "command.txt"
        self.old = b"sequence=7\ncommand=snapshot\n"
        self.new = b"sequence=8\ncommand=snapshot\n"
        self.path.write_bytes(self.old)

    def test_only_recognized_winerrors_retry_same_temporary_bytes(self):
        for code in (5, 32, 33):
            with self.subTest(winerror=code):
                original = os.replace
                error = PermissionError(errno.EACCES, "own contention fixture")
                error.winerror = code
                calls = []
                def replace(source, target):
                    calls.append((str(source), str(target), Path(source).read_bytes()))
                    if len(calls) < 3:
                        raise error
                    return original(source, target)
                with patch.object(bridge.os, "replace", side_effect=replace):
                    bridge._atomic_bytes(self.path, self.new)
                self.assertEqual(len(calls), 3)
                self.assertEqual(len(set(calls)), 1)
                self.assertEqual(self.path.read_bytes(), self.new)
                self.assertFalse(self.path.with_name("command.txt.tmp").exists())

    def test_plain_permission_and_other_io_errors_are_not_retried(self):
        for error in (PermissionError(errno.EACCES, "ordinary permission fixture"),
                      OSError(errno.EIO, "ordinary disk fixture")):
            with self.subTest(error=type(error).__name__):
                with patch.object(bridge.os, "replace", side_effect=error) as replace:
                    with self.assertRaises(OSError) as caught:
                        bridge._atomic_bytes(self.path, self.new)
                self.assertIs(caught.exception, error)
                self.assertEqual(replace.call_count, 1)

    def test_permanent_winerror5_is_bounded_and_original_error_propagates(self):
        error = PermissionError(errno.EACCES, "permanent own ACL fixture")
        error.winerror = 5
        began = time.monotonic()
        with patch.object(bridge, "_ATOMIC_REPLACE_TIMEOUT", 0.03), \
                patch.object(bridge.os, "replace", side_effect=error) as replace:
            with self.assertRaises(PermissionError) as caught:
                bridge._atomic_bytes(self.path, self.new)
        self.assertIs(caught.exception, error)
        self.assertGreater(replace.call_count, 1)
        self.assertLess(time.monotonic() - began, 0.3)
        self.assertEqual(self.path.read_bytes(), self.old)
        self.assertEqual(self.path.with_name("command.txt.tmp").read_bytes(), self.new)

    def test_fsync_failure_is_not_retried_as_publication(self):
        error = OSError(errno.EIO, "own fsync failure")
        with patch.object(bridge.os, "fsync", side_effect=error), patch.object(bridge.os, "replace") as replace:
            with self.assertRaises(OSError) as caught:
                bridge._atomic_bytes(self.path, self.new)
        self.assertIs(caught.exception, error)
        replace.assert_not_called()

    @staticmethod
    def kernel():
        import ctypes
        from ctypes import wintypes
        api = ctypes.WinDLL("kernel32", use_last_error=True)
        api.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                   wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        api.CreateFileW.restype = wintypes.HANDLE
        api.CloseHandle.argtypes = [wintypes.HANDLE]
        api.CloseHandle.restype = wintypes.BOOL
        api.MoveFileExW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD]
        api.MoveFileExW.restype = wintypes.BOOL
        api.ReplaceFileW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                    wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p]
        api.ReplaceFileW.restype = wintypes.BOOL
        return api

    def held_without_delete_share(self):
        import ctypes
        api = self.kernel()
        handle = api.CreateFileW(str(self.path), 0x80000000, 3, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            self.fail("Unable to hold own test file: " + str(ctypes.get_last_error()))
        return api, handle

    @unittest.skipUnless(os.name == "nt", "Windows share semantics")
    def test_real_win32_lock_release_allows_same_publication(self):
        api, handle = self.held_without_delete_share()
        released = threading.Event()
        def release():
            time.sleep(0.06)
            api.CloseHandle(handle)
            released.set()
        worker = threading.Thread(target=release)
        worker.start()
        try:
            bridge._atomic_bytes(self.path, self.new)
        finally:
            worker.join(1.0)
            if not released.is_set():
                api.CloseHandle(handle)
        self.assertEqual(self.path.read_bytes(), self.new)
        self.assertFalse(self.path.with_name("command.txt.tmp").exists())

    @unittest.skipUnless(os.name == "nt", "Windows share semantics")
    def test_real_permanent_win32_lock_fails_bounded_without_changing_target(self):
        api, handle = self.held_without_delete_share()
        started = time.monotonic()
        try:
            with patch.object(bridge, "_ATOMIC_REPLACE_TIMEOUT", 0.06):
                with self.assertRaises(OSError) as caught:
                    bridge._atomic_bytes(self.path, self.new)
            self.assertIn(caught.exception.winerror, (5, 32, 33))
            self.assertGreater(caught.exception.publication_attempts, 1)
            self.assertLess(time.monotonic() - started, 0.3)
            self.assertEqual(self.path.read_bytes(), self.old)
            self.assertEqual(self.path.with_name("command.txt.tmp").read_bytes(), self.new)
        finally:
            api.CloseHandle(handle)

    @unittest.skipUnless(os.name == "nt", "Windows share semantics")
    def test_shared_reader_allows_native_atomic_replace_and_keeps_complete_old_bytes(self):
        api = self.kernel()
        incoming = self.path.with_name("incoming.json")
        incoming.write_bytes(self.new)
        with bridge._open_shared_read(self.path) as stream:
            self.assertTrue(api.ReplaceFileW(str(self.path), str(incoming), None, 0, None, None))
            self.assertEqual(stream.read(), self.old)
            self.assertEqual(self.path.read_bytes(), self.new)

    def test_json_reader_missing_invalid_oversized_and_valid(self):
        self.assertIsNone(bridge._read_json(self.path.with_name("missing.json")))
        for data in (b"bad-json", b"[]", b'{"too_long":"' + b"x" * bridge._MAX_JSON + b'"}'):
            self.path.write_bytes(data)
            with self.assertRaises(bridge.BridgeUnknown):
                bridge._read_json(self.path)
        self.path.write_bytes(b'{"complete":true}')
        self.assertEqual(bridge._read_json(self.path), {"complete": True})

    @unittest.skipUnless(os.name == "nt", "Windows CRT handle ownership")
    def test_shared_reader_closes_native_handle_if_descriptor_conversion_fails(self):
        api = SimpleNamespace(CreateFileW=Mock(return_value=1234), CloseHandle=Mock(return_value=True))
        with patch("ctypes.WinDLL", return_value=api), patch("msvcrt.open_osfhandle", side_effect=OSError("own conversion failure")):
            with self.assertRaises(OSError):
                with bridge._open_shared_read(self.path):
                    self.fail("stream must not open")
        api.CloseHandle.assert_called_once_with(1234)

    @unittest.skipUnless(os.name == "nt", "Windows CRT handle ownership")
    def test_shared_reader_closes_only_descriptor_if_file_wrapper_fails(self):
        api = SimpleNamespace(CreateFileW=Mock(return_value=1234), CloseHandle=Mock(return_value=True))
        with patch("ctypes.WinDLL", return_value=api), patch("msvcrt.open_osfhandle", return_value=77), \
                patch.object(bridge.os, "fdopen", side_effect=OSError("own wrapper failure")), \
                patch.object(bridge.os, "close") as close:
            with self.assertRaises(OSError):
                with bridge._open_shared_read(self.path):
                    self.fail("stream must not open")
        close.assert_called_once_with(77)
        api.CloseHandle.assert_not_called()


if __name__ == "__main__":
    unittest.main()
