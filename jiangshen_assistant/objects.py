"""Thread-safe current-game object access; no Tk access from workers."""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
import copy
import hashlib
import json
import queue
import threading
import time
from typing import Callable

from .bridge_client import (BridgeAdmissionClosed, BridgeClient, BridgeError, BridgeUnknown,
                            _campaign_valid, _challenge_valid, _elite_valid, _home_navigation_valid,
                            _home_loss_advice_ready, _home_battle_guide_ready, HOME_NAVIGATION_ACTIONS)
from .mono_runtime import MonoRuntime, ProcessIdentity, Refusal, identity_for, process_status, validate_identity
from .windows import WindowInfo
from .workflow_events import WorkflowEvent, WorkflowEventKind, WorkflowSubscription, workflow_changes
from .event_stream import EventMailbox
from .resource_policy import ResourceAdmission, ResourcePolicyDenied


@dataclass(frozen=True)
class ObjectEvent:
    kind: str
    message: str = ""
    data: dict = field(default_factory=dict)
    level: str = "INFO"


@dataclass(frozen=True)
class CampaignToken:
    run_id: str
    context_key: str
    generation: int
    scope: str = 'campaign'


@dataclass(frozen=True)
class CampaignConnectionToken:
    """Opaque capability for retiring one unclaimed connection attempt."""
    generation: int


@dataclass(frozen=True)
class WorkflowSessionToken:
    """Authority to close one runtime-created client after all owners release."""
    generation: int
    client: object = field(repr=False)


class CampaignAdmissionClosed(BridgeError):
    """No command was admitted; callers may keep their pending ledger."""


class SoulPrepublicationAdmissionClosed(CampaignAdmissionClosed):
    """The soul bridge refused before publication; its resource ticket is undone."""


class ResourcePolicyAdmissionClosed(CampaignAdmissionClosed, ResourcePolicyDenied):
    """A known local resource refusal, with no game command published."""


class ObjectController:
    """Passive state monitor plus explicitly requested, serialized bridge work."""
    def __init__(self, project_root: Path, data_dir: Path, *,
                 reader=None, identity_reader: Callable = identity_for,
                 identity_validator: Callable = validate_identity,
                 client_factory: Callable = BridgeClient,
                 poll_seconds: float = 1.0, close_seconds: float = 18.0) -> None:
        self.project_root = Path(project_root).resolve()
        self.data_dir = Path(data_dir).resolve()
        self.events = EventMailbox('objects')
        self._reader = reader or MonoRuntime()
        self._identity_reader = identity_reader
        self._identity_validator = identity_validator
        self._client_factory = client_factory
        self._poll_seconds = poll_seconds
        self._close_seconds = close_seconds
        self._lock = threading.RLock()
        self._io_lock = threading.RLock()
        self.resource_policy_provider = None
        self._resource_admission = ResourceAdmission()
        self.last_resource_policy_denial = None
        self._prelogin_bridge_active = False
        self._prelogin_unknown = None
        self._campaign_owner: CampaignToken | None = None
        self._campaign_admission = False
        self._campaign_stopping = False
        self._campaign_subscribers: list[queue.Queue] = []
        self._workflow_subscribers: list[WorkflowSubscription] = []
        self._workflow_state: dict = {}
        self._workflow_previous: dict | None = None
        self._workflow_settlements: set[tuple] = set()
        self._workflow_error = ""
        self._workflow_fault: dict = {}
        self._restart_required: dict[tuple[int, int], dict] = {}
        self._connection_recovery_history: list[dict] = []
        self._workflow_sequence = 0
        self._campaign_control_reason = ""
        self._generation = 0
        self._window: WindowInfo | None = None
        self._identity: ProcessIdentity | None = None
        self._client: BridgeClient | None = None
        self._identity_cleanup_client: BridgeClient | None = None
        self._busy = False
        self._closing = False
        self._shutdown = False
        self._bridge_state = "disconnected"
        self._bridge_message = "尚未连接对象调用。"
        self._connection_phase = "idle"
        self._connection_started_at = ""
        self._connection_generation = None
        self._connection_error = ""
        self._connection_stop_reason = ""
        self._connection_refusal = ""
        self._connection_refused_at = ""
        self._connection_token: CampaignConnectionToken | None = None
        self._state_valid = False
        self._passive_player: dict | None = None
        self._passive_player_received = 0.0
        self._passive_player_generation = -1
        self._passive_error = ''
        self._passive_error_generation = -1
        self._last_reply = 0.0
        self._last_heartbeat = 0.0
        self._native_player: dict | None = None
        self._native_player_client: BridgeClient | None = None
        self._native_player_received = 0.0
        self._native_player_unavailable = ""
        self._last_action_phase = ""
        self._operation: threading.Thread | None = None
        self._pending_connection = None
        self._cancel = threading.Event()
        self._wake = threading.Event()
        self._poll_stop = threading.Event()
        self._poll_thread = threading.Thread(target=self._monitor, name="object-state-monitor", daemon=True)
        self._poll_thread.start()

    @property
    def generation(self) -> int:
        with self._lock:
            return self._generation

    @property
    def is_busy(self) -> bool:
        with self._lock:
            return self._busy

    @property
    def is_shutdown(self) -> bool:
        with self._lock:
            return self._shutdown

    @property
    def bridge_state(self) -> str:
        with self._lock:
            return self._bridge_state

    @property
    def connection_status(self) -> dict:
        """Read-only progress; never waits for identity checks or bridge I/O."""
        with self._lock:
            recovery = self._recovery_status_locked()
            return {"state": self._bridge_state, "phase": self._connection_phase,
                    "message": self._bridge_message, "busy": self._busy,
                    "generation": self._generation, "started_generation": self._connection_generation,
                    "started_at": self._connection_started_at,
                    "error": self._connection_error,
                    "stop_reason": self._connection_stop_reason,
                    "first_failure": copy.deepcopy(self._workflow_fault),
                    "recovery": recovery,
                    "business_diagnostics": copy.deepcopy(recovery.get('business_diagnostics') or {}),
                    "recovery_history": copy.deepcopy(self._connection_recovery_history),
                    "last_refusal": self._connection_refusal, "refused_at": self._connection_refused_at,
                    "window_present": self._window is not None, "client_present": self._client is not None,
                    "client_terminal": self._client.terminal if self._client else None,
                    "state_events_enabled": bool(self._client and getattr(self._client, 'state_events_enabled', False)),
                    "cancelled": self._cancel.is_set(), "owner": self._campaign_owner is not None,
                    "prelogin_bridge": self._prelogin_bridge_active,
                    "prelogin_unknown": self._prelogin_unknown is not None,
                    "closing": self._closing, "shutdown": self._shutdown}

    @property
    def connection_preflight(self) -> dict:
        """Fresh passive role evidence for automatic admission; never performs I/O.

        The existing monitor reads the initialized Character before any native
        bridge is loaded. Binding/generation, age and monitor failure all fence
        this evidence; a bridge heartbeat cannot renew it.
        """
        with self._lock:
            identity, value = self._identity, self._passive_player
            age = time.monotonic() - self._passive_player_received
            role = value.get('character_id') if value else None
            ready = bool(identity and value and self._state_valid and
                         self._passive_player_generation == self._generation and
                         0 <= age <= 4.0 and type(role) is str and
                         role.isascii() and role.isdecimal() and int(role) > 0 and
                         value.get('pid') == identity.pid and
                         value.get('process_creation_filetime') == identity.creation_time and
                         not self._closing and not self._shutdown and not self._busy and
                         not self._campaign_owner and not self._prelogin_bridge_active and
                         self._prelogin_unknown is None)
            return {'ready': ready, 'generation': self._generation,
                    'identity': identity.as_dict() if identity else None,
                    'age_seconds': round(age, 3) if value else None,
                    'error': self._passive_error if self._passive_error_generation == self._generation else ''}

    def mark_prelogin_bridge_unknown(self, identity: ProcessIdentity, reason: str) -> None:
        """Fence ordinary access until the unconfirmed target process exits."""
        with self._lock:
            self._prelogin_unknown = identity
            self._state_valid = False
            self._set_bridge("unknown", "进入游戏接口清理未确认：" + str(reason))

    @contextmanager
    def prelogin_bridge_guard(self, identity: ProcessIdentity):
        """Serialize a pre-login bridge with passive scans and ordinary connections."""
        with self._io_lock:
            with self._lock:
                window = self._window
                if (self._prelogin_bridge_active or self._prelogin_unknown is not None or
                        self._closing or self._shutdown or self._busy or
                        self._campaign_owner is not None or self._bridge_state != "disconnected" or
                        (self._client is not None and not self._client.terminal) or
                        window is None or window.hwnd != identity.hwnd or window.process_id != identity.pid or
                        (self._identity is not None and self._identity != identity)):
                    raise BridgeAdmissionClosed("登录前接口尚未获得当前游戏的独占准入。")
                self._prelogin_bridge_active = True
            try:
                yield
            finally:
                with self._lock:
                    self._prelogin_bridge_active = False
                self._wake.set()

    @property
    def can_verify(self) -> bool:
        with self._lock:
            return self._can_verify_locked()

    def _can_verify_locked(self) -> bool:
        if (self._campaign_owner or self._closing or self._busy or not self._state_valid or self._bridge_state != "ready"
                or not self._client or getattr(self._client, "is_unknown", False)
                or time.monotonic() - self._last_reply > 8.0):
            return False
        state = self._client.last_state
        snap = state.get("snapshot")
        return (state.get("lifecycle") == "ready" and state.get("ready") is True
                and state.get("main_thread_verified") is True and state.get("component_destroyed") is False
                and isinstance(snap, dict) and snap.get("safe") is True
                and snap.get("main_enabled") is True and snap.get("gate") == "PASS"
                and type(snap.get("main_instance_id")) is int and snap["main_instance_id"] != 0)

    def _identity_data(self, identity: ProcessIdentity | None = None) -> dict:
        target = identity or self._identity
        return {"generation": self._generation,
                "pid": target.pid if target else (self._window.process_id if self._window else None),
                "process_creation_filetime": target.creation_time if target else None}

    def _emit(self, kind: str, message: str = "", data: dict | None = None,
              level: str = "INFO", *, identity: ProcessIdentity | None = None) -> None:
        self.events.put(ObjectEvent(kind, message, {**self._identity_data(identity), **(data or {})}, level))

    def _record_workflow_fault_locked(self, client: BridgeClient | None, error: Exception | str,
                                      stage: str) -> str:
        """Keep the first cause through subsequent stop/cleanup notifications."""
        if not self._workflow_fault:
            first = getattr(client, "first_unknown", {}) if client else {}
            fields = ("message", "exception_type", "observed_at", "command_seq", "command_name", "stage",
                      "winerror", "errno", "read_attempts", "read_elapsed_ms", "read_stage",
                      'read_first_winerror', 'read_first_errno', 'read_first_stage', 'read_first_message',
                      'read_last_winerror', 'read_last_errno', 'read_last_stage', 'read_last_message',
                      "publication_attempts", "publication_elapsed_ms", "publication_stage")
            details = {key: first[key] for key in fields if key in first}
            details.setdefault("message", str(error))
            details.setdefault("exception_type", type(error).__name__ if isinstance(error, Exception) else "")
            details["stage"] = details.get("stage") or stage
            details.setdefault("observed_at", datetime.now(timezone.utc).isoformat())
            for key in ("winerror", "errno"):
                if getattr(error, key, None) is not None:
                    details.setdefault(key, getattr(error, key))
            self._workflow_fault = details
            self._emit("log", "工作流故障 " + json.dumps(
                {"run_id": self._campaign_owner.run_id if self._campaign_owner else "",
                 "session_id": client.nonce if client else "", "generation": self._generation,
                 **details}, ensure_ascii=False, separators=(",", ":")),
                data={"workflow_notice": "fault"}, level="ERROR")
        self._workflow_error = self._workflow_fault["message"]
        self._connection_error = self._workflow_error
        self._connection_stop_reason = self._workflow_fault["stage"] + ": " + self._workflow_error
        self._campaign_control_reason = self._connection_stop_reason
        return self._workflow_error

    def _recovery_status_locked(self) -> dict:
        identity = self._identity
        client = self._client
        if identity is None and client is not None and self._window is not None and self._window.process_id == client.identity.pid:
            identity = client.identity
        if identity is not None:
            retained = self._restart_required.get((identity.pid, identity.creation_time))
            if retained is not None:
                diagnostics = copy.deepcopy(getattr(client, 'recovery_status', {}).get('business_diagnostics') or {})
                history = diagnostics.get('historical_unknown') or []
                diagnostics['historical_unknown'] = [copy.deepcopy(retained)] + [item for item in history
                    if item.get('session_id') != retained.get('session_id')]
                state = client.last_state if client is not None and client.identity == identity else {}
                if not (self._bridge_state == 'ready' and state.get('lifecycle') == 'ready'
                        and state.get('ready') is True and state.get('main_thread_verified') is True
                        and state.get('component_destroyed') is False
                        and state.get('process_restart_required') is False):
                    # A native failure observed in this exact live process is
                    # still a transport boundary, not merely business history.
                    return {**copy.deepcopy(retained), 'business_diagnostics': diagnostics}
                return {'classification': 'historical_unknown', 'process_restart_required': False,
                        'replay_allowed': False, 'reason': '',
                        'business_diagnostics': diagnostics}
        if client is not None and (identity is None or
                (identity.pid, identity.creation_time) == (client.identity.pid, client.identity.creation_time)):
            state = client.last_state
            launch = state.get('battle_launch') or {}
            if launch.get('unresolved') is True:
                return {'classification': 'pending_mutation', 'process_restart_required': False,
                        'replay_allowed': False, 'reason': launch.get('reason') or 'battle_launch_pending',
                        'battle_launch': copy.deepcopy(launch)}
            if getattr(client, 'is_unknown', False):
                return {'classification': 'observation_lost', 'process_restart_required': False,
                        'replay_allowed': False, 'reason': self._workflow_error}
            proof = getattr(client, 'same_process_recovery', {})
            if proof:
                return {'classification': 'connection_reconciled', 'process_restart_required': False,
                        'replay_allowed': False, 'reason': '', 'same_process_recovery': copy.deepcopy(proof),
                        'old_outcome': 'UNKNOWN', 'old_operation_quarantined': True,
                        'soul_mutations_quarantined': proof['soul_mutations_quarantined']}
            diagnostics = getattr(client, 'recovery_status', {}).get('business_diagnostics')
            if diagnostics:
                return {'classification': ('historical_unknown' if diagnostics.get('historical_unknown')
                                           else 'business_unavailable'), 'process_restart_required': False,
                        'replay_allowed': False, 'business_diagnostics': copy.deepcopy(diagnostics)}
        return {'classification': 'none', 'process_restart_required': False, 'replay_allowed': False}

    def _retain_restart_required_locked(self, client: BridgeClient, evidence: dict) -> bool:
        """Retain a native process boundary, separately from the first transport fault."""
        recovery = evidence.get('recovery') or evidence
        if not (evidence.get('process_restart_required') is True or
                recovery.get('process_restart_required') is True or evidence.get('lifecycle') == 'recovery_unknown'):
            return False
        identity = client.identity
        if (evidence.get('pid', identity.pid) != identity.pid or
                evidence.get('process_creation_filetime', identity.creation_time) != identity.creation_time):
            return False
        key = (identity.pid, identity.creation_time)
        if key not in self._restart_required:
            launch = recovery.get('battle_launch') or evidence.get('battle_launch')
            self._restart_required[key] = {'classification': 'restart_required',
                'process_restart_required': True, 'replay_allowed': False,
                'pid': identity.pid, 'process_creation_filetime': identity.creation_time,
                'session_id': recovery.get('session_id') or evidence.get('session_id') or client.nonce,
                'reason': recovery.get('reason') or evidence.get('reason') or 'native_recovery_unknown',
                'battle_launch': copy.deepcopy(launch), 'first_failure': copy.deepcopy(self._workflow_fault)}
            self._emit('log', self._restart_message_locked(self._restart_required[key]),
                       data={'workflow_notice': 'historical_unknown',
                             'recovery': copy.deepcopy(self._restart_required[key])}, level='ERROR')
        self._campaign_admission = False
        self._campaign_stopping = self._campaign_owner is not None
        return True

    @staticmethod
    def _restart_message_locked(recovery: dict) -> str:
        return ('当前游戏进程的调用接口尚未恢复，需要退出并重新打开游戏；原操作结果未确认，不会重放。'
                ' 原因：' + str(recovery.get('reason') or 'native_recovery_unknown'))

    def _command_event_locked(self, token: CampaignToken, action: str, stage: str,
                              fields: dict, *, wire: dict | None = None,
                              error: Exception | None = None) -> None:
        ack = (wire or {}).get("ack") or {}
        record = {"stage": stage, "action": action, "run_id": token.run_id,
                  'workflow_scope': token.scope,
                  "generation": token.generation,
                  "session_id": self._client.nonce if self._client else "", **fields}
        for key in ("command_seq", "command_name", "status"):
            if key in ack:
                record[key] = ack[key]
        if error is not None:
            record.update(exception_type=type(error).__name__, reason=str(error))
            for field in ('winerror', 'errno', 'read_attempts', 'read_elapsed_ms', 'read_stage',
                          'read_first_winerror', 'read_first_errno', 'read_first_stage', 'read_first_message',
                          'read_last_winerror', 'read_last_errno', 'read_last_stage', 'read_last_message'):
                value = getattr(error, field, None)
                if type(value) in (str, int, float):
                    record[field] = value
        self._emit("log", "工作流命令 " + json.dumps(record, ensure_ascii=False, separators=(",", ":")),
                   data={"workflow_command": True, "workflow_command_info": record}, level="ERROR" if error else "INFO")

    def _discard_events(self) -> None:
        retained = []
        while True:
            try:
                event = self.events.get_nowait()
            except queue.Empty:
                break
            try:
                if event.kind == "log":
                    retained.append(event)
            finally:
                self.events.task_done()
        for event in retained:
            self.events.put_nowait(event)

    def _bridge_event(self) -> None:
        state = self._client.last_state if self._client else {}
        snapshot = state.get("snapshot") or {}
        self._emit("bridge", self._bridge_message,
                   {"state": self._bridge_state, "can_verify": self._can_verify_locked(),
                    "gate": state.get("gate", snapshot.get("gate", "")), "message": self._bridge_message,
                    "main_instance_id": state.get("main_instance_id", snapshot.get("main_instance_id")),
                    "session_id": self._client.nonce if self._client else "",
                    "connection": self.connection_status},
                   "WARNING" if self._bridge_state in ("error", "unknown") else "INFO",
                   identity=self._client.identity if self._client else None)

    def _set_bridge(self, state: str, message: str) -> None:
        self._bridge_state, self._bridge_message = state, message
        self._bridge_event()
        self._publish_workflow_locked()

    def _action_result(self, client: BridgeClient, wire: dict) -> dict:
        action = wire.get("ack", {}).get("result", {}).get("action")
        if not isinstance(action, dict):
            action = (wire.get("state") or {}).get("action")
        if not isinstance(action, dict):
            action = {}
        requested = wire.get("requested_command")
        if requested == "disable-restore" and action.get("command_seq") != wire.get("requested_seq"):
            action = {}
        elif requested == "stop" or (wire.get("command") == "stop" and not requested):
            action = {}
        equality = action.get("resource_equality_status", "UNVERIFIED")
        if equality not in ("PASS", "FAIL", "UNVERIFIED"):
            equality = "UNVERIFIED"
        return {"performed": action.get("mutation_attempted") is True,
                "restored": action.get("restore_verified") is True and action.get("restore_method_invoked") is True,
                "resource_equality_status": equality, "outcome": action.get("outcome", "NONE"),
                "evidence_path": str(client.session_dir), "raw": wire}

    def _advance(self) -> None:
        self._retire_workflow_locked("对象绑定代次已改变。")
        self._generation += 1
        self._discard_events()

    @property
    def has_campaign_owner(self) -> bool:
        with self._lock:
            return self._campaign_owner is not None

    def current_identity(self) -> dict | None:
        """Return only the ready cached process/account identity; perform no I/O."""
        with self._lock:
            client = self._client
            if (self._closing or self._bridge_state != 'ready' or client is None or client.terminal
                    or getattr(client, 'is_unknown', False)):
                return None
            state, identity = client.last_state, client.identity
            if (state.get('ready') is not True or state.get('lifecycle') != 'ready'
                    or state.get('component_destroyed') is not False
                    or state.get('pid') != identity.pid
                    or state.get('process_creation_filetime') != identity.creation_time
                    or state.get('nonce') != client.nonce or state.get('session_id') != client.nonce):
                return None
            campaign = state.get('campaign') or {}
            fields = ('character_id', 'user_id', 'zone_id')
            if any(type(campaign.get(key)) is not str or not campaign[key].isascii()
                    or not campaign[key].isdecimal() or not 0 < int(campaign[key]) <= 2**64 - 1
                    for key in fields):
                return None
            for scope in ('challenge', 'elite'):
                other = state.get(scope)
                if isinstance(other, dict) and any(other.get(key) != campaign[key] for key in fields):
                    return None
            return dict(pid=identity.pid, creation=identity.creation_time, process_creation_filetime=identity.creation_time,
                        hwnd=getattr(identity, 'hwnd', None), generation=self._generation, ready=True,
                        **{key: campaign[key] for key in fields})

    def current_elite_choices(self) -> dict | None:
        """Return only the cached, currently visible game choices; perform no I/O."""
        with self._lock:
            identity = self.current_identity()
            if identity is None or self._client is None:
                return None
            elite = self._client.last_state.get('elite')
            if not _elite_valid(elite):
                return None
            rows = [{'campaign_id': row['campaign_id'], 'star': row['star']}
                    for row in elite['challenge_candidates']
                    if row['unlocked'] and not row['finished'] and row['gate'] == 'PASS']
            return {'phase': elite['phase'], 'gate': elite['gate'], 'identity': identity, 'choices': rows}

    def scene_context(self) -> dict:
        """Detached scheduler facts from the cache; no bridge or process I/O.

        The session fingerprint permits replacement detection without exposing
        the bridge nonce/capability or an owner token's context key.
        """
        from .scene_navigation import scene_context_from_state
        with self._lock:
            client = self._client
            state = self._workflow_state or (client.last_state if client is not None else {})
            identity = self.current_identity()
            if identity is not None and any((state.get('campaign') or {}).get(key) != identity.get(key)
                                            for key in ('character_id', 'user_id', 'zone_id')):
                identity = None
            context = scene_context_from_state(state, identity=identity,
                generation=self._generation, connection=self._bridge_state,
                owner_run_id=self._campaign_owner.run_id if self._campaign_owner else None,
                stopping=self._campaign_stopping or self._closing)
            context['session_binding']['session_fingerprint'] = (
                hashlib.sha256(client.nonce.encode('utf-8')).hexdigest()[:24]
                if client is not None and identity is not None else None)
            return copy.deepcopy(context)

    def _campaign_context_locked(self, state: dict | None = None, *, scope: str | None = None) -> str:
        client = self._client
        if client is None:
            raise BridgeError("征战对象连接不可用。")
        state = client.last_state if state is None else state
        scope = scope or (self._campaign_owner.scope if self._campaign_owner else 'campaign')
        campaign = state.get(scope) or {}
        role = campaign.get("character_id", state.get("character_id"))
        if type(role) is not str or not role.isdecimal() or int(role) == 0:
            raise BridgeError("征战观察缺少角色身份。")
        context = {"pid": client.identity.pid, "creation": client.identity.creation_time,
                   "nonce": client.nonce, "generation": self._generation, "character_id": str(role)}
        for key in ('user_id', 'zone_id'):
            value = campaign.get(key)
            if type(value) is str and value.isascii() and value.isdecimal() and 0 < int(value) <= 2**64 - 1:
                context[key] = value
            elif scope == 'elite':
                raise BridgeError('elite_account_identity_unconfirmed: ' + key)
        return json.dumps(context, sort_keys=True, separators=(",", ":"))

    def acquire_campaign(self, run_id: str, *, scope: str = 'campaign') -> CampaignToken:
        if scope not in ('campaign', 'challenge', 'elite'):
            raise BridgeError('未知的战斗任务范围。')
        with self._lock:
            if (not run_id or self._campaign_owner or self._busy or self._closing or
                    self._bridge_state != "ready" or not self._client or self._client.terminal or
                    getattr(self._client, "is_unknown", False)):
                raise BridgeError("对象连接尚未就绪或已被占用。")
            if scope == 'elite' and not _elite_valid(self._client.last_state.get('elite')):
                raise BridgeError('elite_native_interface_or_observation_unavailable')
            token = CampaignToken(run_id, self._campaign_context_locked(scope=scope), self._generation, scope)
            self._client.open_battle_admission()
            self._campaign_owner = token
            # Ownership has transferred. The startup caller's cancellation
            # capability must never regain authority after this owner releases.
            self._connection_token = None
            self._campaign_admission = True
            self._campaign_stopping = False
            self._workflow_previous = None
            self._workflow_settlements.clear()
            self._workflow_error = ""
            self._workflow_fault = {}
            self._campaign_control_reason = "workflow_start"
            self._bridge_event()
            self._wake.set()
            return token

    def _campaign_check_locked(self, token: CampaignToken) -> None:
        if (token is not self._campaign_owner or token.generation != self._generation or self._closing or
                not self._client or self._client.terminal or self._bridge_state != "ready" or
                getattr(self._client, "is_unknown", False)):
            raise BridgeError("征战令牌或连接已失效。")
        if token.context_key != self._campaign_context_locked():
            self._campaign_admission = False
            self._campaign_stopping = True
            self._retire_workflow_locked("征战角色或会话身份已改变。")
            raise BridgeError("征战角色或会话身份已改变。")

    def campaign_snapshot(self, token: CampaignToken) -> dict:
        with self._lock:
            self._campaign_check_locked(token)
            if token.scope == 'challenge' and not _challenge_valid(self._client.last_state.get('challenge')):
                raise BridgeUnknown('challenge_observation_invalid')
            if token.scope == 'elite' and not _elite_valid(self._client.last_state.get('elite')):
                raise BridgeUnknown('elite_native_interface_or_observation_unavailable')
            return {"context_key": token.context_key,
                    "campaign": copy.deepcopy(self._client.last_state.get(token.scope) or {}),
                    "stopping": self._campaign_stopping}

    def subscribe_campaign(self, token: CampaignToken) -> queue.Queue:
        with self._lock:
            self._campaign_check_locked(token)
            subscriber = queue.Queue(maxsize=4)
            self._campaign_subscribers.append(subscriber)
            subscriber.put_nowait(self.campaign_snapshot(token))
            return subscriber

    def _publish_campaign_locked(self, state: dict | None = None) -> None:
        try:
            value = self.campaign_snapshot(self._campaign_owner)
            if state is not None:
                value["campaign"] = copy.deepcopy(state.get(self._campaign_owner.scope) or {})
        except BridgeError as exc:
            value = {"error": str(exc), "stopping": True}
        for subscriber in self._campaign_subscribers:
            if subscriber.full():
                subscriber.get_nowait()
            subscriber.put_nowait(copy.deepcopy(value))

    def _workflow_snapshot_locked(self, token: CampaignToken) -> dict:
        # Observation remains available during a stop/unknown outcome. Command
        # admission retains the stricter ready/nonterminal checks above.
        if (token is not self._campaign_owner or token.generation != self._generation
                or self._closing or self._client is None):
            raise BridgeError("工作流令牌或连接已失效。")
        state = self._workflow_state or self._client.last_state
        if token.scope == 'challenge' and not _challenge_valid(state.get('challenge')):
            raise BridgeUnknown('challenge_observation_invalid')
        if token.scope == 'elite' and not _elite_valid(state.get('elite')):
            raise BridgeUnknown('elite_native_interface_or_observation_unavailable')
        if token.context_key != self._campaign_context_locked(state):
            self._campaign_admission = False
            self._campaign_stopping = True
            raise BridgeError("工作流角色或会话身份已改变。")
        development = state.get("development") or {}
        if development and development.get("character_id") != json.loads(token.context_key)["character_id"]:
            self._campaign_admission = False
            self._campaign_stopping = True
            raise BridgeError("养成角色与已绑定会话身份不一致。")
        recovery = self._recovery_status_locked()
        current_error = self._workflow_error
        value = {"context_key": token.context_key, "campaign": state.get(token.scope) or {},
                 "workflow_scope": token.scope,
                 "development": development, "state": state, "stopping": self._campaign_stopping,
                 "admission_open": self._campaign_admission,
                 "control_reason": self._campaign_control_reason,
                 "connection": {"state": self._bridge_state, "message": self._bridge_message,
                                "error": current_error,
                                "stop_reason": self._connection_stop_reason,
                                "first_failure": self._workflow_fault,
                                "recovery": recovery}}
        if current_error:
            value["error"] = current_error
        return copy.deepcopy(value)

    def workflow_snapshot(self, token: CampaignToken) -> dict:
        """Return detached validated state without issuing a bridge request."""
        with self._lock:
            return self._workflow_snapshot_locked(token)

    def subscribe_workflow(self, token: CampaignToken,
                           notify: Callable[[], None] | None = None) -> WorkflowSubscription:
        with self._lock:
            value = self._workflow_snapshot_locked(token)
            subscriber = WorkflowSubscription(notify)
            self._workflow_subscribers.append(subscriber)
            if self._workflow_previous is None:
                self._publish_workflow_locked()
                return subscriber
            # Each subscriber receives its own initial snapshot, even if another
            # subscriber has already consumed the owner's latest change. This
            # is a private replay, not a new domain publication or log entry.
            for kind in workflow_changes(None, value, set()):
                subscriber._publish(WorkflowEvent(kind, token.context_key, token.run_id,
                                                  token.generation, value))
            return subscriber

    def _publish_workflow_locked(self) -> None:
        token = self._campaign_owner
        if token is None:
            return
        try:
            value = self._workflow_snapshot_locked(token)
        except BridgeError as exc:
            self._retire_workflow_locked(str(exc))
            return
        kinds = workflow_changes(self._workflow_previous, value, self._workflow_settlements)
        self._workflow_previous = value
        self._workflow_subscribers = [item for item in self._workflow_subscribers if not item.closed]
        events = [self._workflow_event_locked(kind, token, value) for kind in kinds]
        for subscriber in self._workflow_subscribers:
            subscriber._update_latest(value)
            for event in events:
                subscriber._publish(event)

    def _workflow_event_locked(self, kind: WorkflowEventKind, token: CampaignToken,
                               value: dict) -> WorkflowEvent:
        self._workflow_sequence += 1
        event = WorkflowEvent(kind, token.context_key, token.run_id, token.generation,
                              value, self._workflow_sequence)
        # Queue only. Formatting, disk I/O and Tk updates happen in App.log on
        # the UI thread, never while a workflow/subscription lock is held.
        self._emit("log", data={"workflow_event": event},
                   level="ERROR" if self._workflow_fault else "INFO")
        return event

    def _retire_workflow_locked(self, reason: str) -> None:
        token = self._campaign_owner
        event = None
        if token is not None and (self._workflow_previous is not None or self._workflow_subscribers):
            value = copy.deepcopy(self._workflow_previous or {})
            recovery = self._recovery_status_locked()
            current_error = self._workflow_error or reason
            value.update(stopping=True, admission_open=False, control_reason=reason,
                         error=current_error)
            value["connection"] = {"state": "retired", "message": reason,
                "error": current_error, "stop_reason": self._connection_stop_reason or reason,
                "first_failure": copy.deepcopy(self._workflow_fault),
                "recovery": recovery}
            event = self._workflow_event_locked(WorkflowEventKind.CONNECTION_CHANGED, token, value)
        for subscriber in self._workflow_subscribers:
            if event is not None:
                subscriber._publish(event)
            subscriber.close()
        self._workflow_subscribers.clear()
        self._workflow_previous = None
        self._workflow_settlements.clear()

    def observe_campaign(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
            observe = client.elite_observe if token.scope == 'elite' else client.challenge_observe if token.scope == 'challenge' else client.campaign_observe
            wire = observe(timeout=timeout)
            # Fakes and bridge callbacks both use the same public state path.
            self._runtime_state(client, wire.get("state", client.last_state))
            return self.campaign_snapshot(token)

    def observe_home_navigation(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        """Refresh all scene views, retaining the task's original owner and scope."""
        self.observe_campaign(token, timeout=timeout)
        return self.workflow_snapshot(token)

    def observe_scene(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        """Worker-only fresh scene observation retaining this exact owner."""
        return self.observe_home_navigation(token, timeout=timeout)

    def submit_home_navigation(self, token: CampaignToken, action: str, *, scope: str,
                               expected_revision: int, campaign_id: int, timeout: float = 5.0) -> dict:
        if (action not in HOME_NAVIGATION_ACTIONS | {'continue'}
                or scope not in ('challenge', 'elite') and not (scope == 'campaign' and action == 'continue')):
            raise BridgeError('未知的返回主界面动作。')
        fields = dict(scope=scope, expected_revision=expected_revision, campaign_id=campaign_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('返回主界面发令门已关闭。')
                client = self._client
                snap = client.last_state.get(scope) or {}
                navigation = snap.get('home_navigation')
                role = json.loads(token.context_key)['character_id']
                validator = {'campaign': _campaign_valid, 'challenge': _challenge_valid, 'elite': _elite_valid}[scope]
                if (not validator(snap)
                        or not _home_navigation_valid(navigation) or snap.get('character_id') != role):
                    raise BridgeError('返回主界面观察或角色身份不可用。')
                if action == 'continue' and (not (_home_battle_guide_ready(snap, scope) or _home_loss_advice_ready(snap, scope))
                        or snap.get('revision') != expected_revision or snap.get('campaign_id') != campaign_id):
                    raise CampaignAdmissionClosed('home_loss_advice_changed_before_submit')
                launch = self._capture_battle_epoch_locked(client)
                self._busy = True
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.home_navigation_command(action, scope=scope, expected_revision=expected_revision,
                    campaign_id=campaign_id, star=snap.get('elite_star', 0), timeout=timeout, **launch)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def submit_scene_result(self, token: CampaignToken, action: str, *, scope: str,
                            expected_revision: int, campaign_id: int, troop_id: int,
                            root_instance_id: int, result_kind: str, timeout: float = 5.0) -> dict:
        """Close a native-approved ordinary result/advice, without adopting its battle.

        The token owns this request; the actual result scope chooses the normal
        native method. No battle start, reward choice or settlement is created.
        """
        from .home_navigation import result_navigation_candidate, fresh_loss_advice_candidate
        fresh_advice = action == 'continue' and scope == 'campaign'
        if (action not in ('ack_result', 'next_chapter') and not fresh_advice
                or scope not in ('campaign', 'challenge', 'elite')
                or type(root_instance_id) is not int or root_instance_id == 0
                or result_kind not in (('',) if fresh_advice else ('NORMAL', 'REWARD', 'CHAPTER'))):
            raise BridgeError('未知的普通结果退出动作。')
        fields = dict(scope=scope, expected_revision=expected_revision, campaign_id=campaign_id,
                      troop_id=troop_id, root_instance_id=root_instance_id, result_kind=result_kind,
                      attempt_id='')
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('普通结果退出发令门已关闭。')
                client = self._client
                identity = json.loads(token.context_key)
                candidate = (fresh_loss_advice_candidate(client.last_state, identity['character_id'])
                             if fresh_advice else result_navigation_candidate(client.last_state, identity['character_id']))
                if candidate is None or candidate[0] != scope:
                    raise CampaignAdmissionClosed('scene_result_native_capability_changed')
                snap = candidate[1]
                if (snap.get('revision') != expected_revision or snap.get('campaign_id') != campaign_id
                        or snap.get('troop_id') != troop_id or snap.get('result_kind') != result_kind
                        or (not fresh_advice and snap.get('result_navigation') != action)
                        or (snap.get('ui_context') or {}).get('current_root_instance_id') != root_instance_id
                        or any(key in identity and snap.get(key) != identity[key] for key in ('user_id', 'zone_id'))):
                    raise CampaignAdmissionClosed('scene_result_identity_or_revision_changed')
                launch = self._capture_battle_epoch_locked(client)
                self._busy = True
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                command = {'campaign': client.campaign_command, 'challenge': client.challenge_command,
                           'elite': client.elite_command}[scope]
                wire = command(action, expected_revision=expected_revision, campaign_id=campaign_id,
                    troop_id=troop_id, attempt_id='', timeout=timeout,
                    **({'star': snap['elite_star'], 'daily_target': 10} if scope == 'elite' else {}), **launch)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def submit_campaign(self, token: CampaignToken, action: str, *, expected_revision: int,
                        campaign_id: int = 0, troop_id: int = 0, attempt_id: str = "",
                        star: int | None = None, daily_target: int = 10,
                        timeout: float = 5.0) -> dict:
        fields = {"expected_revision": expected_revision, "campaign_id": campaign_id,
                  "troop_id": troop_id, "attempt_id": attempt_id}
        if token.scope == 'elite':
            fields.update(star=star, daily_target=daily_target)
        elif star is not None:
            raise BridgeError('星级参数只适用于星级挑战。')
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed("征战发令门已关闭。")
                # Admission is the linearization point. Pause never holds this
                # lock across I/O; this one admitted request may still finish.
                client = self._client
                launch = self._capture_battle_epoch_locked(client)
                self._busy = True
                self._command_event_locked(token, action, "admitted", fields)
            try:
                command = client.elite_command if token.scope == 'elite' else client.challenge_command if token.scope == 'challenge' else client.campaign_command
                wire = command(action, expected_revision=expected_revision,
                    campaign_id=campaign_id, troop_id=troop_id, attempt_id=attempt_id,
                    timeout=timeout, **({'star': star, 'daily_target': daily_target} if token.scope == 'elite' else {}), **launch)
                self._runtime_state(client, wire.get("state", client.last_state))
                with self._lock:
                    self._command_event_locked(token, action, "reply", fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, "failed", fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def development_snapshot(self, token: CampaignToken) -> dict:
        with self._lock:
            self._campaign_check_locked(token)
            development = self._client.last_state.get("development") or {}
            role = json.loads(token.context_key)["character_id"]
            if development.get("character_id") != role:
                self._campaign_admission = False
                self._campaign_stopping = True
                raise BridgeError("养成角色与已绑定会话身份不一致。")
            return {"context_key": token.context_key, "development": copy.deepcopy(development),
                    "stopping": self._campaign_stopping}

    def observe_development(self, token: CampaignToken, *, fields: dict | None = None,
                            timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
            wire = client.development_observe(fields=fields, timeout=timeout)
            self._runtime_state(client, wire.get("state", client.last_state))
            return self.development_snapshot(token)

    def observe_daily(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        """Read a single daily projection while retaining the workflow owner."""
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
            wire = client.daily_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                observed = (ack.get('result') or {}).get('daily')
                if ack.get('status') != 'COMPLETED' or not isinstance(observed, dict):
                    raise BridgeError('日常状态读取未完成。')
                if observed.get('available') and observed.get('character_id') != json.loads(token.context_key)['character_id']:
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('日常观察角色与已绑定会话身份不一致。')
                return copy.deepcopy(observed)

    def begin_daily_refresh(self, token: CampaignToken, operation_id: str, *, timeout: float = 5.0) -> dict:
        """Start one explicit normal state refresh under the current owner."""
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                client = self._client
                development = client.last_state.get('development') or {}
                revision = development.get('revision')
                if type(revision) is not int or revision <= 0:
                    raise BridgeError('日常刷新缺少当前状态版本。')
                self._busy = True
                self._command_event_locked(token, 'daily_state_refresh', 'admitted', {'operation_id': operation_id})
            try:
                wire = client.daily_state_refresh(expected_revision=revision, operation_id=operation_id, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._command_event_locked(token, 'daily_state_refresh', 'reply', {'operation_id': operation_id}, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, 'daily_state_refresh', 'failed', {'operation_id': operation_id}, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def daily_refresh_snapshot(self, token: CampaignToken) -> dict:
        """Read published refresh progress without issuing another game action."""
        with self._lock:
            self._campaign_check_locked(token)
            state = self._workflow_state or self._client.last_state
            observed = state.get('daily_refresh')
            if not isinstance(observed, dict):
                raise BridgeError('日常刷新状态不可用。')
            return copy.deepcopy(observed)

    def observe_daily_guild(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
            wire = client.daily_guild_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                observed = (ack.get('result') or {}).get('daily_guild')
                if ack.get('status') != 'COMPLETED' or not isinstance(observed, dict):
                    raise BridgeError('同盟状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if observed.get('character_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('同盟观察角色与已绑定会话身份不一致。')
                return copy.deepcopy(observed)

    def submit_daily_guild(self, token: CampaignToken, action: str, *, expected_revision: int,
                           character_id: str, guild_id: str, task_id: int, operation_id: str,
                           donation_type: int, max_premium: int, timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id, guild_id=guild_id,
                      task_id=task_id, operation_id=operation_id, donation_type=donation_type,
                      max_premium=max_premium)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('同盟动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_guild', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_guild_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_reward(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_reward_enabled') is not True:
                    return {'enabled': False, 'reward': None}
            wire = client.daily_reward_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed, enabled = result.get('daily_reward'), result.get('daily_reward_enabled')
                if ack.get('status') != 'COMPLETED' or not isinstance(observed, dict) or type(enabled) is not bool:
                    raise BridgeError('日常宝箱状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if observed.get('character_id') not in ('', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('日常宝箱观察角色与已绑定会话身份不一致。')
                return {'enabled': enabled, 'reward': copy.deepcopy(observed)}

    def submit_daily_reward(self, token: CampaignToken, action: str, *, expected_revision: int,
                            character_id: str, category_id: int, operation_id: str,
                            timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      category_id=category_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('日常宝箱动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_reward', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_reward_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_stone(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_stone_enabled') is not True:
                    return {'enabled': False, 'stone': None}
            wire = client.daily_stone_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed, enabled = (wire.get('state') or {}).get('daily_stone'), result.get('daily_stone_enabled')
                if ack.get('status') != 'COMPLETED' or enabled is not True or not isinstance(observed, dict):
                    raise BridgeError('采石状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if (observed.get('view') or {}).get('role_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('采石观察角色与已绑定会话身份不一致。')
                return {'enabled': True, 'stone': copy.deepcopy(observed)}

    def submit_daily_stone(self, token: CampaignToken, action: str, *, expected_revision: int,
                           character_id: str, task_id: int, operation_id: str,
                           timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('采石动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_stone', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_stone_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._campaign_check_locked(token)
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    if self._client is client and self._campaign_owner is token:
                        self._campaign_admission = False
                        self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_mingshi(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_mingshi_enabled') is not True:
                    return {'enabled': False, 'mingshi': None}
            wire = client.daily_mingshi_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed = (wire.get('state') or {}).get('daily_mingshi')
                if ack.get('status') != 'COMPLETED' or result.get('daily_mingshi_enabled') is not True or not isinstance(observed, dict):
                    raise BridgeError('铭石状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if (observed.get('view') or {}).get('role_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('铭石观察角色与已绑定会话身份不一致。')
                return {'enabled': True, 'mingshi': copy.deepcopy(observed)}

    def submit_daily_mingshi(self, token: CampaignToken, action: str, *, expected_revision: int,
                             character_id: str, task_id: int, operation_id: str,
                             timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('铭石动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_mingshi', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_mingshi_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._campaign_check_locked(token)
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    if self._client is client and self._campaign_owner is token:
                        self._campaign_admission = False
                        self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_refine(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_refine_enabled') is not True:
                    return {'enabled': False, 'refine': None}
            wire = client.daily_refine_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed = (wire.get('state') or {}).get('daily_refine')
                if ack.get('status') != 'COMPLETED' or result.get('daily_refine_enabled') is not True or not isinstance(observed, dict):
                    raise BridgeError('炼魂状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if (observed.get('view') or {}).get('role_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('炼魂观察角色与已绑定会话身份不一致。')
                return {'enabled': True, 'refine': copy.deepcopy(observed)}

    def submit_daily_refine(self, token: CampaignToken, action: str, *, expected_revision: int,
                            character_id: str, task_id: int, operation_id: str,
                            timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('炼魂动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_refine', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_refine_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._campaign_check_locked(token)
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    if self._client is client and self._campaign_owner is token:
                        self._campaign_admission = False
                        self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_arena(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_arena_enabled') is not True:
                    return {'enabled': False, 'arena': None}
            wire = client.daily_arena_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed = (wire.get('state') or {}).get('daily_arena')
                if ack.get('status') != 'COMPLETED' or result.get('daily_arena_enabled') is not True or not isinstance(observed, dict):
                    raise BridgeError('竞技宝箱状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if observed.get('character_id') not in ('', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('竞技宝箱观察角色与已绑定会话身份不一致。')
                return {'enabled': True, 'arena': copy.deepcopy(observed)}

    def submit_daily_arena(self, token: CampaignToken, action: str, *, expected_revision: int,
                           character_id: str, operation_id: str, timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('竞技宝箱动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_arena', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_arena_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._campaign_check_locked(token)
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    if self._client is client and self._campaign_owner is token:
                        self._campaign_admission = False
                        self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def rearm_daily_arena(self, token: CampaignToken, *, expected_revision: int,
                          character_id: str, operation_id: str, timeout: float = 5.0) -> dict:
        """Re-scan after the named settled chest claim, preserving its owned page when reusable."""
        return self.submit_daily_arena(token, 'rearm', expected_revision=expected_revision,
                                       character_id=character_id, operation_id=operation_id, timeout=timeout)

    def observe_daily_soul(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_soul_enabled') is not True:
                    return {'enabled': False, 'soul': None}
            wire = client.daily_soul_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed = (wire.get('state') or {}).get('daily_soul')
                if ack.get('status') != 'COMPLETED' or result.get('daily_soul_enabled') is not True or not isinstance(observed, dict):
                    raise BridgeError('聚魂状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if observed.get('character_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('聚魂观察角色与已绑定会话身份不一致。')
                return {'enabled': True, 'soul': copy.deepcopy(observed)}

    def submit_daily_soul(self, token: CampaignToken, action: str, *, expected_revision: int,
                          character_id: str, task_id: int, operation_id: str, soul_mode: int = 1, free_only: int = 1,
                          timeout: float = 5.0, admission=None) -> dict:
        # The optional predicate is a nonblocking deadline check. Pause/stop
        # use the controller's own admission flags under the lock below.
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id, soul_mode=soul_mode, free_only=free_only)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('聚魂动作角色与已绑定会话身份不一致。')
                if (not self._campaign_admission or self._campaign_stopping or self._busy
                        or admission is not None and not admission()):
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_soul', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_soul_command(action, **fields, timeout=timeout, admission=admission)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._campaign_check_locked(token)
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except BridgeAdmissionClosed as exc:
                with self._lock:
                    if resource_ticket is not None and not self._resource_admission.release_not_submitted(
                            resource_ticket, domain='daily_soul', action=action, fields=fields):
                        raise CampaignAdmissionClosed(f'{exc}; soul_unsubmitted_reservation_unreleased') from exc
                    self._command_event_locked(token, action, 'not_submitted', fields, error=exc)
                raise SoulPrepublicationAdmissionClosed(str(exc)) from exc
            except Exception as exc:
                with self._lock:
                    if self._client is client and self._campaign_owner is token:
                        self._campaign_admission = False
                        self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_popup(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        from .daily_popup import valid_popup
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
            wire = client.daily_popup_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            value = ((wire.get('ack') or {}).get('result') or {}).get('daily_popup')
            if not valid_popup(value):
                raise BridgeError('奖励确认状态读取未完成。')
            return copy.deepcopy(value)

    def submit_daily_popup(self, token: CampaignToken, *, window_type: str, instance_id: int,
                           appearance_generation: int, expected_revision: int, character_id: str,
                           operation_id: str, timeout: float = 5.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('奖励确认角色与当前连接不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('奖励确认发令门已关闭。')
                self._busy = True
                client = self._client
            def admission():
                with self._lock:
                    return (token is self._campaign_owner and self._client is client
                            and token.generation == self._generation and self._campaign_admission
                            and not self._campaign_stopping and not self._closing)
            fields = dict(window_type=window_type, instance_id=instance_id, appearance_generation=appearance_generation,
                          expected_revision=expected_revision, character_id=character_id, operation_id=operation_id)
            try:
                wire = client.daily_popup_confirm(**fields, timeout=timeout, admission=admission)
                self._runtime_state(client, wire.get('state', client.last_state))
                return wire
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_script(self, token: CampaignToken, task_id: int, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                character_id = json.loads(token.context_key)['character_id']
            wire = client.daily_script('observe', task_id=task_id, character_id=character_id,
                                       operation_id='0' * 32, expected_revision=1, timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            result = (wire.get('ack') or {}).get('result') or {}
            value = result.get('daily_script')
            if not isinstance(value, dict):
                raise BridgeError('日常状态读取未完成。')
            return value

    def submit_daily_script(self, token: CampaignToken, action: str, *, task_id: int,
                            character_id: str, operation_id: str, expected_revision: int,
                            timeout: float = 5.0) -> dict:
        return self._submit_daily_script(token, action, task_id=task_id, character_id=character_id,
            operation_id=operation_id, expected_revision=expected_revision, timeout=timeout)

    def submit_daily_script_cleanup(self, token: CampaignToken, action: str, *, task_id: int,
                                    character_id: str, operation_id: str, expected_revision: int,
                                    timeout: float = 5.0) -> dict:
        # Only the bound point-chest operation can request these stock closes
        # after soft stop. Native ownership and pending-receipt checks still
        # decide whether any particular cleanup action may actually run.
        if task_id != 310 or action not in ('finish', 'close_reward', 'close_chest', 'return', 'close_selector'):
            raise BridgeError('该动作不属于积分宝箱的正常收尾。')
        return self._submit_daily_script(token, action, task_id=task_id, character_id=character_id,
            operation_id=operation_id, expected_revision=expected_revision, timeout=timeout, allow_cleanup=True)

    def _submit_daily_script(self, token: CampaignToken, action: str, *, task_id: int,
                             character_id: str, operation_id: str, expected_revision: int,
                             timeout: float = 5.0, allow_cleanup: bool = False) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('日常动作角色与当前连接不一致。')
                if self._busy or (not self._campaign_admission or self._campaign_stopping) and not allow_cleanup:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_script', action,
                    dict(task_id=task_id, character_id=character_id, operation_id=operation_id,
                         expected_revision=expected_revision))
                client = self._client
                launch = self._capture_battle_epoch_locked(client)
                self._busy = True
            try:
                wire = client.daily_script(action, task_id=task_id, character_id=character_id,
                    operation_id=operation_id, expected_revision=expected_revision, timeout=timeout, **launch)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                return wire
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_wood(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_wood_enabled') is not True:
                    return {'enabled': False, 'wood': None}
            wire = client.daily_wood_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack = wire.get('ack') or {}
                result = ack.get('result') or {}
                observed = (wire.get('state') or {}).get('daily_wood')
                if ack.get('status') != 'COMPLETED' or result.get('daily_wood_enabled') is not True or not isinstance(observed, dict):
                    raise BridgeError('木人试炼状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if observed.get('role_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('木人试炼观察角色与已绑定会话身份不一致。')
                return {'enabled': True, 'wood': copy.deepcopy(observed)}

    def submit_daily_wood(self, token: CampaignToken, action: str, *, expected_revision: int,
                          character_id: str, task_id: int, operation_id: str,
                          timeout: float = 5.0, admission=None) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('木人试炼动作角色与已绑定会话身份不一致。')
                client = self._client
                if (client.last_state.get('daily_wood_enabled') is not True
                        or not self._campaign_admission or self._campaign_stopping or self._busy
                        or admission is not None and not admission()):
                    raise CampaignAdmissionClosed('木人试炼发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_wood', action, fields)
                self._busy = True
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_wood_command(action, **fields, timeout=timeout, admission=admission)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._campaign_check_locked(token)
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except BridgeAdmissionClosed as exc:
                raise CampaignAdmissionClosed(str(exc)) from exc
            except Exception as exc:
                with self._lock:
                    if self._client is client and self._campaign_owner is token:
                        self._campaign_admission = False
                        self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def observe_daily_timed(self, token: CampaignToken, *, timeout: float = 3.0) -> dict:
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                client = self._client
                if client.last_state.get('daily_timed_enabled') is not True:
                    return {'enabled': False, 'timed': None}
            wire = client.daily_timed_observe(timeout=timeout)
            self._runtime_state(client, wire.get('state', client.last_state))
            with self._lock:
                self._campaign_check_locked(token)
                ack, result = wire.get('ack') or {}, (wire.get('ack') or {}).get('result') or {}
                observed, enabled = result.get('daily_timed'), result.get('daily_timed_enabled')
                if ack.get('status') != 'COMPLETED' or type(enabled) is not bool or (enabled and not isinstance(observed, dict)):
                    raise BridgeError('限时日常状态读取未完成。')
                role = json.loads(token.context_key)['character_id']
                if observed is not None and observed.get('character_id') not in ('0', role):
                    self._campaign_admission = False
                    self._campaign_stopping = True
                    raise BridgeError('限时日常观察角色与已绑定会话身份不一致。')
                return {'enabled': enabled, 'timed': copy.deepcopy(observed)}

    def submit_daily_timed(self, token: CampaignToken, action: str, *, expected_revision: int,
                           character_id: str, task_id: int, operation_id: str,
                           timeout: float = 5.0) -> dict:
        fields = dict(expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                if character_id != json.loads(token.context_key)['character_id']:
                    raise BridgeError('限时日常动作角色与已绑定会话身份不一致。')
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed('日常发令门已关闭。')
                resource_ticket = self._authorize_resources_locked(token, 'daily_timed', action, fields)
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, 'admitted', fields)
            try:
                wire = client.daily_timed_command(action, **fields, timeout=timeout)
                self._runtime_state(client, wire.get('state', client.last_state))
                with self._lock:
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, 'reply', fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, 'failed', fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def submit_development(self, token: CampaignToken, action: str, *, expected_revision: int,
                           operation_id: str, target_id: int, desired_level: int, max_requests: int,
                           max_money: int = 0, max_stone: int = 0, max_food: int = 0,
                           mode: str = "", max_practice: int | None = None,
                           timeout: float = 5.0) -> dict:
        fields = {"expected_revision": expected_revision, "operation_id": operation_id,
                  "target_id": target_id, "desired_level": desired_level, "mode": mode,
                  "max_requests": max_requests, "max_money": max_money,
                  "max_stone": max_stone, "max_food": max_food}
        practice_fields = {"max_practice": max_practice} if action.startswith('star_') else {}
        fields.update(practice_fields)
        with self._io_lock:
            with self._lock:
                self._campaign_check_locked(token)
                self.development_snapshot(token)
                if not self._campaign_admission or self._campaign_stopping or self._busy:
                    raise CampaignAdmissionClosed("养成发令门已关闭。")
                resource_ticket = self._authorize_resources_locked(token, 'development', action, fields)
                # Same admission linearization as campaign: pause can close the
                # next submission while this one admitted request finishes.
                self._busy = True
                client = self._client
                self._command_event_locked(token, action, "admitted", fields)
            try:
                wire = client.development_command(action, expected_revision=expected_revision,
                    operation_id=operation_id, target_id=target_id, desired_level=desired_level,
                    max_requests=max_requests, max_money=max_money, max_stone=max_stone,
                    max_food=max_food, mode=mode, timeout=timeout, **practice_fields)
                self._runtime_state(client, wire.get("state", client.last_state))
                with self._lock:
                    self._resource_admission.release_not_invoked(resource_ticket, wire)
                    self._command_event_locked(token, action, "reply", fields, wire=wire)
                return wire
            except Exception as exc:
                with self._lock:
                    self._campaign_admission = False
                    self._command_event_locked(token, action, "failed", fields, error=exc)
                raise
            finally:
                with self._lock:
                    self._busy = False
                self._wake.set()

    def _authorize_resources_locked(self, token, domain, action, fields):
        # The caller owns _io_lock and _lock and has checked the active token.
        # last_state is the current bridge frame; old operation receipts never
        # supply a new charge quote or replenish this admission allowance.
        try:
            return self._resource_admission.authorize(self.resource_policy_provider,
                token, self._client.last_state, domain, action, fields)
        except ResourcePolicyDenied as exc:
            self.last_resource_policy_denial = {'domain': domain, 'action': action,
                'reason': str(exc), 'operation_id': fields.get('operation_id', ''),
                'run_id': token.run_id, 'not_submitted': True}
            self._command_event_locked(token, action, 'resource_policy_denied', fields, error=exc)
            raise ResourcePolicyAdmissionClosed(str(exc)) from exc

    def pause_campaign(self, token: CampaignToken, *, reason: str = "workflow_pause") -> bool:
        with self._lock:
            if token is not self._campaign_owner:
                return False
            self._campaign_admission = False
            self._campaign_control_reason = reason
            drained = self._cancel_battle_launch_locked()
            self._publish_workflow_locked()
            return drained

    def resume_campaign(self, token: CampaignToken) -> bool:
        with self._lock:
            self._campaign_check_locked(token)
            if self._campaign_stopping:
                return False
            self._client.open_battle_admission()
            self._campaign_admission = True
            self._campaign_control_reason = "workflow_resume"
            self._publish_workflow_locked()
            return True

    def stop_campaign(self, token: CampaignToken, *, reason: str = "workflow_stop") -> bool:
        with self._lock:
            if token is not self._campaign_owner:
                return False
            self._campaign_admission = False
            self._campaign_stopping = True
            self._campaign_control_reason = self._connection_stop_reason or reason
            drained = self._cancel_battle_launch_locked()
            self._publish_workflow_locked()
            return drained

    def _capture_battle_epoch_locked(self, client) -> dict:
        return {'battle_epoch': client.capture_battle_epoch()}

    def _cancel_battle_launch_locked(self) -> bool:
        try:
            if self._client is not None:
                self._client.cancel_battle_admission()
            return True
        except Exception as exc:
            self._campaign_stopping = True
            self._campaign_control_reason = 'battle_launch_cancel_unverified'
            self._workflow_error = str(exc)
            self._emit('error', str(exc), level='ERROR')
            return False

    def release_campaign(self, token: CampaignToken, *, cleanup: bool = True) -> None:
        with self._lock:
            if token is not self._campaign_owner:
                return
            self._campaign_admission = False
            self._cancel_battle_launch_locked()
            self._retire_workflow_locked("工作流所有权已释放。")
            self._campaign_owner = None
            self._campaign_subscribers.clear()
            if cleanup and self._client and not self._client.terminal and not self._closing:
                self._cancel.set()
                self._set_bridge("stopping", "征战账本已保存；正在清理对象组件。")
                self._launch_locked(self._disconnect_worker, "campaign-cleanup", reset_cancel=False)
            self._bridge_event()

    def retire_idle_campaign(self) -> bool:
        """Discard an old task token after the caller confirms its runner is idle."""
        with self._lock:
            retired = self._campaign_owner is not None
            self._campaign_admission = False
            self._campaign_stopping = True
            self._cancel_battle_launch_locked()
            self._retire_workflow_locked('下一次点击已退休上次任务所有权。')
            self._campaign_owner = None
            self._campaign_subscribers.clear()
            if self._client and getattr(self._client, 'is_unknown', False):
                self._set_bridge('unknown', '上次连接的观察已中断；尚未确认的操作不会自动重试。')
            self._bridge_event()
            return retired

    def bind(self, window: WindowInfo) -> None:
        if not isinstance(window, WindowInfo):
            raise TypeError("需要有效的游戏窗口。")
        with self._lock:
            if self._closing or self._campaign_owner or self._prelogin_bridge_active or self._prelogin_unknown:
                return
            same = self._window and (self._window.hwnd, self._window.process_id) == (window.hwnd, window.process_id)
            if same:
                self._window = window
                self._wake.set()
                return
            self._advance()
            self._window, self._identity, self._state_valid = window, None, False
            self._cancel.set()
            self._emit("state_error", "正在读取所选游戏窗口状态。")
            if self._client and not self._client.terminal:
                self._set_bridge("stopping", "切换窗口前正在停止原对象连接。")
                if not self._busy:
                    self._launch_locked(self._disconnect_worker, "object-rebind-stop", reset_cancel=False)
            else:
                self._set_bridge("disconnected", "已绑定窗口；状态监控不载入对象调用组件。")
            self._wake.set()

    def retire_session_monitor(self, window: WindowInfo, creation_time: int) -> bool:
        """Fence passive reads before the owner closes an already stopped session.

        This does not stop a bridge or resolve an unknown operation. The caller
        must own the exact process and retain its separate close/exit receipts.
        """
        if not isinstance(window, WindowInfo) or type(creation_time) is not int or creation_time <= 0:
            return False
        # Match monitor/worker lock order and drain any passive scan before
        # handing the process to its closer. Never wait for I/O under _lock.
        with self._io_lock, self._lock:
            identity, client = self._identity, self._client
            if (self._closing or self._shutdown or self._busy or self._campaign_owner is not None
                    or self._prelogin_bridge_active or self._prelogin_unknown is not None
                    or self._bridge_state != "disconnected" or client is not None and not client.terminal
                    or self._window is None or identity is None):
                return False
            expected = (window.process_id, creation_time, window.hwnd)
            if ((self._window.process_id, self._window.hwnd) != (window.process_id, window.hwnd)
                    or (identity.pid, identity.creation_time, identity.hwnd) != expected
                    or client is not None and
                    (client.identity.pid, client.identity.creation_time, client.identity.hwnd) != expected):
                return False
            # The generation change also rejects a read/refusal already in
            # flight. Keep first_failure and the terminal client's receipts.
            self._advance()
            self._window, self._identity = None, None
            self._campaign_admission = False
            self._cancel.set()
            message = "本轮连接已结束，正在正常退出游戏。"
            self._clear_native_player_locked(message)
            self._emit("log", "会话监控退休 " + json.dumps(
                {"pid": identity.pid, "process_creation_filetime": identity.creation_time,
                 "hwnd": identity.hwnd, "session_id": client.nonce if client else "",
                 "generation": self._generation, "reason": "owned_session_normal_close"},
                ensure_ascii=False, separators=(",", ":")),
                data={"workflow_notice": "session_monitor_retired"})
            self._set_bridge("disconnected", message)
            self._wake.set()
            return True

    def unbind(self) -> None:
        with self._lock:
            if self._closing or self._campaign_owner or self._prelogin_bridge_active or self._prelogin_unknown:
                return
            self._advance()
            self._window, self._identity, self._state_valid = None, None, False
            self._cancel.set()
            self._emit("state_error", "未绑定游戏窗口。")
            if self._client and not self._client.terminal:
                self._set_bridge("stopping", "正在停止原窗口的对象连接。")
                if not self._busy:
                    self._launch_locked(self._disconnect_worker, "object-unbind-stop", reset_cancel=False)
            else:
                self._set_bridge("disconnected", "未绑定游戏窗口。")
            self._wake.set()

    def refresh_state(self) -> None:
        if not self._closing:
            self._wake.set()

    def _ensure_identity(self, window: WindowInfo, generation: int) -> ProcessIdentity:
        current = self._identity_reader(window)
        with self._lock:
            if generation != self._generation or self._window is None or self._closing:
                raise Refusal("窗口绑定已改变。")
            if self._identity is not None and self._identity != current:
                raise Refusal("目标进程或窗口身份已改变，请重新绑定。")
            self._identity = current
            return current

    def _clear_native_player_locked(self, message: str) -> None:
        """An unavailable display sample does not invalidate typed task state."""
        changed = self._state_valid or self._native_player is not None or self._native_player_unavailable != message
        self._native_player = None
        self._native_player_client = None
        self._native_player_received = 0.0
        self._native_player_unavailable = message
        self._state_valid = False
        if changed:
            self._emit("state_error", message)

    def _remember_native_player_locked(self, client: BridgeClient, state: dict) -> bool:
        """Only call for an initial capture or a completed snapshot command.

        Heartbeats and business notifications republish the old snapshot with a
        new state timestamp; they must never renew the resource observation.
        """
        if client is not self._client or client.terminal:
            return False
        identity = client.identity
        if (state.get("pid") != identity.pid or
                state.get("process_creation_filetime") != identity.creation_time or
                state.get("session_id") != client.nonce or state.get("nonce") != client.nonce or
                state.get("verified_native_tid") != identity.tid):
            raise Refusal("原生状态快照的进程或会话身份不匹配。")
        snapshot = state.get("snapshot")
        resources = snapshot.get("resources") if isinstance(snapshot, dict) else None
        role = (state.get("campaign") or {}).get("character_id")
        if type(role) is not str or not role.isdecimal() or int(role) == 0:
            self._clear_native_player_locked("连接已就绪，等待有效角色资源观察。")
            return False
        if (state.get("lifecycle") != "ready" or state.get("ready") is not True or
                state.get("main_thread_verified") is not True or state.get("component_destroyed") is not False or
                not isinstance(snapshot, dict) or type(snapshot.get("main_instance_id")) is not int or
                snapshot["main_instance_id"] == 0 or not isinstance(resources, dict) or
                any(type(resources.get(key)) is not int for key in
                    ("level", "stamina", "gold", "bind_gold", "money", "money64", "mana"))):
            self._clear_native_player_locked("当前页面暂不提供完整资源数据，等待下一次读取。")
            return False
        observed = state.get("updated_utc")
        try:
            age = (datetime.now(timezone.utc) - datetime.fromisoformat(observed.replace("Z", "+00:00"))).total_seconds()
            if not -2.0 <= age <= 10.0:
                raise ValueError("stale snapshot")
        except (AttributeError, TypeError, ValueError):
            self._clear_native_player_locked("资源数据已过期，等待下一次读取。")
            return False
        self._native_player = {"pid": identity.pid, "process_creation_filetime": identity.creation_time,
                               "observed_at": observed, "source": "native_snapshot", "cached": True,
                               "character_id": role,
                               **{key: resources[key] for key in ("level", "stamina", "gold", "bind_gold", "mana")},
                               "money": resources["money"] + resources["money64"]}
        self._native_player_client = client
        self._native_player_received = time.monotonic() - max(0.0, age)
        self._native_player_unavailable = ""
        return True

    def _native_player_locked(self, client: BridgeClient, identity: ProcessIdentity) -> dict | None:
        value = self._native_player
        role = (client.last_state.get("campaign") or {}).get("character_id")
        if client.identity != identity:
            raise Refusal("原生角色资源快照的进程身份已改变。")
        if not value or client is not self._native_player_client:
            return None
        if role != value["character_id"]:
            self._clear_native_player_locked("角色已改变，等待当前角色资源观察。")
            return None
        if time.monotonic() - self._native_player_received > 10.0:
            self._clear_native_player_locked("资源数据已过期，等待下一次读取。")
            return None
        return dict(value)

    def _retire_exited_connection(self, window: WindowInfo, generation: int) -> bool:
        """Retire a terminal connection only after its exact process is gone.

        Window disappearance and access failures are not exit evidence. This
        runs on the passive monitor, never on the application owner thread, and
        does not resolve or rewrite any historical operation/session record.
        """
        with self._io_lock:
            with self._lock:
                identity, client = self._identity, self._client
                if (generation != self._generation or identity is None or self._window != window
                        or self._closing or self._shutdown or self._busy or self._pending_connection is not None
                        or self._campaign_owner is not None or self._prelogin_bridge_active or self._prelogin_unknown
                        or client is not None and client.identity != identity):
                    return False
                recovery = copy.deepcopy(self._recovery_status_locked())
            if process_status(identity.pid, identity.creation_time) != 'gone':
                return False
            # Process exit ends its callbacks and receiver. Retire only local
            # transport ownership; retain every unresolved business receipt.
            if client is not None and not client.target_exited():
                return False
            with self._lock:
                if (generation != self._generation or self._identity != identity or self._window != window
                        or self._client is not client or self._closing or self._shutdown or self._busy
                        or self._pending_connection is not None or self._campaign_owner is not None
                        or self._prelogin_bridge_active or self._prelogin_unknown
                        or client is not None and (not client.terminal or client.state_events_enabled)):
                    return False
                archive = dict(recovery=recovery, first_failure=copy.deepcopy(self._workflow_fault),
                    resolution=dict(status='TARGET_EXITED', scope='connection_only', replay_allowed=False,
                        **identity.as_dict(), observed_at=datetime.now(timezone.utc).isoformat(), process_status='gone'),
                    evidence_path=str(client.session_dir) if client else '')
                self._connection_recovery_history.append(archive)
                self._advance()
                self._window = self._identity = self._client = self._identity_cleanup_client = None
                self._cancel.set()
                self._campaign_admission = False
                self._workflow_state = {}
                self._workflow_error = ''
                self._workflow_fault = {}
                self._connection_error = self._connection_stop_reason = self._connection_refusal = ''
                self._connection_refused_at = ''
                self._connection_token = None
                self._connection_phase = 'target_exited'
                self._clear_native_player_locked('原游戏进程已退出，正在等待新的游戏窗口。')
                self._set_bridge('disconnected', '原游戏进程已退出，正在等待新的游戏窗口。')
                self._emit('log', '原连接已随进程退出而退休 ' + json.dumps(archive, ensure_ascii=False,
                    separators=(',', ':')), data={'workflow_notice': 'connection_target_exited'}, identity=identity)
                return True

    def _monitor(self) -> None:
        while not self._poll_stop.is_set():
            self._wake.wait(self._poll_seconds)
            self._wake.clear()
            if self._poll_stop.is_set():
                break
            with self._lock:
                uncertain = self._prelogin_unknown
                window, generation = self._window, self._generation
            if uncertain is not None and process_status(uncertain.pid, uncertain.creation_time) == "gone":
                with self._lock:
                    if self._prelogin_unknown == uncertain:
                        self._prelogin_unknown = None
                        if self._client is None or self._client.terminal:
                            self._set_bridge("disconnected", "原游戏已退出，未确认的登录接口已随进程结束。")
            if window is None:
                continue
            try:
                identity = self._ensure_identity(window, generation)
                with self._lock:
                    if generation != self._generation or self._closing:
                        continue
                    client = self._client
                    poll_bridge = bool(client and not self._busy and self._bridge_state == "ready")
                if poll_bridge:
                    try:
                        with self._io_lock:
                            # Recheck after serialization: a business command or
                            # stop may have changed the session while waiting.
                            with self._lock:
                                valid = client is self._client and not self._busy and self._bridge_state == "ready" and not self._closing
                                campaign = self._campaign_owner is not None
                            if valid:
                                if time.monotonic() - self._last_heartbeat >= 5.0:
                                    wire = client.request("snapshot", timeout=3.0)
                                    with self._lock:
                                        self._remember_native_player_locked(client, wire.get("state", {}))
                                    self._last_heartbeat = time.monotonic()
                                elif not callable(getattr(client, "start_state_events", None)) and campaign:
                                    observe = (client.challenge_observe if self._campaign_owner and self._campaign_owner.scope == 'challenge'
                                               else client.campaign_observe)
                                    observe(timeout=3.0)
                    except Exception as request_error:
                        # BridgeClient may already have recorded the transport
                        # failure. Preserve that first cause even if persisting
                        # it also failed, and do not issue another snapshot into
                        # a client whose outcome is unknown.
                        persistence_error = ""
                        if not client.terminal:
                            try:
                                client.mark_unknown(request_error)
                            except Exception as write_error:
                                persistence_error = str(write_error)
                        with self._lock:
                            if generation != self._generation or self._closing:
                                continue
                            self._state_valid = False
                            message = self._record_workflow_fault_locked(client, request_error, "bridge_poll")
                            self._campaign_admission = False
                            self._campaign_stopping = self._campaign_owner is not None
                            self._emit("state_error", message, level="WARNING")
                            self._set_bridge("disconnected" if client.terminal else "unknown", message)
                            if persistence_error:
                                self._emit("log", "首个桥接错误：" + message + "；会话记录写入失败：" + persistence_error, level="ERROR")
                        continue
                # Connection work shares this lock: never scan a Mono graph
                # while the current object connection is being established.
                with self._io_lock:
                    with self._lock:
                        if generation != self._generation or self._closing:
                            continue
                        client = self._client
                        external = False
                        if (self._prelogin_unknown is not None or self._bridge_state == "connecting" or
                                client and not client.terminal and self._bridge_state != "ready"):
                            value = None
                        elif client and not client.terminal:
                            value = self._native_player_locked(client, identity)
                        else:
                            external = True
                    if external:
                        value = self._reader.read(identity.pid, expected_creation=identity.creation_time)
                    with self._lock:
                        if generation != self._generation or self._closing:
                            continue
                        if value is not None:
                            if value.get("pid") != identity.pid or value.get("process_creation_filetime") != identity.creation_time:
                                raise Refusal("状态读取回执的进程身份不匹配。")
                            if external:
                                self._passive_player = dict(value)
                                self._passive_player_received = time.monotonic()
                                self._passive_player_generation = generation
                                self._passive_error = ''
                                self._passive_error_generation = generation
                            self._state_valid = True
                            self._emit("player", data=value, identity=identity)
                        elif self._state_valid:
                            self._state_valid = False
                            self._emit('state_error', '角色状态正在正常刷新或连接，等待新状态就绪。')
            except Exception as exc:
                with self._lock:
                    if generation != self._generation or self._closing:
                        continue
                    self._state_valid = False
                    self._passive_error = str(exc)
                    self._passive_error_generation = generation
                    message = self._record_workflow_fault_locked(self._client, exc, "state_monitor")
                    self._campaign_admission = False
                    self._campaign_stopping = self._campaign_owner is not None
                    self._emit("state_error", message, level="WARNING")
                    client = self._client
                    if isinstance(exc, (BridgeError, BridgeUnknown)) and client:
                        self._set_bridge("disconnected" if client.terminal else "unknown", message)
                    else:
                        self._bridge_event()
                        self._publish_workflow_locked()
                if isinstance(exc, Refusal) and self._retire_exited_connection(window, generation):
                    continue
                with self._lock:
                    if generation != self._generation or self._closing:
                        continue
                    client = self._client
                    if (client and not client.terminal and not self._busy and isinstance(exc, Refusal)
                            and client is not self._identity_cleanup_client):
                        # Cleanup can itself be unconfirmed. Do not let its
                        # completion wake turn the same identity refusal into
                        # an automatic stop/retry loop for this client.
                        self._identity_cleanup_client = client
                        self._cancel.set()
                        self._launch_locked(self._disconnect_worker, "object-identity-stop", reset_cancel=False)

    def _launch_locked(self, target: Callable, name: str, *, reset_cancel: bool = True) -> bool:
        if self._busy or self._prelogin_bridge_active or self._prelogin_unknown and name != 'object-connect':
            return False
        if reset_cancel:
            self._cancel = threading.Event()
        self._busy = True
        thread = threading.Thread(target=self._run_worker, args=(target,), name=name, daemon=True)
        self._operation = thread
        try:
            thread.start()
        except Exception:
            self._busy = False
            self._operation = None
            raise
        return True

    def _run_worker(self, target: Callable) -> None:
        try:
            with self._io_lock:
                target()
        except Exception as exc:
            with self._lock:
                client = self._client
                if not self._closing:
                    if self._pending_connection is not None:
                        self._emit('log', '上次连接清理错误：' + str(exc), level='WARNING')
                    elif self._connection_phase in ("queued", "identity", "factory", "bridge_connect", "player_loading"):
                        if not self._connection_error:
                            self._connection_error = str(exc)
                        self._connection_phase += "_failed"
                    if self._pending_connection is None:
                        if client is not None:
                            evidence = getattr(exc, 'recovery_status', None)
                            if not isinstance(evidence, dict):
                                evidence = getattr(client, 'recovery_status', None)
                            if isinstance(evidence, dict) and evidence.get('process_restart_required') is True:
                                if not self._workflow_fault:
                                    first = evidence.get('first_unknown') or {}
                                    if isinstance(first, dict) and first.get('message'):
                                        self._workflow_fault = copy.deepcopy(first)
                                        self._workflow_fault['stage'] = self._workflow_fault.get('stage') or 'prior_session'
                                self._record_workflow_fault_locked(client, exc, 'connection_recovery')
                                self._retain_restart_required_locked(client, evidence)
                        self._set_bridge("unknown" if client and not client.terminal else "error", str(exc))
                        self._emit("log", str(exc), level="ERROR")
        finally:
            with self._lock:
                self._busy = False
                pending = self._pending_connection
                self._pending_connection = None
                if pending is not None and not self._closing and pending[1] == self._generation:
                    try:
                        self._launch_locked(lambda: self._connect_worker(*pending), 'object-connect', reset_cancel=False)
                    except Exception as exc:
                        self._connection_token = None
                        self._connection_phase = 'queued_failed'
                        self._connection_error = str(exc)
                        self._set_bridge('error', str(exc))
                        self._emit('log', str(exc), level='ERROR')
                elif not self._closing:
                    if self._cancel.is_set() and (not self._client or self._client.terminal):
                        message = (self._bridge_message if self._connection_phase == "stopped" else
                                   "对象调用已停止；状态监控继续。")
                        self._set_bridge("disconnected", message)
                    else:
                        self._bridge_event()
            self._wake.set()

    def connect(self) -> bool:
        with self._lock:
            connecting = bool(self._busy and self._operation and self._operation.is_alive()
                              and self._operation.name in ('object-connect', 'object-soul-reconcile'))
            queue_after_current = self._busy and not connecting
            reason = ""
            if self._closing:
                reason = "controller_closing"
            elif self._prelogin_bridge_active:
                reason = "prelogin_bridge_owned"
            elif connecting or self._pending_connection is not None:
                reason = "connection_in_progress"
            elif self._window is None:
                reason = "window_unbound"
            elif self._recovery_status_locked().get('process_restart_required') is True:
                reason = 'game_process_restart_required'
            if reason:
                self._connection_refusal = reason
                self._connection_refused_at = datetime.now(timezone.utc).isoformat(timespec="milliseconds")
                self._bridge_message = "对象连接请求未接受：" + reason
                self._bridge_event()
                self._emit("log", self._bridge_message, level="WARNING")
                return False
            window, previous_client = self._window, self._client
            if previous_client is not None and (self._workflow_fault or self._campaign_owner
                                                or getattr(previous_client, 'is_unknown', False)):
                self._connection_recovery_history.append(dict(
                    recovery=copy.deepcopy(self._recovery_status_locked()),
                    first_failure=copy.deepcopy(self._workflow_fault),
                    old_session_id=previous_client.nonce, old_outcome='UNKNOWN', replay_allowed=False,
                    reason='explicit_reconnect', evidence_path=str(previous_client.session_dir)))
            self._cancel.set()
            self._campaign_admission = False
            self._cancel_battle_launch_locked()
            self._advance()
            self._campaign_owner = None
            self._campaign_subscribers.clear()
            self._campaign_stopping = False
            generation = self._generation
            self._client = None
            self._connection_phase = "queued"
            self._connection_started_at = datetime.now(timezone.utc).isoformat(timespec="milliseconds")
            self._connection_generation = generation
            self._connection_token = CampaignConnectionToken(generation)
            self._connection_error = ""
            self._connection_stop_reason = ""
            self._connection_refusal = ""
            self._connection_refused_at = ""
            self._set_bridge("connecting", "正在校验窗口身份并连接对象调用。")
            try:
                if queue_after_current:
                    self._pending_connection = (window, generation, previous_client)
                    self._cancel = threading.Event()
                    return True
                return self._launch_locked(lambda: self._connect_worker(window, generation, previous_client), "object-connect")
            except Exception as exc:
                # No worker was started; do not leave an unclaimable attempt
                # or a permanently connecting state for a later task.
                self._connection_token = None
                self._connection_phase = "queued_failed"
                self._connection_error = str(exc)
                self._set_bridge("error", str(exc))
                raise

    def recover_soul_connection(self) -> bool:
        """Compatibility entry; historical soul evidence cannot gate reconnect."""
        return self.connect()

    def begin_campaign_connection(self) -> CampaignConnectionToken | None:
        """Start and capture the exact attempt atomically; GUI connect stays bool."""
        with self._lock:
            if not self.connect():
                return None
            return self._connection_token

    def cancel_campaign_connection(self, token: CampaignConnectionToken) -> bool:
        """Retire this attempt only while no business owner has claimed it.

        Check and stop share the owner lock. An old task cannot close another
        owner's admission or cancel a replacement binding/connection attempt.
        """
        with self._lock:
            if (token is None or token is not self._connection_token or token.generation != self._generation or
                    self._campaign_owner is not None or self._closing):
                return False
            return self.stop()

    def claim_workflow_session(self, token: CampaignConnectionToken) -> WorkflowSessionToken | None:
        with self._lock:
            if (token is None or token is not self._connection_token or token.generation != self._generation
                    or self._campaign_owner is not None or self._busy or self._closing
                    or self._bridge_state != 'ready' or self._client is None or self._client.terminal):
                return None
            return WorkflowSessionToken(self._generation, self._client)

    def release_workflow_session(self, token: WorkflowSessionToken) -> bool:
        with self._lock:
            if (token is None or token.generation != self._generation or token.client is not self._client
                    or self._campaign_owner is not None or self._busy or self._closing):
                return False
            return self.stop()

    def _connect_worker(self, window: WindowInfo, generation: int, previous_client=None) -> None:
        if not self._connect_progress(generation, "identity", "正在校验对象连接的窗口与进程身份。"):
            return
        identity = self._ensure_identity(window, generation)
        if previous_client is not None:
            recovery = getattr(previous_client, 'recovery_status', {})
            with self._lock:
                if generation == self._generation and isinstance(recovery, dict):
                    self._retain_restart_required_locked(previous_client, recovery)
            try:
                previous_client.retire_for_retry()
            except Exception as exc:
                # Cleanup belongs to the retired channel. Native bootstrap owns
                # proving that the replacement is the only live receiver.
                self._emit('log', '上次对象连接已退休：' + str(exc), level='WARNING')
        if not self._connect_progress(generation, "factory", "正在创建对象连接客户端。"):
            return
        holder = {}
        client = self._client_factory(self.project_root, identity,
                                      lambda: self._identity_validator(window, identity),
                                      on_state=lambda state: self._runtime_state(holder["client"], state))
        holder["client"] = client
        with self._lock:
            if generation != self._generation or self._closing:
                return
            self._client = client
            self._native_player = None
            self._native_player_client = None
            self._workflow_state = {}
            self._connection_phase = "bridge_connect"
            self._set_bridge("connecting", "正在建立对象桥接会话并等待就绪。")
        state = client.connect(self._cancel)
        if self._cancel.is_set() and not client.terminal:
            client.stop()
        # Link readiness is the native protocol contract. Player resources
        # are a separate observation and cannot stop an established link.
        with self._lock:
            if self._client is client and generation == self._generation and not self._closing:
                role = (state.get('campaign') or {}).get('character_id') if isinstance(state, dict) else None
                if isinstance(role, str) and role.isdecimal() and int(role) > 0:
                    self._remember_native_player_locked(client, state)
                else:
                    self._clear_native_player_locked('连接已就绪，等待角色资源观察。')
        with self._lock:
            if self._client is client and not self._closing:
                if client.terminal:
                    self._connection_phase = "stopped"
                    returned = state.get("state", state) if isinstance(state, dict) else {}
                    reason = returned.get("reason") or client.last_state.get("reason") or ""
                    reason = reason if isinstance(reason, str) else ""
                    self._connection_stop_reason = reason
                    cancelled = self._cancel.is_set() or generation != self._generation
                    expected_stop = reason in ("stop", "cancelled_before_bootstrap") or (cancelled and not reason)
                    if expected_stop:
                        message = "对象连接已取消或显式停止；组件已停止，状态监控继续。"
                        if reason:
                            message += " 原因：" + reason
                        level = "INFO"
                    else:
                        if not self._connection_error:
                            self._connection_error = reason or "connection_stopped_before_ready"
                        message = "对象连接未就绪即已干净停止：" + self._connection_error + "；状态监控继续。"
                        level = "WARNING"
                    self._set_bridge("disconnected", message)
                    self._emit("log", message, level=level)
                elif generation == self._generation:
                    self._workflow_error = ""
                    self._workflow_fault = {}
                    self._last_heartbeat = time.monotonic()
                    self._connection_phase = "ready"
                    self._set_bridge("ready", "对象调用已连接；仅显式验证会执行操作。")
        with self._lock:
            start_events = (self._client is client and generation == self._generation and
                            not self._closing and not client.terminal and self._bridge_state == "ready")
        if start_events and callable(getattr(client, "start_state_events", None)):
            try:
                client.start_state_events(on_error=lambda exc: self._state_events_error(client, exc),
                    on_notice=lambda notice: self._state_events_notice(client, notice))
            except Exception as exc:
                self._state_events_error(client, exc)

    def _state_events_notice(self, client: BridgeClient, notice: dict) -> None:
        """Transient sharing waits are diagnostics, never stop decisions."""
        try:
            with self._lock:
                if self._closing or client is not self._client:
                    return
                fields = ("kind", "stage", "message", "winerror", "attempts", "elapsed_ms")
                details = {key: notice[key] for key in fields if key in notice}
                if notice.get("filename"):
                    details["file"] = Path(str(notice["filename"])).name
                self._emit("log", "状态通知 " + json.dumps(
                    {"session_id": client.nonce, "generation": self._generation, **details},
                    ensure_ascii=False, separators=(",", ":")),
                    data={"workflow_notice": notice.get("kind") or "notice"},
                    level="WARNING" if notice.get("kind") == "state_read_wait" else "INFO")
        except Exception:
            # A diagnostic callback cannot invalidate bridge state or admission.
            return

    def _state_events_error(self, client: BridgeClient, error: Exception) -> None:
        """Invalidate admission; only native state may establish business facts."""
        with self._lock:
            if self._closing or client is not self._client:
                return
            message = self._record_workflow_fault_locked(client, error, "state_notification")
            self._state_valid = False
            self._campaign_admission = False
            self._campaign_stopping = self._campaign_owner is not None
            self._emit("state_error", message, level="WARNING")
            self._set_bridge("disconnected" if client.terminal else "unknown", message)
        self._wake.set()

    def _connect_progress(self, generation: int, phase: str, message: str) -> bool:
        with self._lock:
            if generation != self._generation or self._closing:
                return False
            self._connection_phase = phase
            self._set_bridge("connecting", message)
            return True

    def _runtime_state(self, client: BridgeClient, state: dict) -> None:
        with self._lock:
            if self._closing or client is not self._client:
                return
            self._workflow_state = copy.deepcopy(state)
            self._last_reply = time.monotonic()
            lifecycle = state.get("lifecycle")
            if lifecycle == "ready" and self._connection_phase == "ready" and self._bridge_state not in ("stopping", "unknown"):
                self._bridge_state = "ready"
            elif lifecycle in ("restoring", "recovery"):
                self._bridge_state = "stopping" if self._cancel.is_set() else "ready"
            elif lifecycle in ("failed", "recovery_unknown"):
                self._bridge_state = "unknown"
                self._record_workflow_fault_locked(client,
                    state.get("reason") or "native_state_" + lifecycle, "native_state")
                self._campaign_admission = False
                self._campaign_stopping = self._campaign_owner is not None
            elif lifecycle == "stopped":
                self._bridge_state = "disconnected" if client.terminal else "stopping"
                self._bridge_message = "对象调用组件已停止；状态监控继续。" if client.terminal else "正在确认对象调用组件停止。"
                self._campaign_admission = False
                self._campaign_stopping = self._campaign_owner is not None
                if not self._connection_stop_reason:
                    self._connection_stop_reason = state.get("reason") or (
                        "control_stop" if self._cancel.is_set() else "native_component_stopped")
                self._campaign_control_reason = self._connection_stop_reason
            self._retain_restart_required_locked(client, state)
            action = state.get("action") or {}
            phase = action.get("phase", "")
            if action.get("outcome") == "PENDING" and phase != self._last_action_phase:
                self._last_action_phase = phase
                action_state = "restoring" if lifecycle in ("restoring", "recovery") else "running"
                self._emit("action", data={"state": action_state, "result": self._action_result(client, {"state": state})}, identity=client.identity)
            self._bridge_event()
            if self._campaign_owner:
                self._publish_campaign_locked(state)
                self._publish_workflow_locked()

    def verify_call(self) -> bool:
        with self._lock:
            if not self._can_verify_locked():
                return False
            self._advance()
            self._last_action_phase = ""
            self._emit("action", "正在验证 MainUI.Disable/Enable。", {"state": "running", "result": {}})
            return self._launch_locked(self._verify_worker, "object-mainui-verify")

    def _verify_worker(self) -> None:
        with self._lock:
            client = self._client
        if client is None:
            raise BridgeError("对象调用尚未连接。")
        try:
            result = client.stop() if self._cancel.is_set() else client.verify(self._cancel)
            if self._cancel.is_set() and not client.terminal:
                result = client.stop()
            with self._lock:
                if self._closing or self._client is not client:
                    return
                action = result.get("ack", {}).get("result", {}).get("action") or {}
                if client.terminal:
                    outcome = "stopped"
                    self._set_bridge("disconnected", "对象调用已停止并确认组件销毁；状态监控继续。")
                elif result.get("ack", {}).get("status") == "REJECTED":
                    outcome = "failed"
                elif action.get("outcome") == "PASS" and action.get("restore_verified") is True:
                    outcome = "complete"
                    self._set_bridge("ready", "调用验证完成并已恢复原状态。")
                elif action.get("outcome") == "FAIL" and action.get("restore_verified") is True:
                    outcome = "failed"
                else:
                    outcome = "unverified"
                    client.mark_unknown("action_result_unverified")
                    self._set_bridge("unknown", "调用结果或恢复尚未确认，请停止或重启游戏。")
                self._emit("action", data={"state": outcome, "result": self._action_result(client, result)}, identity=client.identity)
        except Exception as exc:
            with self._lock:
                if not self._closing and self._client is client:
                    self._emit("action", str(exc), {"state": "unverified", "result": {}}, "WARNING", identity=client.identity)
            raise

    def disconnect(self) -> bool:
        return self.stop()

    def stop(self) -> bool:
        with self._lock:
            if self._campaign_owner:
                self._campaign_admission = False
                self._campaign_stopping = True
                self._campaign_control_reason = self._connection_stop_reason or "control_stop"
                drained = self._cancel_battle_launch_locked()
                self._publish_workflow_locked()
                return drained
            if self._closing:
                return False
            if not self._client and not self._busy:
                return False
            if self._client and self._client.terminal and not self._busy:
                return False
            self._advance()
            self._cancel.set()
            self._set_bridge("stopping", "正在停止对象调用并确认恢复与组件销毁。")
            self._wake.set()
            if self._busy:
                return True
            return self._launch_locked(self._disconnect_worker, "object-stop", reset_cancel=False)

    def _disconnect_worker(self) -> None:
        with self._lock:
            client = self._client
        if client is None:
            with self._lock:
                self._set_bridge("disconnected", "对象调用未载入；状态监控继续。")
            return
        result = client.stop()
        with self._lock:
            if self._client is client and not self._closing:
                self._set_bridge("disconnected", "对象调用已停止；状态监控继续。")
                self._emit("action", data={"state": "stopped", "result": self._action_result(client, result)}, identity=client.identity)

    def close(self) -> None:
        with self._lock:
            if self._closing:
                return
            self._closing = True
            self._campaign_admission = False
            self._campaign_stopping = True
            self._cancel_battle_launch_locked()
            self._advance()
            self._cancel.set()
            self._poll_stop.set()
            self._wake.set()
            self._state_valid = False
            self._emit("state_error", "助手正在关闭，旧状态已失效。")
            thread = threading.Thread(target=self._close_worker, name="object-close", daemon=True)
            thread.start()

    def _close_worker(self) -> None:
        deadline = time.monotonic() + self._close_seconds
        persistence_error = ""
        try:
            operation = self._operation
            if operation and operation is not threading.current_thread():
                operation.join(max(0.0, deadline - time.monotonic()))
            client = self._client
            if client and not client.terminal:
                if operation and operation.is_alive():
                    client.mark_unknown("application_close_operation_pending")
                else:
                    def stop_owned():
                        try:
                            client.stop()
                        except Exception as exc:
                            try:
                                client.mark_unknown("application_close_unconfirmed: " + str(exc))
                            except Exception as write_error:
                                with self._lock:
                                    self._emit("log", "停止未确认，且会话记录写入失败：" + str(write_error), level="ERROR")
                    stopper = threading.Thread(target=stop_owned, name="object-close-stop", daemon=True)
                    stopper.start()
                    stopper.join(max(0.0, deadline - time.monotonic()))
                    if stopper.is_alive():
                        client.mark_unknown("application_close_stop_pending")
            self._poll_thread.join(max(0.0, deadline - time.monotonic()))
        except Exception as exc:
            persistence_error = str(exc)
        finally:
            with self._lock:
                client = self._client
                self._shutdown = True
                self._busy = False
                if persistence_error:
                    self._emit("log", "恢复未确认，会话记录写入失败；保留现有租约：" + persistence_error, level="ERROR")
                self._set_bridge("disconnected" if not client or client.terminal else "unknown",
                                 "对象服务已关闭。" if not client or client.terminal else
                                 ("恢复尚未确认，会话记录写入失败；未确认的操作不会自动重试。" if persistence_error else
                                  "恢复尚未确认；已保留会话记录，未确认的操作不会自动重试。"))
