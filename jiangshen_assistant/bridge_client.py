"""Fixed bridge protocol, staged assets, and durable per-process session ownership.

The native host is launched only by an explicit connect operation. Requests are
serialized. A later explicit connection supersedes the previous connection.
"""
from __future__ import annotations

from collections import deque
from contextlib import contextmanager
from datetime import datetime, timezone
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import threading
import time
import uuid
from typing import Callable

from .mono_runtime import ProcessIdentity, process_status, JS_SHA256, MONO_SHA256, MANAGED_HASHES
from .campaign import APPLIED_LOSS_EVIDENCE, OBSERVED_LOSS_EVIDENCE, valid_settlement_evidence
from .development_recovery import decode_sc16_projection
from .battle_launch import BattleLaunchAdmission, BattleLaunchError
from .same_process_recovery import same_json

BRIDGE_ID = "jiangshen-assistant-win32-v1"
PROTOCOL = 3
CAMPAIGN_INTERFACE = "scoped-campaign-native-flow-v2"
TITLE_START_INTERFACE = 'scoped-title-start-v1'
ASSET_NAMES = ("bridge_host.exe", "bridge.dll", "JSAssistantBridge.dll")
COMMANDS = frozenset(("title-observe", "title-start", "snapshot", "diagnose", "heartbeat", "disable-restore", "stop", "campaign-observe", "campaign-command", "challenge-observe", "challenge-command", "elite-observe", "elite-command", "development-observe", "development-command", "player-state-refresh", "daily-state-refresh", "daily-observe", "daily-guild-observe", "daily-guild-command", "daily-reward-observe", "daily-reward-command", "daily-timed-observe", "daily-timed-command", "daily-stone-observe", "daily-stone-command", "daily-mingshi-observe", "daily-mingshi-command", "daily-refine-observe", "daily-refine-command", "daily-arena-observe", "daily-arena-command", "daily-soul-observe", "daily-soul-command", "daily-wood-observe", "daily-wood-command", "daily-script", "daily-popup-observe", "daily-popup-command"))
GENERAL_ACTIONS = frozenset(("general_deploy", "formation_confirm"))
CAMPAIGN_ACTIONS = frozenset(("enter", "select", "prepare", "start", "continue", "ack_result", "next_chapter")) | GENERAL_ACTIONS
HOME_NAVIGATION_ACTIONS = frozenset(('return_home', 'close_home_list', 'close_challenge_list'))
CHALLENGE_ACTIONS = (CAMPAIGN_ACTIONS - GENERAL_ACTIONS) | {'enter_challenge'} | HOME_NAVIGATION_ACTIONS
ELITE_INTERFACE = 'elite-type4-current-balance-v2'
DEVELOPMENT_OBSERVATIONS = frozenset(("growth_observe", "equipment_observe", "spirit_observe", "tribute_observe", "star_observe"))
DEVELOPMENT_ACTIONS = frozenset(("growth_open", "growth_check", "growth_claim", "growth_reward_confirm", "growth_close",
    "growth_weapon_open", "growth_weapon_wear", "growth_weapon_close",
    "growth_guide_open", "growth_soul_recruit", "growth_soul_open", "growth_soul_start", "growth_soul_grow", "growth_soul_harvest",
    "growth_artifact_open", "growth_artifact_wear", "growth_requirement_close",
    "growth_reputation_open", "growth_artifact_grant", "growth_artifact_view",
    "growth_recruit_open", "growth_recruit_right", "growth_recruit_left", "growth_recruit_choose", "growth_recruit_confirm", "growth_recruit_deploy", "growth_recruit_save",
    "equipment_open", "reinforce_open", "enchant_tab", "equipment_enchant", "equipment_close",
    "spirit_open", "spirit_train", "spirit_rankup", "spirit_rankup_confirm", "spirit_close",
    "tribute_open", "tribute_panel_open", "tribute_train", "tribute_rankup", "tribute_rankup_confirm",
    "tribute_close", "tribute_parent_close", "star_open", "star_activate", "star_reward_confirm", "star_close"))
DEVELOPMENT_FIELDS = ("action", "expected_revision", "operation_id", "target_id", "desired_level",
                      "max_requests", "max_money", "max_stone", "max_food", "mode")
GUILD_FIELDS = ("action", "expected_revision", "character_id", "guild_id", "task_id",
                "operation_id", "donation_type", "max_premium")
REWARD_FIELDS = ('action', 'expected_revision', 'character_id', 'category_id', 'operation_id')
POPUP_FIELDS = ('action', 'operation_id', 'character_id', 'window_type', 'instance_id',
                'appearance_generation', 'expected_revision')
TIMED_FIELDS = ('action', 'operation_id', 'character_id', 'task_id', 'expected_revision')
STONE_FIELDS = TIMED_FIELDS
MINGSHI_FIELDS = TIMED_FIELDS
REFINE_FIELDS = TIMED_FIELDS
ARENA_FIELDS = ('action', 'operation_id', 'character_id', 'expected_revision')
SOUL_FIELDS = (*TIMED_FIELDS, 'soul_mode', 'free_only')
WOOD_FIELDS = TIMED_FIELDS
_MAX_JSON = 4 * 1024 * 1024
_ATOMIC_REPLACE_TIMEOUT = 0.6
_ATOMIC_REPLACE_DELAY = 0.01
_REPLACE_CONTENTION_ERRORS = frozenset((5, 32, 33))
_READ_RETRY_TIMEOUT = 0.25
_READ_RETRY_DELAY = 0.01
_READ_CONTENTION_ERRORS = frozenset((32, 33))
_RECEIPT_READ_TRANSIENT_ERRORS = _READ_CONTENTION_ERRORS | {2}
_STATE_READ_RECOVERY_TIMEOUT = 5.0


class BridgeError(RuntimeError):
    pass


class BridgeUnknown(BridgeError):
    """The current request has an unconfirmed outcome."""


class BridgeAdmissionClosed(BridgeError):
    """The command was rejected locally before sequence allocation or publication."""


class BridgeBusinessError(BridgeError):
    """A matched completed command has invalid business evidence; link stays usable."""


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def _atomic_json(path: Path, data: dict) -> None:
    _atomic_bytes(path, (json.dumps(data, ensure_ascii=True, sort_keys=True) + "\n").encode("ascii"))


def _atomic_bytes(path: Path, data: bytes) -> None:
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    # Windows may reject replacement while the other endpoint briefly reads
    # this file. Legacy MoveFileEx also rejected a held target with delete
    # sharing in our own-file tests. WinError 5 can also mean permanent ACL denial, which
    # remains an error after the same short deadline. No other I/O is retried.
    # Keep the single fsynced temporary file, sequence and bytes unchanged:
    # this retries publication, never a command or game action.
    started = time.monotonic()
    deadline = started + _ATOMIC_REPLACE_TIMEOUT
    first_error = None
    attempts = 0
    while True:
        attempts += 1
        try:
            os.replace(temporary, path)
            return
        except OSError as exc:
            if getattr(exc, "winerror", None) not in _REPLACE_CONTENTION_ERRORS:
                raise
            if first_error is None:
                first_error = exc
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                first_error.publication_attempts = attempts
                first_error.publication_elapsed_ms = round((time.monotonic() - started) * 1000, 3)
                first_error.publication_stage = "atomic_replace"
                raise first_error
            time.sleep(min(_ATOMIC_REPLACE_DELAY, remaining))


@contextmanager
def _open_shared_read(path: Path):
    """Read-only handle with read/write/delete sharing for the peer writer."""
    if os.name != "nt":
        with path.open("rb") as stream:
            yield stream
        return
    import ctypes
    from ctypes import wintypes
    import msvcrt
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    handle = kernel.CreateFileW(str(path), 0x80000000, 0x1 | 0x2 | 0x4, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        error = ctypes.WinError(ctypes.get_last_error())
        error.filename = str(path)
        raise error
    descriptor = None
    try:
        descriptor = msvcrt.open_osfhandle(int(handle), os.O_RDONLY | os.O_BINARY)
        handle = None  # The CRT descriptor now owns the Windows handle.
        stream = os.fdopen(descriptor, "rb")
        descriptor = None  # The file object now owns the CRT descriptor.
        with stream:
            yield stream
    finally:
        if descriptor is not None:
            os.close(descriptor)
        if handle is not None:
            kernel.CloseHandle(handle)


def _read_json(path: Path, *, read_stats: dict | None = None,
               retry_timeout: float = _READ_RETRY_TIMEOUT, retry_missing: bool = False) -> dict | None:
    # A peer or unrelated process may briefly hold an incompatible read handle.
    # Established state/ACK callers also opt into short missing-file windows
    # during peer replacement. Archive/manifest/lease reads retain None for
    # their first missing file; no publication or business action is repeated.
    # Reopen/read only this path; no command, sequence or publication is retried.
    started = time.monotonic()
    deadline = started + retry_timeout
    first_error = None
    first_stage = ""
    last_error = None
    last_stage = ''
    attempts = 0

    def read_failure(error=None, stage=None):
        error = first_error if error is None else error
        error.read_attempts = attempts
        error.read_elapsed_ms = round((time.monotonic() - started) * 1000, 3)
        error.read_stage = first_stage if stage is None else stage
        if getattr(error, 'filename', None) is None:
            error.filename = str(path)
        for prefix, value, point in (('first', first_error, first_stage), ('last', last_error, last_stage)):
            setattr(error, 'read_' + prefix + '_stage', point)
            setattr(error, 'read_' + prefix + '_message', str(value)[:512])
            for field in ('winerror', 'errno'):
                number = getattr(value, field, None)
                if type(number) is int:
                    setattr(error, 'read_' + prefix + '_' + field, number)
        return error

    while True:
        if first_error is not None and time.monotonic() >= deadline:
            raise read_failure()
        attempts += 1
        if read_stats is not None:
            read_stats["attempts"] = attempts
        stage = "open"
        try:
            with _open_shared_read(path) as stream:
                stage = "read"
                raw = stream.read(_MAX_JSON + 1)
                stage = "close"
            break
        except OSError as error:
            last_error, last_stage = error, stage
            if first_error is None:
                if isinstance(error, FileNotFoundError) and not retry_missing:
                    return None
                first_error, first_stage = error, stage
            retry = (os.name == "nt" and stage in ("open", "read")
                     and (getattr(error, "winerror", None) in _READ_CONTENTION_ERRORS or
                          retry_missing and getattr(error, 'winerror', None) == 2))
            remaining = deadline - time.monotonic()
            if not retry and error is not first_error:
                # A permanent failure after contention must stay identifiable
                # as that failure, with the first OS error retained as cause.
                raise read_failure(error, stage) from read_failure()
            if not retry or remaining <= 0:
                if error is first_error:
                    raise read_failure()
                raise read_failure() from error
            time.sleep(min(_READ_RETRY_DELAY, remaining))
    if len(raw) > _MAX_JSON:
        raise BridgeUnknown(f"桥接回执超出大小上限：{path.name}")
    try:
        value = json.loads(raw.decode("utf-8-sig"))
    except (ValueError, UnicodeError) as exc:
        raise BridgeUnknown(f"桥接回执格式错误：{path.name}") from exc
    if not isinstance(value, dict):
        raise BridgeUnknown(f"桥接回执类型错误：{path.name}")
    return value


class _ReceiptReads:
    """Retry only unavailable receipt reads within the caller's deadline."""
    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self.failures: dict[str, dict] = {}

    def read(self, filename: str, reader, deadline: float):
        try:
            value = reader(min(_READ_RETRY_TIMEOUT, max(0.0, deadline - time.monotonic())))
        except OSError as exc:
            if (getattr(exc, 'winerror', None) not in _RECEIPT_READ_TRANSIENT_ERRORS
                    or getattr(exc, 'read_stage', None) not in ('open', 'read')
                    or getattr(exc, 'filename', None) != str(self.directory / filename)):
                raise
            failure = self.failures.get(filename)
            if failure is None:
                self.failures[filename] = dict(error=exc, attempts=exc.read_attempts,
                    started=time.monotonic() - exc.read_elapsed_ms / 1000)
            else:
                failure['attempts'] += exc.read_attempts
                for field in ('read_last_winerror', 'read_last_errno', 'read_last_stage', 'read_last_message'):
                    if hasattr(exc, field):
                        setattr(failure['error'], field, getattr(exc, field))
            return None
        self.failures.pop(filename, None)
        return value

    def raise_pending(self):
        if self.failures:
            first = min(self.failures.values(), key=lambda value: value['started'])
            error = first['error']
            error.read_attempts = first['attempts']
            error.read_elapsed_ms = round((time.monotonic() - first['started']) * 1000, 3)
            raise error


def _is_clean(state: dict) -> bool:
    if (state.get("lifecycle") != "stopped" or state.get("component_destroyed") is not True
            or state.get("reconnect_allowed") is not True):
        return False
    action = state.get("action")
    if not isinstance(action, dict):
        return False
    if action.get("mutation_attempted") is False and action.get("outcome") in ("NONE", "FAIL"):
        return True
    return (action.get("mutation_attempted") is True and action.get("restore_verified") is True
            and action.get("outcome") in ("PASS", "FAIL"))


def _character_id_valid(value: object) -> bool:
    return (isinstance(value, str) and re.fullmatch("[1-9][0-9]{0,19}", value) is not None
            and int(value) <= 2**64 - 1)


def _validate_guild_fields(fields: object) -> dict:
    if not isinstance(fields, dict) or set(fields) != set(GUILD_FIELDS):
        raise BridgeError('同盟命令参数必须匹配固定字段。')
    task, tier, premium, action = (fields[k] for k in ('task_id', 'donation_type', 'max_premium', 'action'))
    if (type(task) is not int or type(tier) is not int or type(premium) is not int
            or task not in (18, 20) or (task == 18 and tier != 0)
            or (task == 20 and tier not in (1, 2, 3)) or premium != (80 if tier == 3 else 0)):
        raise BridgeError('同盟任务或捐献档位无效。')
    allowed = {'open', 'reward_confirm', 'close'} | ({'lottery_panel', 'lottery'} if task == 18 else
        {'donate_panel', 'donate'} | ({'premium_confirm'} if tier == 3 else set()))
    if (not isinstance(action, str) or action not in allowed
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not _character_id_valid(fields['character_id']) or not _character_id_valid(fields['guild_id'])
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('同盟命令的身份、动作或版本无效。')
    return dict(fields)


def _validate_reward_fields(fields: object) -> dict:
    if not isinstance(fields, dict) or set(fields) != set(REWARD_FIELDS):
        raise BridgeError('日常宝箱命令参数必须匹配固定字段。')
    if (fields['action'] not in ('open', 'claim', 'reward_confirm', 'close')
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not _character_id_valid(fields['character_id'])
            or type(fields['category_id']) is not int or fields['category_id'] not in range(2, 7)
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('日常宝箱身份、分类或动作无效。')
    return dict(fields)


def _validate_timed_fields(fields: object) -> dict:
    if not isinstance(fields, dict) or set(fields) != set(TIMED_FIELDS):
        raise BridgeError('限时日常命令参数必须匹配固定字段。')
    if (fields['action'] not in ('open', 'participate', 'close') or not _character_id_valid(fields['character_id'])
            or type(fields['task_id']) is not int or fields['task_id'] not in (12, 13)
            or fields['action'] == 'close' and fields['task_id'] != 12
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('限时日常身份、任务或动作无效。')
    return dict(fields)


def _validate_stone_fields(fields: object) -> dict:
    if (not isinstance(fields, dict) or set(fields) != set(STONE_FIELDS)
            or fields['action'] not in ('open', 'dig', 'close')
            or not _character_id_valid(fields['character_id'])
            or type(fields['task_id']) is not int or fields['task_id'] != 16
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('采石身份、任务或动作无效。')
    return dict(fields)


def _stone_valid(value: object) -> bool:
    if (not isinstance(value, dict) or set(value) != {'schema', 'revision', 'view', 'operation'}
            or type(value['schema']) is not int or value['schema'] != 1
            or type(value['revision']) is not int or not 0 <= value['revision'] <= 999_999_999):
        return False
    view, op = value['view'], value['operation']
    if (not isinstance(view, dict) or set(view) != {'task_id', 'role_id', 'cycle', 'current', 'required',
            'silver_picks', 'gold_cost', 'silver_pick_cost', 'next_action', 'reason'}
            or type(view['task_id']) is not int or view['task_id'] != 16
            or not (view['role_id'] == '0' or _character_id_valid(view['role_id']))
            or any(type(view[k]) is not int or not 0 <= view[k] <= 2**32 - 1
                   for k in ('cycle', 'current', 'required', 'silver_picks'))
            or any(view[k] is not None and (type(view[k]) is not int or not 0 <= view[k] <= 2**32 - 1)
                   for k in ('gold_cost', 'silver_pick_cost'))
            or view['next_action'] not in ('', 'open', 'dig', 'close') or not isinstance(view['reason'], str)):
        return False
    if (not isinstance(op, dict) or set(op) != {'status', 'reason', 'operation_id', 'sequence',
            'mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed'}
            or op['status'] not in ('IDLE', 'QUEUED', 'OPENING', 'OPEN', 'PENDING', 'SETTLED', 'COMPLETED', 'REJECTED', 'UNKNOWN')
            or not isinstance(op['reason'], str) or not isinstance(op['operation_id'], str)
            or (op['operation_id'] and re.fullmatch('[0-9a-f]{32}', op['operation_id']) is None)
            or type(op['sequence']) is not int or not 0 <= op['sequence'] <= 999_999_999
            or any(type(op[k]) is not bool for k in ('mutation_attempted', 'stock_callback_returned',
                                                     'account_progress_observed', 'panel_closed'))):
        return False
    return True


def _stone_pending_busy(value: dict) -> bool:
    view, op = value['view'], value['operation']
    return (view['cycle'] == 0 and view['reason'] == 'stone_client_busy'
            and view['next_action'] == '' and op['status'] == 'PENDING'
            and op['mutation_attempted'])


def _validate_mingshi_fields(fields: object) -> dict:
    if (not isinstance(fields, dict) or set(fields) != set(MINGSHI_FIELDS)
            or fields['action'] not in ('open', 'harvest', 'close', 'cleanup')
            or not _character_id_valid(fields['character_id'])
            or type(fields['task_id']) is not int or fields['task_id'] != 7
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('铭石身份、任务或动作无效。')
    return dict(fields)


def _mingshi_valid(value: object) -> bool:
    if (not isinstance(value, dict) or set(value) != {'schema', 'revision', 'view', 'operation'}
            or type(value['schema']) is not int or value['schema'] != 1
            or type(value['revision']) is not int or not 0 <= value['revision'] <= 999_999_999):
        return False
    view, op = value['view'], value['operation']
    if (not isinstance(view, dict) or set(view) != {'task_id', 'role_id', 'cycle', 'current', 'required',
                                                   'gold_cost', 'next_action', 'reason'}
            or type(view['task_id']) is not int or view['task_id'] != 7
            or not (view['role_id'] == '0' or _character_id_valid(view['role_id']))
            or any(type(view[k]) is not int or not 0 <= view[k] <= 2**32 - 1 for k in ('cycle', 'current', 'required'))
            or view['gold_cost'] is not None and (type(view['gold_cost']) is not int or not 0 <= view['gold_cost'] <= 2**32 - 1)
            or view['next_action'] not in ('', 'open', 'harvest', 'close', 'cleanup') or not isinstance(view['reason'], str)):
        return False
    if (not isinstance(op, dict) or set(op) != {'status', 'reason', 'operation_id', 'sequence',
            'mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed'}
            or op['status'] not in ('IDLE', 'QUEUED', 'OPENING', 'OPEN', 'PENDING', 'SETTLED', 'CLOSING', 'SUCCEEDED', 'REJECTED', 'UNKNOWN')
            or not isinstance(op['reason'], str) or not isinstance(op['operation_id'], str)
            or (op['operation_id'] and re.fullmatch('[0-9a-f]{32}', op['operation_id']) is None)
            or type(op['sequence']) is not int or not 0 <= op['sequence'] <= 999_999_999
            or any(type(op[k]) is not bool for k in ('mutation_attempted', 'stock_callback_returned',
                                                     'account_progress_observed', 'panel_closed'))):
        return False
    return True


def _validate_refine_fields(fields: object) -> dict:
    if (not isinstance(fields, dict) or set(fields) != set(REFINE_FIELDS)
            or fields['action'] not in ('open', 'experience', 'refine', 'close')
            or not _character_id_valid(fields['character_id'])
            or type(fields['task_id']) is not int or fields['task_id'] != 21
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('炼魂身份、任务或动作无效。')
    return dict(fields)


def _refine_valid(value: object) -> bool:
    if (not isinstance(value, dict) or set(value) != {'schema', 'revision', 'view', 'operation'}
            or type(value['schema']) is not int or value['schema'] != 1
            or type(value['revision']) is not int or not 0 <= value['revision'] <= 999_999_999):
        return False
    view, op = value['view'], value['operation']
    view_fields = {'task_id', 'role_id', 'cycle', 'current', 'required', 'money', 'money_cost',
                   'coin_exchanges', 'gold_cost', 'next_action', 'reason'}
    experience_fields = {'mode', 'experience', 'experience_exchanges', 'gold_exchanges', 'experience_cost'}
    quota_fields = {'practice', 'experience_limit', 'remaining_exchanges'}
    if (not isinstance(view, dict) or set(view) - {'continuation_reason'} not in (view_fields, view_fields | experience_fields,
                                                         view_fields | experience_fields | quota_fields)
            or type(view['task_id']) is not int or view['task_id'] != 21
            or not (view['role_id'] == '0' or _character_id_valid(view['role_id']))
            or any(type(view[k]) is not int or not 0 <= view[k] <= 2**32 - 1 for k in ('cycle', 'current', 'required', 'coin_exchanges'))
            or type(view['money']) is not int or not 0 <= view['money'] < 2**63
            or any(view[k] is not None and (type(view[k]) is not int or not 0 <= view[k] <= 2**31 - 1)
                   for k in ('money_cost', 'gold_cost'))
            or view['next_action'] not in ('', 'open', 'experience', 'refine', 'close') or not isinstance(view['reason'], str)
            or not isinstance(view.get('continuation_reason', ''), str)):
        return False
    if 'mode' in view:
        if (view['mode'] not in ('coin', 'experience')
                or any(type(view[k]) is not int or not 0 <= view[k] <= 2**31 - 1
                       for k in ('experience', 'experience_exchanges', 'gold_exchanges'))
                or view['experience_exchanges'] + view['gold_exchanges'] > 10
                or view['experience_cost'] is not None and (type(view['experience_cost']) is not int
                    or not 0 <= view['experience_cost'] <= 2**31 - 1)
                or view['mode'] == 'experience' and view['money_cost'] not in (None, 0)
                or view['mode'] == 'coin' and view['experience_cost'] is not None):
            return False
    if 'practice' in view:
        if (type(view['practice']) is not int or not 0 <= view['practice'] <= 2**31 - 1
                or type(view['experience_limit']) is not int or view['experience_limit'] != 10
                or type(view['remaining_exchanges']) is not int
                or view['remaining_exchanges'] != 10 - view['experience_exchanges'] - view['gold_exchanges']):
            return False
    op_fields = {'status', 'reason', 'operation_id', 'sequence', 'mutation_attempted', 'stock_callback_returned',
                 'account_progress_observed', 'panel_closed', 'ui_ownership_held', 'practice_credit', 'coins_spent'}
    if (not isinstance(op, dict) or set(op) not in (op_fields, op_fields | {'mode', 'experience_spent'})
            or op['status'] not in ('IDLE', 'QUEUED', 'OPENING', 'OPEN', 'PENDING', 'SETTLED', 'COMPLETED', 'REJECTED', 'UNKNOWN')
            or not isinstance(op['reason'], str) or not isinstance(op['operation_id'], str)
            or (op['operation_id'] and re.fullmatch('[0-9a-f]{32}', op['operation_id']) is None)
            or type(op['sequence']) is not int or not 0 <= op['sequence'] <= 999_999_999
            or any(type(op[k]) is not bool for k in ('mutation_attempted', 'stock_callback_returned',
                'account_progress_observed', 'panel_closed', 'ui_ownership_held'))
            or any(type(op[k]) is not int or not 0 <= op[k] <= 2**31 - 1 for k in ('practice_credit', 'coins_spent'))):
        return False
    if ('mode' in view) != ('mode' in op):
        return False
    if 'mode' in op:
        if (op['mode'] not in ('coin', 'experience') or type(op['experience_spent']) is not int
                or not 0 <= op['experience_spent'] <= 2**31 - 1
                or op['mode'] == 'coin' and op['experience_spent'] != 0
                or op['mode'] == 'experience' and op['coins_spent'] != 0
                or not op['account_progress_observed'] and (op['experience_spent'] or op['coins_spent'])):
            return False
    return True


def _refine_busy(value: dict) -> bool:
    view, op = value['view'], value['operation']
    return (view['cycle'] == 0 and view['reason'] == 'refine_client_busy'
            and view['next_action'] == '' and (op['status'] in ('IDLE', 'OPENING', 'OPEN', 'PENDING', 'SETTLED')
                or op['status'] == 'COMPLETED' and op['panel_closed'] and not op['ui_ownership_held']))


def _validate_arena_fields(fields: object) -> dict:
    if (not isinstance(fields, dict) or set(fields) != set(ARENA_FIELDS)
            or fields['action'] not in ('open', 'enter', 'open_chest', 'claim', 'reward_confirm', 'close', 'rearm')
            or not _character_id_valid(fields['character_id'])
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('竞技宝箱身份或动作无效。')
    return dict(fields)


def _arena_valid(value: object) -> bool:
    # Lazy import avoids a module cycle; the executor is loaded before a
    # connection can read an enabled arena projection.
    from .daily_arena_chest import _valid_snapshot
    return _valid_snapshot(value)


def _validate_soul_fields(fields: object) -> dict:
    if (not isinstance(fields, dict) or set(fields) != set(SOUL_FIELDS)
            or fields['action'] not in ('open', 'start', 'grow', 'harvest', 'close', 'next', 'adopt')
            or type(fields['soul_mode']) is not int or fields['soul_mode'] not in (1, 2)
            or type(fields['free_only']) is not int or fields['free_only'] not in (0, 1)
            or not _character_id_valid(fields['character_id'])
            or type(fields['task_id']) is not int or fields['task_id'] != 5
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('聚魂身份、任务或动作无效。')
    return dict(fields)


def _soul_valid(value: object) -> bool:
    from .daily_soul import _snapshot_error
    return not _snapshot_error(value)


def _soul_command_admitted(value: object, fields: dict) -> bool:
    from .daily_soul import _valid, _growth_projection
    if (not _valid(value) or value['revision'] != fields['expected_revision']
            or value['character_id'] != fields['character_id']):
        return False
    action, op = fields['action'], value['operation']
    if action == 'adopt':
        return (value['action'] == 'adopt' and not value['reason']
                and (not op['operation_id'] or op['status'] in ('COMPLETED', 'REJECTED'))
                and op['operation_id'] != fields['operation_id']
                and value['selected_mode'] == fields['soul_mode']
                and any(slot['color'] != 0 or slot['level'] != 0 for slot in value['board']))
    if action in ('open', 'next'):
        quote = value['quotes']['blue' if fields['soul_mode'] == 1 else 'purple']
        if not quote['known'] or quote['start_available'] is not True:
            return False
        if action == 'open':
            return value['action'] == 'open' and not value['reason']
    if (op['operation_id'] != fields['operation_id']
            or action != 'next' and (op['mode'] != fields['soul_mode'] or op['free_only'] != bool(fields['free_only']))):
        return False
    if action == 'next':
        return value['action'] == 'close' and op['harvest_verified']
    return ((value['action'] == action or action == 'harvest' and value['action'] == 'grow')
            and (action != 'grow' or _growth_projection(value)))


def _validate_wood_fields(fields: object) -> dict:
    from .daily_wood import ACTIONS
    if (not isinstance(fields, dict) or set(fields) != set(WOOD_FIELDS)
            or fields['action'] not in ACTIONS or not _character_id_valid(fields['character_id'])
            or type(fields['task_id']) is not int or fields['task_id'] != 22
            or type(fields['expected_revision']) is not int or not 1 <= fields['expected_revision'] <= 999_999_999
            or not isinstance(fields['operation_id'], str)
            or re.fullmatch('[0-9a-f]{32}', fields['operation_id']) is None):
        raise BridgeError('木人试炼身份、任务或动作无效。')
    return dict(fields)


def _wood_valid(value: object) -> bool:
    from .daily_wood import valid_snapshot
    return valid_snapshot(value)


def _wood_receipt_valid(result, state, fields=None, sequence=None):
    from .daily_wood import echo_matches, follows, invocation_matches
    observed, current = result.get('daily_wood'), state.get('daily_wood')
    if (result.get('daily_wood_enabled') is not True or state.get('daily_wood_enabled') is not True
            or not _wood_valid(observed) or not _wood_valid(current)):
        return False
    # During read-only admission, a newer idle projection can resolve the
    # role or refresh proof without any operation to correlate.
    idle_unbound = (fields is None and current['revision'] > observed['revision']
                    and all(s['status'] == 'IDLE' and s['operation_id'] == '' and s['next_action'] in ('', 'open')
                            and not any(s[key] for key in ('mutation_attempted', 'task_confirmed', 'cleanup_pending', 'busy'))
                            for s in (observed, current)))
    if not idle_unbound and not follows(observed, current):
        return False
    return fields is None or (
        observed['role_id'] == fields['character_id'] and observed['operation_id'] == fields['operation_id']
        and observed['revision'] > fields['expected_revision']
        and invocation_matches(result.get('campaign_gui'), sequence)
        and invocation_matches(state.get('campaign_gui'), sequence)
        and echo_matches(result.get('daily_wood_command'), fields, sequence))


def _timed_valid(value: object) -> bool:
    if not isinstance(value, dict) or set(value) != {'schema', 'revision', 'character_id', 'tasks', 'operation'}:
        return False
    if (type(value['schema']) is not int or value['schema'] != 1
            or type(value['revision']) is not int or not 0 <= value['revision'] <= 999_999_999
            or not (value['character_id'] == '0' or _character_id_valid(value['character_id']))
            or not isinstance(value['tasks'], dict) or set(value['tasks']) != {'12', '13'}):
        return False
    for task_key, row in value['tasks'].items():
        if (not isinstance(row, dict) or set(row) != {'action', 'reason', 'current', 'required'}
                or row['action'] not in ('', 'open', 'participate', 'close')
                or task_key == '13' and row['action'] == 'close' or not isinstance(row['reason'], str)
                or any(row[key] is not None and (type(row[key]) is not int or not 0 <= row[key] <= 255)
                       for key in ('current', 'required'))):
            return False
    op = value['operation']
    if (not isinstance(op, dict) or set(op) != {'operation_id', 'task_id', 'sequence', 'stage', 'status',
            'invoked', 'opcode', 'reason', 'request_attribution', 'counter_before', 'counter_after'}
            or not isinstance(op['operation_id'], str) or (op['operation_id'] and re.fullmatch('[0-9a-f]{32}', op['operation_id']) is None)
            or type(op['task_id']) is not int or op['task_id'] not in (0, 12, 13)
            or type(op['sequence']) is not int or not 0 <= op['sequence'] <= 999_999_999
            or op['stage'] not in ('', 'open', 'participate', 'close')
            or op['status'] not in ('IDLE', 'QUEUED', 'OPENING', 'READY', 'PENDING', 'CLOSING', 'SUCCEEDED', 'REJECTED', 'UNKNOWN')
            or type(op['invoked']) is not bool or type(op['opcode']) is not int or op['opcode'] not in (0, 312, 321, 1829)
            or not isinstance(op['reason'], str) or op['request_attribution'] not in (None, 'UNKNOWN')
            or any(op[key] is not None and (type(op[key]) is not int or not 0 <= op[key] <= 255)
                   for key in ('counter_before', 'counter_after'))):
        return False
    return True


def _reward_valid(value: object) -> bool:
    required = {'schema', 'revision', 'status', 'reason', 'next_action', 'character_id', 'operation_id',
                'sequence', 'category_id', 'claim_sequence', 'refresh_epoch', 'invoked', 'unresolved',
                'callback_returned', 'settled', 'reward_closed', 'receipt_category', 'receipt_refresh', 'categories'}
    if not isinstance(value, dict) or set(value) != required:
        return False
    if (type(value['schema']) is not int or value['schema'] != 1
            or type(value['revision']) is not int or not 0 <= value['revision'] <= 999_999_999
            or value['status'] not in ('IDLE', 'BLOCKED', 'READY', 'WAITING', 'CLOSING', 'COMPLETED', 'REJECTED', 'UNKNOWN', 'STOPPED')
            or not isinstance(value['reason'], str) or value['next_action'] not in ('', 'open', 'claim', 'reward_confirm', 'close')
            or not isinstance(value['character_id'], str) or (value['character_id'] and not _character_id_valid(value['character_id']))
            or not isinstance(value['operation_id'], str) or (value['operation_id'] and re.fullmatch('[0-9a-f]{32}', value['operation_id']) is None)
            or any(type(value[key]) is not bool for key in ('invoked', 'unresolved', 'callback_returned', 'settled', 'reward_closed'))
            or any(type(value[key]) is not int or not 0 <= value[key] <= 999_999_999 for key in ('sequence', 'claim_sequence'))
            or any(type(value[key]) is not int or not 0 <= value[key] <= 2**32 - 1 for key in ('refresh_epoch', 'receipt_refresh'))
            or type(value['category_id']) is not int or value['category_id'] not in (0, 2, 3, 4, 5, 6)
            or type(value['receipt_category']) is not int or value['receipt_category'] not in (0, 2, 3, 4, 5, 6)
            or not isinstance(value['categories'], list) or len(value['categories']) != 5):
        return False
    for category, expected in zip(value['categories'], range(2, 7)):
        if (not isinstance(category, dict) or set(category) != {'id', 'ready', 'claimed', 'reason'}
                or type(category['id']) is not int or category['id'] != expected
                or type(category['ready']) is not bool or not (category['claimed'] is None or type(category['claimed']) is bool)
                or not isinstance(category['reason'], str)):
            return False
    return True


def _guild_valid(value: object) -> bool:
    required = {'schema', 'revision', 'character_id', 'guild_id', 'phase', 'gate', 'reason', 'actions', 'operation'}
    if (not isinstance(value, dict) or not required <= set(value)
            or set(value) - required - {'retry_unstarted'}
            or 'retry_unstarted' in value and type(value['retry_unstarted']) is not bool):
        return False
    if (type(value['schema']) is not int or value['schema'] != 1 or type(value['revision']) is not int
            or not 1 <= value['revision'] <= 999_999_999
            or not all(v == '0' or _character_id_valid(v) for v in (value['character_id'], value['guild_id']))
            or value['phase'] not in ('MAIN', 'GUILD', 'DONATE', 'LOTTERY', 'PREMIUM', 'REWARD', 'WAITING')
            or value['gate'] not in ('PASS', 'BLOCKED', 'WAITING_UI', 'WAITING_NETWORK', 'UNKNOWN')
            or not isinstance(value['reason'], str) or not isinstance(value['actions'], list)
            or any(not isinstance(action, str) for action in value['actions'])
            or len(set(value['actions'])) != len(value['actions'])
            or any(action not in {'open', 'donate_panel', 'lottery_panel', 'donate', 'lottery',
                                  'premium_confirm', 'reward_confirm', 'close'} for action in value['actions'])):
        return False
    op = value['operation']
    if op is None:
        return True
    op_required = {'operation_id', 'task_id', 'donation_type', 'max_premium', 'status', 'invoked', 'closed',
                   'receipt_opcode', 'receipt_result', 'progress_before', 'progress_after', 'reason'}
    op_flags = {'request_issued', 'reply_received', 'page_confirmed', 'processed_without_reply', 'wrappers_released'}
    if (not isinstance(op, dict) or not op_required <= set(op) or set(op) - op_required - op_flags - {'observed_progress'}
            or any(type(op[name]) is not bool for name in op_flags if name in op)
            or 'observed_progress' in op and op['observed_progress'] is not None
                and (type(op['observed_progress']) is not int or not 0 <= op['observed_progress'] <= 255)):
        return False
    if op.get('status') == 'PROCESSED' and not (op.get('processed_without_reply') is True
            and op.get('request_issued') is True and op.get('reply_received') is False):
        return False
    try:
        _validate_guild_fields({'action': 'open', 'expected_revision': value['revision'],
            'character_id': value['character_id'], 'guild_id': value['guild_id'],
            **{key: op[key] for key in ('task_id', 'donation_type', 'operation_id', 'max_premium')}})
    except BridgeError:
        return False
    return (op['status'] in ('OPEN', 'PENDING', 'COMPLETED', 'PROCESSED', 'REJECTED', 'UNKNOWN')
            and type(op['invoked']) is bool and type(op['closed']) is bool
            and type(op['receipt_opcode']) is int and op['receipt_opcode'] in (0, 301, 302)
            and (op['receipt_result'] is None or type(op['receipt_result']) is int and 0 <= op['receipt_result'] <= 2**31 - 1)
            and all(op[key] is None or type(op[key]) is int and 0 <= op[key] <= 255
                    for key in ('progress_before', 'progress_after'))
            and isinstance(op['reason'], str))


def _divinity_home_return_matches(state, fields):
    """The sole zero-campaign return is an observed idle Divinity adapter."""
    if (not isinstance(fields, dict) or fields.get('action') != 'return_home'
            or fields.get('campaign_id') != 0 or fields.get('troop_id') != 0
            or fields.get('attempt_id') != ''):
        return False
    snap = state.get('challenge') or {}
    navigation = snap.get('home_navigation') or {}
    return (_challenge_valid(snap) and _home_navigation_valid(navigation)
            and snap.get('revision') == fields.get('expected_revision')
            and snap.get('campaign_id') == 0
            and (state.get('snapshot') or {}).get('root_level_id') == 'Divinity'
            and navigation.get('gate') == 'PASS' and navigation.get('divinity_return') is not None
            and (snap.get('attempt') or {}).get('status') in ('NONE', 'SETTLED'))


def _validate_campaign_command_fields(fields: object, *, challenge: bool = False, elite: bool = False,
                                      divinity_return: bool = False) -> dict:
    required = {"action", "expected_revision", "campaign_id", "troop_id", "attempt_id"} | ({'star', 'daily_target'} if elite else set())
    if not isinstance(fields, dict) or set(fields) != required:
        raise BridgeError("征战命令参数必须匹配固定字段。")
    action = fields["action"]
    if not isinstance(action, str) or action not in (CHALLENGE_ACTIONS if challenge or elite else CAMPAIGN_ACTIONS):
        raise BridgeError("征战动作不在固定允许列表中。")
    for key, minimum, maximum in (("expected_revision", 1, 999_999_999), ("campaign_id", 0, 65535), ("troop_id", 0, 255)):
        value = fields[key]
        if type(value) is not int or not minimum <= value <= maximum:
            raise BridgeError(f"征战参数无效：{key}")
    attempt_id = fields["attempt_id"]
    if not isinstance(attempt_id, str) or (not re.fullmatch("[0-9a-f]{32}", attempt_id) if action == "start" else attempt_id != ""):
        raise BridgeError("征战攻击标识无效。")
    if elite and (type(fields['star']) is not int or fields['star'] not in ((0, 1, 2, 3) if action == 'enter' or action in HOME_NAVIGATION_ACTIONS else (1, 2, 3))
                  or type(fields['daily_target']) is not int or not 1 <= fields['daily_target'] <= 10):
        raise BridgeError('星级挑战必须指定真实星级与不超过 10 的日目标。')
    if action in HOME_NAVIGATION_ACTIONS:
        zero_return = (divinity_return and challenge and not elite and action == 'return_home'
                       and fields['campaign_id'] == 0)
        if ((fields['campaign_id'] != 0 if action == 'close_challenge_list' else
             fields['campaign_id'] == 0 and not zero_return) or fields['troop_id'] != 0):
            raise BridgeError('返回主界面必须绑定原地图，不接受战斗目标。')
    elif (challenge or elite) and action in ('enter', 'enter_challenge'):
        if fields['troop_id'] != 0 or (fields['campaign_id'] != 0 if action == 'enter' else fields['campaign_id'] == 0):
            raise BridgeError('挑战入口必须匹配当前列表目标。')
    elif action in GENERAL_ACTIONS:
        if fields["campaign_id"] != 0 or fields["troop_id"] != 0:
            raise BridgeError("教学动作使用当前已绑定界面，不接受征战目标。")
    elif action != "enter" and (fields["campaign_id"] == 0 or (action not in {"continue", "next_chapter"} and fields["troop_id"] == 0)):
        raise BridgeError("征战动作缺少已观察到的目标。")
    return dict(fields)


def _validate_development_fields(fields: object, *, observe: bool = False, refresh: bool = False,
                                 daily_refresh: bool = False) -> dict:
    star = isinstance(fields, dict) and type(fields.get('action')) is str and fields['action'].startswith('star_')
    expected = set(DEVELOPMENT_FIELDS) | ({'max_practice'} if star else set())
    if not isinstance(fields, dict) or set(fields) != expected:
        raise BridgeError("养成命令参数必须匹配固定字段。")
    refresh = refresh or daily_refresh
    allowed = ({'daily_state_refresh'} if daily_refresh else {'player_state_refresh'}) if refresh else DEVELOPMENT_OBSERVATIONS if observe else DEVELOPMENT_ACTIONS
    if type(fields["action"]) is not str or fields["action"] not in allowed:
        raise BridgeError("养成动作不在固定允许列表中。")
    request_limit = 196 if star else 2**31 - 1 if fields['action'].startswith('tribute_') else 100
    for key, low, high in (("expected_revision", 1, 999_999_999), ("target_id", 0, 32767),
            ("desired_level", 0, 255), ("max_requests", 0 if refresh else 1, 0 if refresh else request_limit),
            ("max_money", 0, 2**31 - 1), ("max_stone", 0, 2**31 - 1), ("max_food", 0, 2**31 - 1)):
        if not _development_int(fields[key], low, high):
            raise BridgeError(f"养成参数无效：{key}")
    if (type(fields["operation_id"]) is not str or not re.fullmatch("[0-9a-f]{32}", fields["operation_id"])
            or type(fields["mode"]) is not str or fields["mode"] not in {"", "enchant", "evolve", "fruit", "practice"}):
        raise BridgeError("养成操作标识或模式无效。")
    if refresh and (fields["mode"] != "" or any(fields[key] != 0 for key in
            ("target_id", "desired_level", "max_requests", "max_money", "max_stone", "max_food"))):
        raise BridgeError("状态刷新不接受培养目标或资源预算。")
    if fields['action'].startswith('tribute_') and (fields['mode'] != 'fruit' or
            fields['desired_level'] > 100 or
            any(fields[key] != 0 for key in ('target_id', 'max_money', 'max_stone'))):
        raise BridgeError('灵兽上贡仅接受桃子模式、目标阶数和次数上限，不接受元宝消耗。')
    if star and (fields['mode'] != 'practice' or
            not _development_int(fields['max_practice'], 0, 2147483647) or
            any(fields[key] != 0 for key in ('target_id', 'desired_level', 'max_money', 'max_stone', 'max_food'))):
        raise BridgeError('星灵激活仅接受战魂预算，禁止购买或元宝消耗。')
    if not star and fields['mode'] == 'practice':
        raise BridgeError('战魂模式仅适用于星灵激活。')
    return dict(fields)


def _daily_refresh_valid(value: object) -> bool:
    required = {'schema', 'status', 'reason', 'operation_id', 'sequence', 'expected_revision',
                'character_id', 'opcode', 'invoked', 'callback_returned', 'context_rebuilt',
                'server_time', 'server_cycle', 'receipt_hash'}
    if not isinstance(value, dict) or set(value) != required:
        return False
    if (type(value['schema']) is not int or value['schema'] != 1
            or value['status'] not in ('IDLE', 'QUEUED', 'WAITING', 'UNKNOWN', 'REJECTED', 'BLOCKED', 'STOPPED', 'OBSERVED')
            or not isinstance(value['reason'], str) or type(value['opcode']) is not int or value['opcode'] != 16
            or any(type(value[key]) is not bool for key in ('invoked', 'callback_returned', 'context_rebuilt'))
            or not _development_int(value['sequence'], 0, 999_999_999)
            or not _development_int(value['expected_revision'], 0, 999_999_999)
            or not isinstance(value['operation_id'], str) or not isinstance(value['character_id'], str)
            or not isinstance(value['receipt_hash'], str)):
        return False
    if value['operation_id'] and re.fullmatch('[0-9a-f]{32}', value['operation_id']) is None:
        return False
    if value['character_id'] and not _character_id_valid(value['character_id']):
        return False
    if value['receipt_hash'] and re.fullmatch('[0-9a-f]{64}', value['receipt_hash']) is None:
        return False
    if any(value[key] is not None and not _development_int(value[key], 0, 2**32 - 1)
           for key in ('server_time', 'server_cycle')):
        return False
    if value['status'] == 'OBSERVED':
        return (bool(value['operation_id']) and value['sequence'] > 0 and value['expected_revision'] > 0
                and _character_id_valid(value['character_id']) and value['invoked'] is True
                and value['callback_returned'] is True and value['context_rebuilt'] is True
                and value['server_time'] is not None and value['server_cycle'] is not None
                and value['server_cycle'] <= value['server_time'] < value['server_cycle'] + 86400
                and bool(value['receipt_hash']))
    return True


def _player_refresh_valid(value: object) -> bool:
    fields = "schema status reason operation_id sequence expected_revision character_id opcode mutation_attempted receipt_capture receipt_hash callback_returned server_snapshot context_rebuilt root_level main_ready normal_responses_idle outbound_transport_completion"
    if (not _development_shape(value, fields) or type(value["schema"]) is not int or value["schema"] != 1
            or value["status"] not in {"IDLE", "QUEUED", "WAITING", "UNKNOWN", "REJECTED", "BLOCKED", "STOPPED", "OBSERVED"}
            or type(value["reason"]) is not str or value["opcode"] != 16 or type(value["opcode"]) is not int
            or any(type(value[key]) is not bool for key in ("mutation_attempted", "callback_returned", "context_rebuilt", "main_ready", "normal_responses_idle"))
            or value["outbound_transport_completion"] != "UNVERIFIED" or type(value["root_level"]) is not str
            or not _development_int(value["sequence"], 0, 999_999_999) or not _development_int(value["expected_revision"], 0, 999_999_999)
            or value["operation_id"] != "" and (type(value["operation_id"]) is not str or not re.fullmatch("[0-9a-f]{32}", value["operation_id"]))
            or value["character_id"] != "" and not _character_id_valid(value["character_id"])):
        return False
    capture, raw = value["receipt_capture"], None
    try:
        if capture is not None:
            if (not _development_shape(capture, "encoding bytes data") or capture["encoding"] != "base64"
                    or not _development_int(capture["bytes"], 1, 1048576) or type(capture["data"]) is not str
                    or len(capture["data"]) > 1398104):
                return False
            raw = base64.b64decode(capture["data"], validate=True)
            if len(raw) != capture["bytes"] or hashlib.sha256(raw).hexdigest() != value["receipt_hash"]:
                return False
        elif value["receipt_hash"] != "":
            return False
        server = value["server_snapshot"]
        if server is not None:
            if raw is None or json.dumps(decode_sc16_projection(raw), sort_keys=True) != json.dumps(server, sort_keys=True):
                return False
        if value["status"] == "OBSERVED":
            return (value["operation_id"] != "" and value["sequence"] > 0 and value["expected_revision"] > 0
                    and _character_id_valid(value["character_id"]) and value["mutation_attempted"] is True
                    and all(value[key] is True for key in ("callback_returned", "context_rebuilt", "main_ready", "normal_responses_idle"))
                    and value["root_level"] in ("Campaign", "Capital") and type(server) is dict
                    and server.get("character_id") == value["character_id"])
        return True
    except (ValueError, TypeError, KeyError, OverflowError, RecursionError):
        return False


def _development_int(value: object, low: int = 0, high: int = 2**63 - 1) -> bool:
    return type(value) is int and low <= value <= high


def _development_shape(value: object, fields: str) -> bool:
    return type(value) is dict and set(value) == set(fields.split())


def _development_operation_valid(value: object) -> bool:
    fields = "status action operation_id sequence expected_revision target_id desired_level mode opcode server reason requests mutation_attempted receipt_result receipt_opcode receipt_id receipt_level receipt_experience receipt_food receipt_gold server_progress_observed result_semantics"
    if not (_development_shape(value, fields) or _development_shape(value, fields + ' receipt_practice')):
        return False
    if (value["status"] not in {"IDLE", "WAITING", "OBSERVED", "UNKNOWN", "ERROR"}
            or value["action"] not in DEVELOPMENT_ACTIONS | {""}
            or value["mode"] not in {"", "enchant", "evolve", "fruit", "practice"}
            or value["server"] not in {"UNVERIFIED", "PASS", "FAIL"}
            or value["result_semantics"] not in {"UNVERIFIED", "CONFIRMED"}
            or type(value["reason"]) is not str
            or type(value["mutation_attempted"]) is not bool or type(value["server_progress_observed"]) is not bool):
        return False
    if type(value["operation_id"]) is not str:
        return False
    idle = value["status"] == "IDLE"
    if idle:
        if value["operation_id"] != "" or value["action"] != "" or value["sequence"] != 0 or value["expected_revision"] != 0:
            return False
    elif (not re.fullmatch("[0-9a-f]{32}", value["operation_id"]) or not value["action"]
            or not _development_int(value["sequence"], 1, 999_999_999)
            or not _development_int(value["expected_revision"], 1, 999_999_999)):
        return False
    ranges = {"sequence": (0, 999_999_999), "expected_revision": (0, 999_999_999), "target_id": (0, 32767),
              "desired_level": (0, 255), "requests": (0, 196 if value['action'].startswith('star_') else 2**31 - 1 if value['action'].startswith('tribute_') else 100), "receipt_id": (0, 32767), "receipt_level": (0, 255),
              "receipt_experience": (0, 2**32 - 1), "receipt_food": (0, 2**63 - 1), "receipt_gold": (0, 2**63 - 1)}
    if any(not _development_int(value[key], *bounds) for key, bounds in ranges.items()):
        return False
    if 'receipt_practice' in value and (value['opcode'] != 514 or not _development_int(value['receipt_practice'], 0, 2147483647)):
        return False
    if value['opcode'] == 514 and 'receipt_practice' not in value:
        return False
    return (all(type(value[key]) is int and value[key] in {0, 298, 305, 514, 517, 518, 519, 818, 856, 863, 810, 811, 1104, 1136, 1139} for key in ("opcode", "receipt_opcode"))
            and (value["receipt_result"] is None or _development_int(value["receipt_result"], 0, 2**32 - 1)))


def _development_ui_valid(value: object) -> bool:
    return (_development_shape(value, "instance_id visible enabled")
            and _development_int(value["instance_id"], -(2**31), 2**31 - 1)
            and type(value["visible"]) is bool and type(value["enabled"]) is bool)


def _growth_requirement_valid(value: object) -> bool:
    if value is None:
        return True
    weapon_fields = ('quest_id type param0 param1 phase stage equipment_id owner_id slot '
                     'in_current_formation slot_empty candidate_unowned owner_slot_matches '
                     'available_money available_stone gold wear_receipt ui')
    if _development_shape(value, weapon_fields):
        return (tuple(value[key] for key in ('quest_id', 'type', 'param0', 'param1')) == (3, 3, 5, 0)
                and all(type(value[key]) is int for key in ('quest_id', 'type', 'param0', 'param1', 'owner_id', 'slot'))
                and value['owner_id'] == value['slot'] == 0
                and value['phase'] in ('READY', 'WAITING', 'DONE', 'BLOCKED')
                and _development_int(value['stage'], 0, 2) and _development_int(value['equipment_id'], 0, 255)
                and all(type(value[key]) is bool for key in ('in_current_formation', 'slot_empty', 'candidate_unowned', 'owner_slot_matches'))
                and all(_development_int(value[key]) for key in ('available_money', 'available_stone', 'gold'))
                and (value['wear_receipt'] is None or _development_operation_valid(value['wear_receipt']))
                and _development_shape(value['ui'], 'equipment') and _development_ui_valid(value['ui']['equipment']))
    fields = ('quest_id type param0 param1 phase stage artifact_id owner_id used_times free_times rate grow_cost mode '
              'available_money gold start_receipt grow_receipt harvest_receipt finish_receipt feature_unlocked reputation_unlocked reputation_level ui')
    grant_fields = ' reputation grant_stage grant_receipt'
    recruit_fields = ' blue_souls general_count repair_stage recruit_receipt formation_receipt recruit_tutorial_status'
    shape = (_development_shape(value, fields) or _development_shape(value, fields + grant_fields)
             or _development_shape(value, fields + grant_fields + recruit_fields))
    return (shape and _development_int(value['quest_id'], 1, 32767)
            and value['type'] in (4, 5, 6) and type(value['type']) is int
            and all(_development_int(value[key], -(2**31), 2**31 - 1) for key in ('param0', 'param1'))
            and value['phase'] in ('READY', 'WAITING', 'DONE', 'BLOCKED')
            and _development_int(value['stage'], 0, 4) and _development_int(value['artifact_id'], 0, 255)
            and type(value['owner_id']) is int and value['owner_id'] == 0
            and all(_development_int(value[key]) for key in ('used_times', 'free_times', 'rate', 'grow_cost', 'mode', 'available_money', 'gold'))
            and type(value['feature_unlocked']) is bool and type(value['reputation_unlocked']) is bool
            and _development_int(value['reputation_level'], 0, 65535)
            and all(value[key] is None or _development_operation_valid(value[key])
                    for key in ('start_receipt', 'grow_receipt', 'harvest_receipt', 'finish_receipt'))
            and ('grant_receipt' not in value or (_development_int(value['reputation']) and _development_int(value['grant_stage'], 0, 2)
                and (value['grant_receipt'] is None or _development_operation_valid(value['grant_receipt']))))
            and ('recruit_receipt' not in value or (_development_int(value['blue_souls']) and _development_int(value['general_count'], 0, 512)
                and _development_int(value['repair_stage'], 0, 2)
                and value['recruit_tutorial_status'] in ('NONE', 'DEPLOY', 'WAITING_FORMATION', 'FORMATION_CONFIRM', 'WAITING_CLOSE', 'COMPLETE', 'UNKNOWN')
                and all(value[key] is None or _development_operation_valid(value[key]) for key in ('recruit_receipt', 'formation_receipt'))))
            and (_development_shape(value['ui'], 'recruit soul equipment') or _development_shape(value['ui'], 'recruit soul equipment reputation special')
                 or _development_shape(value['ui'], 'recruit soul equipment reputation special dialog formation'))
            and all(_development_ui_valid(panel) for panel in value['ui'].values()))


def _growth_reward_wait_valid(value: object) -> bool:
    fields = ('phase reason claim_confirmed bound quest_id claim_operation_id claim_sequence '
              'dialog_instance_id loading collecting asset_names_count show_visible_pending')
    if (not _development_shape(value, fields) or
            value['phase'] not in {'NONE', 'LOADING', 'READY', 'BLOCKED'} or
            type(value['reason']) is not str or
            any(type(value[key]) is not bool for key in ('claim_confirmed', 'bound')) or
            not _development_int(value['quest_id'], 0, 32767) or
            not _development_int(value['claim_sequence'], 0, 999_999_999) or
            not _development_int(value['dialog_instance_id'], -(2**31), 2**31 - 1) or
            type(value['claim_operation_id']) is not str or
            value['claim_operation_id'] != '' and re.fullmatch('[0-9a-f]{32}', value['claim_operation_id']) is None or
            any(value[key] is not None and type(value[key]) is not bool
                for key in ('loading', 'collecting', 'show_visible_pending')) or
            value['asset_names_count'] is not None and not _development_int(value['asset_names_count'], 0, 4096)):
        return False
    if value['phase'] in {'LOADING', 'READY'}:
        if (value['claim_confirmed'] is not True or value['bound'] is not True or
                value['quest_id'] == 0 or value['claim_operation_id'] == '' or value['claim_sequence'] == 0 or
                value['dialog_instance_id'] == 0 or value['collecting'] is not False):
            return False
        if value['phase'] == 'LOADING':
            return (value['loading'] is True and value['show_visible_pending'] is True and
                    value['asset_names_count'] is not None and value['asset_names_count'] > 0)
        return value['loading'] is False
    return True


def _development_sample_valid(value: object, kind: str) -> bool:
    common = "schema gate reason available_action operation "
    extras = {"growth": "selected_quest_id ui_selected_quest_id window_visible window_enabled show_window step tasks",
              "equipment": "target_id desired_level mode available_money available_stone candidates ui",
              "spirit": "level experience beast_id food food_type next_exp desired_level refreshing cost ui",
              "tribute": "done level experience next_exp food gold food_type spent_food parent_kind refreshing cost ui desired_level tribute_requests",
              "star": "done level practice gold next_cost spent_practice ui"}
    cleanup_field = ' cleanup_action' if kind == 'tribute' and type(value) is dict and 'cleanup_action' in value else ''
    shape = (_development_shape(value, common + extras[kind] + cleanup_field) or
        kind == 'tribute' and _development_shape(value, common + extras[kind].replace(' desired_level tribute_requests', '') + cleanup_field)) or kind == 'growth' and (
        _development_shape(value, common + extras[kind] + ' prompt_active reward_dialog reward_quest_id') or
        _development_shape(value, common + extras[kind] + ' prompt_active reward_dialog reward_quest_id requirement') or
        _development_shape(value, common + extras[kind] + ' prompt_active reward_dialog reward_quest_id reward_wait') or
        _development_shape(value, common + extras[kind] + ' prompt_active reward_dialog reward_quest_id requirement reward_wait'))
    if (not shape or type(value["schema"]) is not int or value["schema"] != 1
            or type(value["gate"]) is not str or type(value["reason"]) is not str
            or type(value["available_action"]) is not str
            or value["available_action"] not in DEVELOPMENT_ACTIONS | {""}
            or not _development_operation_valid(value["operation"])):
        return False
    actions = {action for action in DEVELOPMENT_ACTIONS if action.startswith(kind + "_")}
    if kind == "equipment":
        actions |= {"reinforce_open", "enchant_tab"}
    if value["available_action"] not in actions | {""} or value["operation"]["action"] not in actions | {""}:
        return False
    if kind == 'star':
        ui = value['ui']
        ui_fields = 'star special formation page selected animating'
        animation_fields = ('lighting_marker_set', 'zodiac_sliding', 'fadein_marker_set', 'fadeout_marker_set')
        return (type(value['done']) is bool and _development_int(value['level'], 0, 196)
                and _development_int(value['gold'])
                and all(_development_int(value[key], 0, 2147483647) for key in ('practice', 'spent_practice'))
                and (value['next_cost'] is None or _development_int(value['next_cost'], 0, 2147483647))
                and (_development_shape(ui, ui_fields) or
                     _development_shape(ui, ui_fields + ' ' + ' '.join(animation_fields)))
                and all(_development_ui_valid(ui[key]) for key in ('star', 'special', 'formation'))
                and _development_int(ui['page'], -1, 255) and _development_int(ui['selected'], -1, 196)
                and type(ui['animating']) is bool
                and all(type(ui[key]) is bool for key in animation_fields if key in ui)
                and (not value['done'] or value['available_action'] in {'', 'star_close', 'star_reward_confirm'}))
    if kind == 'tribute':
        ui, cost = value['ui'], value['cost']
        return (type(value['done']) is bool and type(value['refreshing']) is bool
                and type(value.get('cleanup_action', '')) is str
                and value.get('cleanup_action', '') in {'', 'tribute_close', 'tribute_parent_close'}
                and _development_int(value['level'], 0, 100)
                and _development_int(value.get('desired_level', 0), 0, 100)
                and _development_int(value.get('tribute_requests', value['operation']['requests']), 0, 2**31 - 1)
                and _development_int(value['experience'], 0, 2**32 - 1)
                and (value['next_exp'] is None or _development_int(value['next_exp'], 0, 2**32 - 1))
                and all(_development_int(value[key]) for key in ('food', 'gold', 'spent_food'))
                and _development_int(value['food_type'], -1, 1)
                and value['parent_kind'] in {'none', 'direct', 'spirit2', 'spirit3'}
                and _development_shape(cost, 'food gold cost_status')
                and cost['food'] is None and type(cost['gold']) is int and cost['gold'] == 0
                and cost['cost_status'] == 'UNVERIFIED'
                and (_development_shape(ui, 'spirit parent rankup page nested_owned') or
                     _development_shape(ui, 'spirit parent page nested_owned'))
                and all(_development_ui_valid(ui[key]) for key in ('spirit', 'parent', 'rankup') if key in ui)
                and _development_int(ui['page'], -1, 255) and type(ui['nested_owned']) is bool
                and (not value['done'] or value['available_action'] in
                     {'', 'tribute_close', 'tribute_parent_close', 'tribute_rankup_confirm'}))
    if kind == "growth":
        if 'requirement' in value and not _growth_requirement_valid(value['requirement']):
            return False
        if 'reward_wait' in value and not _growth_reward_wait_valid(value['reward_wait']):
            return False
        if (not all(_development_int(value[key], 0, 32767) for key in ("selected_quest_id", "ui_selected_quest_id"))
                or any(type(value[key]) is not bool for key in ("window_visible", "window_enabled", "show_window"))
                or value["step"] not in {"NONE", "CLAIMED_LOCAL", "CLAIM", "CHECK", "REQUIREMENT", "REWARD"}
                or type(value["tasks"]) is not list or len(value["tasks"]) > 128):
            return False
        if 'reward_dialog' in value and (not _development_ui_valid(value['reward_dialog'])
                or type(value['prompt_active']) is not bool or not _development_int(value['reward_quest_id'], 0, 32767)):
            return False
        seen = set()
        for row in value["tasks"]:
            if (not _development_shape(row, "quest_id isfinish isgot template condition_met dispatch action_reason")
                    or not _development_int(row["quest_id"], 1, 32767) or row["quest_id"] in seen
                    or not all(_development_int(row[key], 0, 1) for key in ("isfinish", "isgot"))
                    or row["condition_met"] is not None and type(row["condition_met"]) is not bool
                    or type(row["dispatch"]) is not str or type(row["action_reason"]) is not str):
                return False
            seen.add(row["quest_id"])
            template = row["template"]
            if template is None:
                continue
            if (not _development_shape(template, "quest_id type param0 param1 exp money name desc")
                    or not _development_int(template["quest_id"], 1, 32767) or template["quest_id"] != row["quest_id"]
                    or not _development_int(template["type"], 0, 255)
                    or not all(_development_int(template[key], -(2**31), 2**31 - 1) for key in ("param0", "param1"))
                    or not all(_development_int(template[key], 0, 2**31 - 1) for key in ("exp", "money"))
                    or any(type(template[key]) is not str for key in ("name", "desc"))):
                return False
        return True
    if not _development_int(value["desired_level"], 0, 255):
        return False
    ui = value["ui"]
    if kind == "equipment":
        if (not _development_int(value["target_id"], 0, 255) or value["mode"] not in {"", "enchant", "evolve"}
                or not all(_development_int(value[key]) for key in ("available_money", "available_stone"))
                or type(value["candidates"]) is not list or len(value["candidates"]) > 255
                or not _development_shape(ui, "equipment reinforce owner_id slot page sended bound_equipment_id")
                or not all(_development_ui_valid(ui[key]) for key in ("equipment", "reinforce"))
                or any(not _development_int(ui[key], -1, 65535) for key in ("owner_id", "slot", "page", "bound_equipment_id"))
                or type(ui["sended"]) is not bool):
            return False
        seen = set()
        for row in value["candidates"]:
            if (not _development_shape(row, "equipment_id owner_id slot level main_id next_main_id cost in_current_formation owner_slot_matches")
                    or type(row["in_current_formation"]) is not bool or type(row["owner_slot_matches"]) is not bool
                    or not _development_int(row["equipment_id"], 1, 255) or row["equipment_id"] in seen
                    or not _development_int(row["owner_id"], 0, 65535)
                    or not _development_int(row["slot"], -1, 255) or not _development_int(row["level"], 0, 255)
                    or not all(_development_int(row[key], 0, 65535) for key in ("main_id", "next_main_id"))):
                return False
            seen.add(row["equipment_id"])
            cost = row["cost"]
            if (not _development_shape(cost, "money stone up cost_status") or cost["cost_status"] not in {"OBSERVED", "UNVERIFIED"}
                    or any(cost[key] is not None and not _development_int(cost[key]) for key in ("money", "stone"))
                    or cost["up"] is not None and not _development_int(cost["up"], 0, 1)):
                return False
        return True
    cost = value["cost"]
    return (all(_development_int(value[key]) for key in ("level", "experience", "beast_id", "food"))
            and _development_int(value["food_type"], -1, 255)
            and (value["next_exp"] is None or _development_int(value["next_exp"], 0, 2**32 - 1))
            and type(value["refreshing"]) is bool and _development_shape(cost, "food gold cost_status")
            and (cost["food"] is None or _development_int(cost["food"])) and _development_int(cost["gold"])
            and cost["cost_status"] in {"OBSERVED", "UNVERIFIED"}
            and _development_shape(ui, "spirit rankup page") and _development_int(ui["page"], -1, 255)
            and all(_development_ui_valid(ui[key]) for key in ("spirit", "rankup")))


def _development_valid(value: object) -> bool:
    try:
        return _development_valid_inner(value)
    except (TypeError, ValueError, KeyError):
        return False


def _development_valid_inner(value: object) -> bool:
    if type(value) is not dict:
        return False
    required = set("schema revision character_id gate growth equipment spirit last_command".split())
    if (not required <= set(value) or set(value) - required - {'reason', 'tribute', 'star'}
            or "reason" in value and type(value["reason"]) is not str
            or type(value["schema"]) is not int or value["schema"] != 1
            or not _development_int(value["revision"], 1, 999_999_999)
            or not _character_id_valid(value["character_id"]) or type(value["gate"]) is not str
            or not all(_development_sample_valid(value[key], key) for key in ("growth", "equipment", "spirit"))):
        return False
    if 'tribute' in value and not _development_sample_valid(value['tribute'], 'tribute'):
        return False
    if 'star' in value and not _development_sample_valid(value['star'], 'star'):
        return False
    command = value["last_command"]
    return command is None or (_development_shape(command, "sequence action operation_id status reason")
        and _development_int(command["sequence"], 1, 999_999_999) and command["action"] in DEVELOPMENT_ACTIONS
        and type(command["operation_id"]) is str and re.fullmatch("[0-9a-f]{32}", command["operation_id"]) is not None
        and command["status"] in {"QUEUED", "INVOKED", "REJECTED", "NOT_INVOKED", "UNKNOWN"} and type(command["reason"]) is str)


def _campaign_valid(value: object, *, challenge: bool = False, elite: bool = False) -> bool:
    if not isinstance(value, dict) or type(value.get("schema")) is not int or value.get("schema") != 1:
        return False
    if value.get("interface") != CAMPAIGN_INTERFACE:
        return False
    pending_story = value.get('pending_story_attempt_id', '')
    if not isinstance(pending_story, str) or pending_story and re.fullmatch('[0-9a-f]{32}', pending_story) is None:
        return False
    if value.get('pending_story_continuation', '') not in ('', 'intro', 'skip', 'next'):
        return False
    context = value.get("ui_context")
    if context is not None and not _ui_context_valid(context):
        return False
    selection = value.get("selection_diagnostics")
    if selection is not None and not _selection_diagnostics_valid(selection):
        return False
    if "settlement_diagnostics" in value and not _settlement_diagnostics_valid(value["settlement_diagnostics"], elite=elite):
        return False
    general = value.get("general_tutorial")
    if general is not None and not _general_tutorial_valid(general):
        return False
    if type(value.get("revision")) is not int or not 1 <= value["revision"] <= 999_999_999:
        return False
    if type(value.get("available")) is not bool or not isinstance(value.get("gate"), str):
        return False
    if not _character_id_valid(value.get("character_id")):
        return False
    if value.get("phase") not in {"MAIN", "CAMPAIGN", "TROOP", "FORMATION", "BATTLE", "RESULT", "SCENARIO", "GENERAL_TUTORIAL", "BLOCKED"}:
        return False
    if value.get("result_kind") not in {"", "NORMAL", "REWARD", "CHAPTER"}:
        return False
    if "result_navigation" in value and (type(value["result_navigation"]) is not str
            or value["result_navigation"] not in {"", "ack_result", "next_chapter"}):
        return False
    navigation = value.get("result_navigation", "")
    if navigation and (value["phase"] != "RESULT" or value["result_kind"] not in
            ({"CHAPTER"} if navigation == "next_chapter" else {"NORMAL", "REWARD"})):
        return False
    for key, upper in (("campaign_id", 65535), ("troop_id", 255)):
        if type(value.get(key)) is not int or not 0 <= value[key] <= upper:
            return False
    eligible = value.get("eligible_troops")
    if not isinstance(eligible, list) or len(eligible) > 255:
        return False
    seen = set()
    for troop in eligible:
        if (not isinstance(troop, dict) or type(troop.get("troop_id")) is not int
                or not 1 <= troop["troop_id"] <= 255 or troop["troop_id"] in seen
                or troop.get("canattack") is not True or troop.get("visible") is not True
                or troop.get("killed") is not False or type(troop.get("challenge_level")) is not int
                or not 0 <= troop["challenge_level"] <= 65535):
            return False
        seen.add(troop["troop_id"])
    tutorial = value.get("tutorial")
    if (not isinstance(tutorial, dict) or tutorial.get("kind") not in {"NONE", "SCENARIO", "HINT", "GENERAL"}
            or not isinstance(tutorial.get("step"), str) or not isinstance(tutorial.get("gate"), str)):
        return False
    if tutorial["kind"] == "HINT" and (tutorial["step"] != "new-skill" or tutorial["gate"] != "" or value["phase"] != "FORMATION"):
        return False
    if tutorial["kind"] == "NONE" and (tutorial["step"] != "" or tutorial["gate"] != ""):
        return False
    if tutorial["kind"] == "GENERAL":
        if general is None or value["phase"] not in {"GENERAL_TUTORIAL", "BLOCKED"}:
            return False
        status = general["status"]
        if status in {"NONE", "COMPLETE"}:
            return False
        suffix = ":confirm" if status in {"FORMATION_CONFIRM", "WAITING_CLOSE"} else ":deploy"
        if (not general["session_key"] or tutorial["step"] != general["session_key"] + suffix
                or tutorial["gate"] not in {"PASS", "WAITING_UI", "WAITING_NETWORK", "UNKNOWN"}):
            return False
        if tutorial["gate"] == "PASS" and (status not in {"DEPLOY", "FORMATION_CONFIRM"}
                or general["general_id"] == 0 or not general["network_observed"] or general["network_pending"]):
            return False
    elif value["phase"] == "GENERAL_TUTORIAL":
        return False
    flow = value.get("flow")
    if (type(flow) is not dict or set(flow) != {"status", "action", "reason"}
            or type(flow["status"]) is not str or flow["status"] not in {"ACTION", "WAIT", "UNSUPPORTED", "IDLE"}
            or type(flow["action"]) is not str or flow["action"] not in {"", "continue", "general_deploy", "formation_confirm"}
            or type(flow["reason"]) is not str or len(flow["reason"]) > 512):
        return False
    if flow["status"] == "ACTION":
        if (not flow["action"] or flow["reason"] or value["available"] is not True
                or value["gate"] != "PASS" or not tutorial["step"]):
            return False
        if flow["action"] == "continue":
            if tutorial["kind"] != "SCENARIO" or value["phase"] != "SCENARIO":
                return False
        elif (challenge or tutorial["kind"] != "GENERAL" or value["phase"] != "GENERAL_TUTORIAL"
                or general["status"] != ("DEPLOY" if flow["action"] == "general_deploy" else "FORMATION_CONFIRM")):
            return False
        if tutorial["gate"] != "PASS":
            return False
    elif flow["action"]:
        return False
    elif flow["status"] == "IDLE":
        if (flow["reason"] or value["available"] is not True or value["gate"] != "PASS"
                or tutorial["kind"] not in {"NONE", "HINT"}):
            return False
    elif not flow["reason"]:
        return False
    attempt = value.get("attempt")
    if not isinstance(attempt, dict) or attempt.get("status") not in ({"NONE", "SUBMITTED", "WAITING", "SETTLED", "UNKNOWN"} | ({'CONTINUED'} if elite else set())):
        return False
    if navigation and attempt["status"] != "NONE":
        return False
    if attempt["status"] == "NONE":
        if attempt.get("character_id") != "":
            return False
    elif not _character_id_valid(attempt.get("character_id")):
        return False
    if (not isinstance(attempt.get("attempt_id"), str)
            or (attempt["status"] != "NONE" and not re.fullmatch("[0-9a-f]{32}", attempt["attempt_id"]))
            or type(attempt.get("command_seq")) is not int or attempt["command_seq"] < 0
            or not isinstance(attempt.get("report_id"), str)
            or not re.fullmatch("0|[1-9][0-9]{0,19}", attempt["report_id"])
            or int(attempt["report_id"]) > 2**64 - 1
            or attempt.get("outcome") not in {"UNKNOWN", "WIN", "LOSS"}
            or not isinstance(attempt.get("evidence"), str)):
        return False
    for key, upper in (("campaign_id", 65535), ("troop_id", 255)):
        if type(attempt.get(key)) is not int or not 0 <= attempt[key] <= upper:
            return False
    if attempt['status'] == 'CONTINUED':
        return (elite and attempt['outcome'] == 'UNKNOWN'
                and attempt['evidence'] == 'elite_owned_result_receipt_unconfirmed'
                and attempt['campaign_id'] > 0 and attempt['troop_id'] > 0 and attempt['command_seq'] > 0)
    if attempt["status"] == "SETTLED":
        # Preserve a weak native/UI observation so the task can mark UNKNOWN;
        # only the separate applied receipt may complete a reportless start.
        observed_loss = (not elite and attempt["report_id"] == "0" and attempt["outcome"] == "LOSS"
                         and attempt["evidence"] == OBSERVED_LOSS_EVIDENCE)
        applied_loss = (not elite and attempt["report_id"] == "0" and attempt["outcome"] == "LOSS"
                        and attempt["evidence"] == APPLIED_LOSS_EVIDENCE)
        return (attempt["outcome"] in {"WIN", "LOSS"}
                and (attempt["report_id"] != "0" or observed_loss or applied_loss)
                and (attempt["evidence"] != APPLIED_LOSS_EVIDENCE or applied_loss)
                and (observed_loss or valid_settlement_evidence(attempt["outcome"], attempt["evidence"], challenge=challenge))
                and attempt["campaign_id"] > 0 and attempt["troop_id"] > 0 and attempt["command_seq"] > 0)
    return attempt["outcome"] == "UNKNOWN"


def _home_navigation_valid(value: object) -> bool:
    if (type(value) is not dict or set(value) - {'divinity_return'} != {'interface', 'status', 'gate', 'reason', 'origin', 'existing_list_gate',
            'current_list_instance_id', 'current_list_root_instance_id',
            'return_command_seq', 'close_command_seq', 'character_id', 'source_campaign_id',
            'source_root_instance_id', 'destination_root_instance_id', 'list_instance_id', 'destination'}
            or value.get('interface') != 'anecdote-home-return-v1'
            or value.get('status') not in ('NONE', 'RETURNING', 'LIST_READY', 'CLOSING', 'READY', 'UNKNOWN')
            or any(type(value.get(key)) is not str or len(value[key]) > 512 for key in ('gate', 'reason', 'existing_list_gate'))
            or value.get('origin') not in ('', 'RETURN', 'EXISTING_LIST', 'DIVINITY')
            or value.get('destination') not in ('', 'Campaign', 'Capital')
            or any(type(value.get(key)) is not int or not 0 <= value[key] <= 999_999_999
                   for key in ('return_command_seq', 'close_command_seq'))
            or type(value.get('source_campaign_id')) is not int or not 0 <= value['source_campaign_id'] <= 65535
            or any(type(value.get(key)) is not int or not -2**31 <= value[key] < 2**31
                   for key in ('source_root_instance_id', 'destination_root_instance_id', 'list_instance_id',
                               'current_list_instance_id', 'current_list_root_instance_id'))):
        return False
    divinity = value.get('divinity_return')
    if divinity is not None and (type(divinity) is not dict
            or set(divinity) != {'ui_instance_id', 'destination_root_instance_id'}
            or any(type(divinity[key]) is not int or not -2**31 <= divinity[key] < 2**31 or divinity[key] == 0
                   for key in divinity)):
        return False
    if value['status'] == 'NONE':
        return (value['character_id'] == '' and value['destination'] == '' and value['origin'] == ''
                and all(value[key] == 0 for key in ('return_command_seq', 'close_command_seq', 'source_campaign_id',
                    'source_root_instance_id', 'destination_root_instance_id', 'list_instance_id')))
    if value['origin'] == 'DIVINITY':
        # The stock Divinity return has no challenge list or campaign target.
        return (_character_id_valid(value.get('character_id')) and value['destination'] in ('Campaign', 'Capital')
                and divinity is not None and divinity['destination_root_instance_id'] == value['destination_root_instance_id']
                and value['status'] in ('RETURNING', 'READY', 'UNKNOWN') and value['return_command_seq'] > 0
                and all(value[key] == 0 for key in ('close_command_seq', 'source_campaign_id', 'list_instance_id'))
                and value['source_root_instance_id'] != 0 and value['destination_root_instance_id'] != 0
                and value['source_root_instance_id'] != value['destination_root_instance_id'])
    return (_character_id_valid(value.get('character_id')) and value['destination'] in ('Campaign', 'Capital')
            and (value['origin'] == 'RETURN' and value['return_command_seq'] > 0 and value['source_campaign_id'] > 0
                 or value['origin'] == 'EXISTING_LIST' and value['return_command_seq'] == value['source_campaign_id'] == 0
                 and value['status'] in ('CLOSING', 'READY', 'UNKNOWN') and value['close_command_seq'] > 0)
            and all(value[key] != 0 for key in ('source_root_instance_id', 'destination_root_instance_id', 'list_instance_id'))
            and (value['status'] not in ('CLOSING', 'READY') or value['close_command_seq'] > 0))


def _challenge_valid(value: object, *, elite: bool = False) -> bool:
    """Validate the distinct normal-only view before any planner admission."""
    kind = 4 if elite else 2
    if (type(value) is not dict or value.get('mode') != ('ELITE_CHALLENGE' if elite else 'NORMAL_CHALLENGE')
            or type(value.get('campaign_type')) is not int or value['campaign_type'] not in (0, 2, 4)):
        return False
    phase = value.get('phase')
    if 'home_navigation' in value and not _home_navigation_valid(value['home_navigation']):
        return False
    if value.get('available') is True and value['campaign_type'] != (0 if phase in ('MAIN', 'CHALLENGE_LIST') else kind):
        return False
    if elite:
        if (value.get('elite_interface') != ELITE_INTERFACE or type(value.get('elite_star')) is not int
                or not 0 <= value['elite_star'] <= 3 or not _elite_quota_valid(value.get('elite_quota'))
                or not _character_id_valid(value.get('user_id')) or not _character_id_valid(value.get('zone_id'))
                or value.get('available') is True and phase not in ('MAIN', 'CHALLENGE_LIST') and value['elite_star'] == 0):
            return False
        if ((value.get('attempt') or {}).get('status') == 'SETTLED'
                and (value.get('attempt') or {}).get('evidence') != 'captured_authoritative_receipt_applied'):
            return False
    resources = value.get('challenge_resources')
    if resources is not None and (type(resources) is not dict or set(resources) != {'gold', 'bind_gold', 'stamina', 'hero_tries'}
            or any(type(number) is not int or not 0 <= number <= 2**63 - 1 for number in resources.values())):
        return False
    entry = value.get('entry')
    if entry is not None and (type(entry) is not dict or set(entry) != {'status', 'command_seq', 'campaign_id', 'reason'}
            or type(entry['status']) is not str or entry['status'] not in ('NONE', 'WAITING', 'READY', 'UNKNOWN')
            or type(entry['command_seq']) is not int or not 0 <= entry['command_seq'] <= 999_999_999
            or type(entry['campaign_id']) is not int or not 0 <= entry['campaign_id'] <= 65535
            or type(entry['reason']) is not str or len(entry['reason']) > 512):
        return False
    entry_waits = {'WAITING_CHALLENGE_ENTER': ('MAIN', 0), 'WAITING_CHALLENGE_MAP': ('CAMPAIGN', kind),
                   'WAITING_CHALLENGE_SOURCE_RETIREMENT': ('CAMPAIGN', kind)}
    if value.get('gate') in entry_waits:
        expected_phase, expected_type = entry_waits[value['gate']]
        if (entry is None or entry['status'] != 'WAITING' or entry['command_seq'] <= 0 or entry['campaign_id'] <= 0
                or value['campaign_type'] != expected_type or phase != expected_phase or value.get('available') is not False
                or expected_type == kind and value.get('campaign_id') != entry['campaign_id']):
            return False
    rows = value.get('challenge_candidates')
    if type(rows) is not list or len(rows) > 512:
        return False
    seen = set()
    positions = set()
    for row in rows:
        if (type(row) is not dict or set(row) != ({'campaign_id', 'campaign_type', 'display_index', 'finished', 'unlocked', 'gate'} | ({'star'} if elite else set()))
                or type(row['campaign_id']) is not int or not 1 <= row['campaign_id'] <= 65535
                or row['campaign_id'] in seen or type(row['campaign_type']) is not int or row['campaign_type'] != kind
                or elite and (type(row['star']) is not int or row['star'] not in (1, 2, 3))
                or type(row['display_index']) is not int or not 0 <= row['display_index'] < 512
                or row['display_index'] in positions or type(row['finished']) is not bool
                or type(row['unlocked']) is not bool or type(row['gate']) is not str
                or len(row['gate']) > 512):
            return False
        seen.add(row['campaign_id'])
        positions.add(row['display_index'])
    # The common battle DTO stays identical. The list has no battle page.
    common = {**value, 'phase': 'MAIN' if phase == 'CHALLENGE_LIST' else phase}
    return _campaign_valid(common, challenge=True, elite=elite)


def _elite_quota_valid(value: object) -> bool:
    if (type(value) is not dict or set(value) != {'cycle', 'verified', 'remaining', 'bought', 'baseline', 'completed', 'reason', 'evidence'}
            or type(value['cycle']) is not int or not 0 <= value['cycle'] <= 2**32 - 1
            or type(value['verified']) is not bool
            or any(type(value[key]) is not int or not 0 <= value[key] <= 2**31 - 1 for key in ('remaining', 'bought'))
            or type(value['reason']) is not str or not value['reason'] or len(value['reason']) > 512):
        return False
    if not value['verified']:
        return (value['baseline'] is None and value['completed'] is None
                and value['reason'] != 'PASS' and value['evidence'] == '')
    if (value['reason'] == 'PASS' and value['evidence'] == 'current_loaded_elite_balance'
            and value['cycle'] > 0 and value['baseline'] is None and value['completed'] is None):
        return True
    return (value['reason'] == 'PASS' and value['evidence'] == 'observed_natural_0x24_callback'
            and value['cycle'] > 0 and value['bought'] == 0 and type(value['baseline']) is int
            and 10 <= value['baseline'] <= 2**31 - 1 and type(value['completed']) is int
            and 0 <= value['completed'] == value['baseline'] - value['remaining'])


def _elite_valid(value: object) -> bool:
    return _challenge_valid(value, elite=True)


def _home_loss_advice_ready(value: object, scope: str) -> bool:
    """Only the native loss-advice close, never another scenario continuation."""
    if scope not in ('challenge', 'elite') or not (_elite_valid(value) if scope == 'elite' else _challenge_valid(value)):
        return False
    ui = value.get('ui_context') or {}
    tutorial = value.get('tutorial') or {}
    flow = value.get('flow') or {}
    attempt = value.get('attempt') or {}
    navigation = value.get('home_navigation')
    if (not _home_navigation_valid(navigation) or navigation['status'] not in ('NONE', 'READY')
            or value.get('phase') != 'SCENARIO' or value.get('available') is not True or value.get('gate') != 'PASS'
            or value.get('campaign_type') != (4 if scope == 'elite' else 2) or value.get('campaign_id', 0) <= 0
            or ui.get('current_root_level') != 'Anecdote' or ui.get('current_root_active') is not True
            or ui.get('selection_gate') != 'PASS' or not ui.get('current_root_instance_id')
            or tutorial.get('kind') != 'SCENARIO' or tutorial.get('gate') != 'PASS'
            or flow.get('status') != 'ACTION' or flow.get('action') != 'continue'
            or (value.get('entry') or {}).get('status') in ('WAITING', 'UNKNOWN')):
        return False
    if attempt.get('status') == 'NONE':
        return tutorial.get('step') == 'loss-advice:current-page'
    evidence = 'captured_authoritative_receipt_applied' if scope == 'elite' else 'applied_owned_campaign_loss_receipt'
    return (attempt.get('status') == 'SETTLED' and attempt.get('outcome') == 'LOSS'
            and attempt.get('evidence') == evidence
            and tutorial.get('step') == 'loss-advice:' + attempt.get('attempt_id', ''))


def _home_battle_guide_ready(value: object, scope: str) -> bool:
    """Admit only the exact native BattleGuideUI close projection."""
    validators = {'campaign': _campaign_valid, 'challenge': _challenge_valid, 'elite': _elite_valid}
    if scope not in validators or not validators[scope](value):
        return False
    ui = value.get('ui_context') or {}
    tutorial = value.get('tutorial') or {}
    flow = value.get('flow') or {}
    counts = ui.get('counts') or {}
    return (value.get('phase') == 'SCENARIO' and value.get('available') is True and value.get('gate') == 'PASS'
            and value.get('campaign_type') == {'campaign': 0, 'challenge': 2, 'elite': 4}[scope]
            and value.get('campaign_id', 0) > 0 and 1 <= value.get('troop_id', 0) <= 255
            and ui.get('current_root_level') == ('Campaign' if scope == 'campaign' else 'Anecdote')
            and ui.get('current_root_active') is True and ui.get('selection_gate') == 'PASS'
            and bool(ui.get('current_root_instance_id'))
            and all((counts.get(name) or {}).get('matching') == 1 for name in ('root_ui', 'campaign', 'battle_guide'))
            and tutorial.get('kind') == 'SCENARIO' and tutorial.get('gate') == 'PASS'
            and re.fullmatch(r'battle-guide:-?[1-9][0-9]*:[1-9][0-9]*', tutorial.get('step', '')) is not None
            and flow.get('status') == 'ACTION' and flow.get('action') == 'continue'
            and (value.get('attempt') or {}).get('status') in ('NONE', 'SETTLED', 'CONTINUED')
            and (value.get('entry') or {}).get('status') not in ('WAITING', 'UNKNOWN'))


def _general_tutorial_valid(value: object) -> bool:
    fields = {"general_id", "status", "local_observation", "server_configuration", "special_instance_id",
              "formation_instance_id", "session_key", "reason", "network_observed", "network_pending"}
    if (type(value) is not dict or set(value) != fields or type(value["general_id"]) is not int
            or not 0 <= value["general_id"] <= 65535):
        return False
    if type(value["status"]) is not str or value["status"] not in {"NONE", "DEPLOY", "FORMATION_CONFIRM", "WAITING_FORMATION", "WAITING_CLOSE", "COMPLETE", "UNKNOWN"}:
        return False
    if (type(value["local_observation"]) is not str or value["local_observation"] not in {"NONE", "INVOKED", "DEPLOYED", "CLOSED"}
            or type(value["server_configuration"]) is not str or value["server_configuration"] != "UNVERIFIED"):
        return False
    for key in ("special_instance_id", "formation_instance_id"):
        if type(value[key]) is not int or not -(2**31) <= value[key] < 2**31:
            return False
    if type(value["network_observed"]) is not bool or type(value["network_pending"]) is not bool:
        return False
    if type(value["reason"]) is not str or len(value["reason"]) > 512 or type(value["session_key"]) is not str:
        return False
    expected = rf"general:{value['general_id']}:{value['special_instance_id']}:[0-9]{{1,19}}"
    return value["session_key"] == "" or re.fullmatch(expected, value["session_key"]) is not None


def _settlement_diagnostics_valid(value: object, *, elite: bool = False) -> bool:
    # Optional evidence only; never promotes an attempt or authorizes an action.
    def integer(item, low, high):
        return type(item) is int and low <= item <= high

    def report(item):
        return (isinstance(item, str) and re.fullmatch("0|[1-9][0-9]{0,19}", item) is not None
                and int(item) <= 2**64 - 1)

    def shape(item, numbers, bools, extra):
        return (isinstance(item, dict) and set(item) == set(numbers) | set(bools) | set(extra)
                and all(integer(item[key], *bounds) for key, bounds in numbers.items())
                and all(type(item[key]) is bool for key in bools))

    if (not isinstance(value, dict) or set(value) != {"anchor_scope", "character_id", "observer", "battle", "result", "target"}
            or value["anchor_scope"] != "this_helper_instance" or not _character_id_valid(value["character_id"])):
        return False
    observer = value["observer"]
    nullable = {"zero_observed", "battle_observed", "receipt_captured", "campaign_match", "battle_match",
                "character_match", "character_id_match", "report_token_match"}
    if (not shape(observer, {}, {"anchors_available"}, nullable | {"status"})
            or not isinstance(observer["status"], str) or observer["status"] not in ({"NONE", "SUBMITTED", "WAITING", "SETTLED", "UNKNOWN"} | ({'CONTINUED'} if elite else set()))
            or any(observer[key] is not None and type(observer[key]) is not bool for key in nullable)
            or (observer["status"] == "NONE" and observer["anchors_available"])
            or (not observer["anchors_available"] and any(observer[key] is not None for key in nullable))):
        return False
    battle = value["battle"]
    if battle is not None:
        numbers = {"instance_id": (-(2**31), 2**31 - 1), "idcampaign": (0, 65535), "idtroop": (0, 255),
                   "view_type": (-(2**31), 2**31 - 1), "parent_instance_id": (-(2**31), 2**31 - 1),
                   "owner_root_instance_id": (-(2**31), 2**31 - 1), "previous_root_instance_id": (-(2**31), 2**31 - 1)}
        bools = {"visible", "parent_matches_campaign", "parent_owned_by_current", "previous_root_matches_current",
                 "replay", "cross", "surrender", "waiting", "use_end", "fighting", "pending_present"}
        loss_fields = {'report_result_status', 'show_defender_result', 'battle_result_level'}
        extra = {"report_id", "owner_root_level", "playback_remaining", "packet"}
        if loss_fields.intersection(battle):
            if (not loss_fields.issubset(battle) or
                    battle['report_result_status'] is not None and
                    not integer(battle['report_result_status'], 0, 3)):
                return False
            numbers['battle_result_level'] = (-(2**31), 2**31 - 1)
            bools.add('show_defender_result')
            extra.add('report_result_status')
        if (not shape(battle, numbers, bools, extra)
                or not report(battle["report_id"]) or not isinstance(battle["owner_root_level"], str) or battle["owner_root_level"] not in {"", "Campaign", "Capital", "Anecdote", "Fairy", "TalentCampaign", "Battle", "Root", "OTHER"}):
            return False
        remaining = battle["playback_remaining"]
        if remaining is not None and (type(remaining) not in (int, float) or not -3.4028235e38 <= remaining <= 3.4028235e38):
            return False
        packet = battle["packet"]
        if packet is not None and (not battle["pending_present"] or not shape(packet,
                {"result": (0, 2**32 - 1), "type": (0, 2**32 - 1), "idcampaign": (0, 2**32 - 1), "idtroop": (0, 2**32 - 1)},
                {"iswin"}, {"report_id"}) or not report(packet["report_id"])):
            return False
    result = value["result"]
    result_numbers = {"instance_id": (-(2**31), 2**31 - 1)}
    if isinstance(result, dict) and 'battle_level' in result:
        result_numbers['battle_level'] = (-(2**31), 2**31 - 1)
    if result is not None and (not shape(result, result_numbers,
            {"battle_match", "root_match", "battle_view"}, {"report_id"}) or not report(result["report_id"])):
        return False
    target = value["target"]
    if target is not None and (not shape(target,
            {"idcampaign": (0, 65535), "idtroop": (0, 255), "finish_count": (0, 255), "finish_max": (0, 255)},
            {"killed", "canattack", "data_present", "matches_battle_target"}, {"campaign_finished", "data_anchor_match"})
            or (target["campaign_finished"] is not None and type(target["campaign_finished"]) is not bool)
            or target["data_anchor_match"] is not None):
        return False
    return True


def _selection_diagnostics_valid(value: object) -> bool:
    fields = {"character_campaign_id", "character_level", "campaign", "troops", "truncated"}
    if not isinstance(value, dict) or set(value) != fields:
        return False
    if (type(value["character_campaign_id"]) is not int or not 0 <= value["character_campaign_id"] <= 65535
            or type(value["character_level"]) is not int or not -32768 <= value["character_level"] <= 65535
            or type(value["truncated"]) is not bool):
        return False
    campaign = value["campaign"]
    campaign_bools = {"enabled", "unlocked", "finished", "m_bDebug", "m_bPostInit", "m_bBorning",
                      "m_bBornWant", "m_bScenarioVisible", "m_bSignActiving"}
    if campaign is not None:
        if (not isinstance(campaign, dict) or set(campaign) != campaign_bools | {"type"}
                or type(campaign["type"]) is not int or not 0 <= campaign["type"] <= 255
                or any(type(campaign[key]) is not bool for key in campaign_bools)):
            return False
    ranges = {"troop_id": (1, 255), "idcampaign": (0, 65535), "idtroop": (0, 255),
              "chanllege_level": (0, 65535), "click_mode": (0, 255), "limit_count": (-32768, 32767),
              "finish_count": (0, 255), "finish_max": (0, 255), "isNewbie": (0, 255),
              "Newbie_Skill": (0, 65535), "Newbie_formation": (0, 2**32 - 1),
              "mesh_script_count": (0, 512)}
    ranges.update({key: (0, 2**32 - 1) for key in ("m_scenarioClickF", "m_scenarioVisible", "m_scenarioAttackF",
                                                "m_scenarioVictoryF", "m_scenarioFailure", "m_scenarioSystem")})
    bools = {"visible", "canattack", "killed", "clicked", "show_arrow", "mesh_present", "mesh_active", "mesh_root_matches",
             "mesh_script_enabled", "mesh_script_owner_matches"}
    gates = {"PASS", "phase", "campaign_disabled", "troop_missing", "campaign_missing", "character_missing",
             "campaign_data_missing", "campaign_type", "campaign_locked", "campaign_finished", "troop_hidden",
             "troop_unattackable", "troop_killed", "character_level", "direct_mode", "click_mode_unknown",
             "mesh_missing", "mesh_inactive", "mesh_root", "mesh_script_not_unique", "mesh_script_owner", "mesh_script_disabled",
             "debug", "post_init", "borning", "born_want", "scenario_visible", "sign_activing", "challenge_cooldown_active"}
    troops = value["troops"]
    if not isinstance(troops, list) or len(troops) > 8:
        return False
    seen = set()
    for troop in troops:
        if not isinstance(troop, dict) or set(troop) != set(ranges) | bools | {"select_gate"}:
            return False
        if any(type(troop[key]) is not int or not low <= troop[key] <= high for key, (low, high) in ranges.items()):
            return False
        if (any(type(troop[key]) is not bool for key in bools) or not isinstance(troop["select_gate"], str)
                or troop["select_gate"] not in gates or troop["troop_id"] in seen):
            return False
        seen.add(troop["troop_id"])
    return True


def _ui_context_valid(value: object) -> bool:
    fields = {"current_root_instance_id", "current_root_level", "current_root_active", "global_input_enabled", "selection_gate", "counts", "campaign_candidates", "candidates_truncated"}
    levels = {"", "Campaign", "Capital", "Anecdote", "Fairy", "TalentCampaign", "Battle", "Root", "OTHER"}
    if not isinstance(value, dict) or set(value) != fields:
        return False
    if type(value["current_root_instance_id"]) is not int or not -(2**31) <= value["current_root_instance_id"] < 2**31:
        return False
    if not isinstance(value["current_root_level"], str) or value["current_root_level"] not in levels or not isinstance(value["selection_gate"], str) or len(value["selection_gate"]) > 100:
        return False
    if any(type(value[key]) is not bool for key in ("current_root_active", "global_input_enabled", "candidates_truncated")):
        return False
    counts = value["counts"]
    roles = {"root_ui", "campaign", "battle", "battle_baseline", "troop", "formation", "chapter", "scenario", "function", "function_scenario", "notification", "battle_guide", "normal_result", "reward", "challenge_list", "challenge_backdrop"}
    if not isinstance(counts, dict) or not set(counts) <= roles:
        return False
    for count in counts.values():
        if not isinstance(count, dict) or set(count) != {"total", "visible", "matching"}:
            return False
        if any(type(n) is not int or not 0 <= n <= 512 for n in count.values()):
            return False
        if count["visible"] > count["total"] or count["matching"] > count["total"]:
            return False
    candidates = value["campaign_candidates"]
    if not isinstance(candidates, list) or len(candidates) > 8:
        return False
    for candidate in candidates:
        if not isinstance(candidate, dict) or set(candidate) != {"instance_id", "visible", "owner_matches_current", "hierarchy_matches_current", "owner_root_level"}:
            return False
        if type(candidate["instance_id"]) is not int or not -(2**31) <= candidate["instance_id"] < 2**31 or not isinstance(candidate["owner_root_level"], str) or candidate["owner_root_level"] not in levels:
            return False
        if any(type(candidate[key]) is not bool for key in ("visible", "owner_matches_current", "hierarchy_matches_current")):
            return False
    return True


class BridgeClient:
    """One explicit bridge lifecycle, including a durable single-owner lease."""
    def __init__(self, project_root: Path, identity: ProcessIdentity,
                 revalidate: Callable[[], None], *,
                 status_reader: Callable[[int, int], str] = process_status,
                 popen: Callable = subprocess.Popen,
                 on_state: Callable[[dict], None] | None = None,
                 title_operation: str = '', title_epoch: str = '') -> None:
        self.project_root = Path(project_root).resolve()
        self.root = self.project_root / "data" / "object_sessions"
        self.assets = self.project_root / "jiangshen_assistant" / "runtime" / "win32"
        self.identity = identity
        if title_operation and (not re.fullmatch('[0-9a-f]{32}', title_operation)
                                or not re.fullmatch('[0-9a-f]{32}', title_epoch)):
            raise BridgeError('title_operation_identity')
        self.title_operation, self.title_epoch = title_operation, title_epoch
        self._title_admitted = False
        self.revalidate = revalidate
        self._status_reader = status_reader
        self._popen = popen
        self._on_state = on_state or (lambda state: None)
        self.nonce = uuid.uuid4().hex
        self.session_dir = self.root / self.nonce
        self.lease_path = self.root / "leases" / f"{identity.pid}-{identity.creation_time}.json"
        self.title_owner_path = self.lease_path.with_suffix('.title')
        self.title_attempt_path = self.lease_path.with_suffix('.title-attempt.json')
        self.ledger_path = self.session_dir / "session.json"
        self.bin_dir: Path | None = None
        self._request_lock = threading.Lock()
        self._ledger_lock = threading.RLock()
        self._seq = 0
        self._owns_lease = False
        self._bootstrap_started = False
        self._unknown = False
        self._terminal = False
        self._last_state: dict = {}
        self._ledger: dict = {}
        self.host = None
        self.native_events: deque[dict] = deque(maxlen=32)
        self._native_error = ""
        self._first_unknown: dict | None = None
        self._recovery_status: dict = {}
        self._historical_recovery: list[dict] = []
        self._domain_validation_errors: dict[str, str] = {}
        self._business_failures: dict[str, dict] = {}
        self._pending_stage = "idle"
        self._state_notifications = None
        self._battle_guard = None
        self._battle_guard_lock = threading.RLock()
        self._same_process_recovery: dict | None = None

    def open_battle_admission(self) -> str:
        with self._battle_guard_lock:
            if self._terminal or self._unknown:
                raise BridgeUnknown('battle_launch_session_unavailable')
            if self._battle_guard is None:
                self._battle_guard = BattleLaunchAdmission(self.nonce)
            try:
                return self._battle_guard.open_epoch()
            except BattleLaunchError as exc:
                raise BridgeUnknown(str(exc)) from exc

    def capture_battle_epoch(self) -> str:
        with self._battle_guard_lock:
            if self._battle_guard is None:
                return self.open_battle_admission()
            try:
                return self._battle_guard.epoch
            except BattleLaunchError as exc:
                raise BridgeAdmissionClosed(str(exc)) from exc

    def cancel_battle_admission(self) -> None:
        # Deliberately independent of _request_lock and file/bridge I/O.
        with self._battle_guard_lock:
            if self._battle_guard is None:
                self._battle_guard = BattleLaunchAdmission(self.nonce)
            try:
                self._battle_guard.cancel_and_drain()
            except BattleLaunchError as exc:
                raise BridgeUnknown(str(exc)) from exc

    @property
    def state_events_enabled(self) -> bool:
        watcher = self._state_notifications
        return watcher is not None and watcher.active

    def start_state_events(self, *, on_error=None, on_notice=None) -> None:
        if self.state_events_enabled:
            return
        if self._terminal or self._unknown:
            raise BridgeError('对象连接已经停止，不能订阅状态事件。')
        from .state_notifications import StateNotifications
        read_recovery = None

        def recovery_failure():
            error = read_recovery['error']
            error.read_attempts = read_recovery['attempts']
            error.read_elapsed_ms = round((time.monotonic() - read_recovery['started']) * 1000, 3)
            return error

        def recovery_notice(kind):
            error = recovery_failure()
            return dict(kind=kind, stage='state_notification',
                        message=('桥接状态文件暂时无法读取，正在等待可读取快照。'
                                 if kind == 'state_read_wait' else '桥接状态快照读取已恢复。'),
                        winerror=error.winerror, filename=str(error.filename),
                        attempts=error.read_attempts, elapsed_ms=error.read_elapsed_ms)

        def failed(exc):
            try:
                self.mark_unknown(exc)
            except Exception as persistence:
                exc.persistence_error = str(persistence)
            if on_error is not None:
                on_error(exc)
        def changed():
            nonlocal read_recovery
            notice = None
            available = True
            with self._request_lock:
                if self._terminal or self._unknown:
                    return
                self._pending_stage = 'state_notification'
                self.revalidate()
                if self.target_exited():
                    if on_error is not None:
                        on_error(BridgeError('游戏进程已退出。'))
                    return
                if (read_recovery is not None and
                        time.monotonic() - read_recovery['started'] >= _STATE_READ_RECOVERY_TIMEOUT):
                    raise recovery_failure()
                read_stats = {}
                read_timeout = (_READ_RETRY_TIMEOUT if read_recovery is None else
                    min(_READ_RETRY_TIMEOUT, max(0.0, _STATE_READ_RECOVERY_TIMEOUT -
                        (time.monotonic() - read_recovery['started']))))
                try:
                    state = self._state(read_stats=read_stats, read_timeout=read_timeout)
                except OSError as exc:
                    # Only this established snapshot's exhausted transient reads are
                    # recoverable here. Validation, ACL, disk and callback errors
                    # retain their ordinary terminal behavior.
                    if (getattr(exc, 'winerror', None) not in _RECEIPT_READ_TRANSIENT_ERRORS
                            or getattr(exc, 'read_stage', None) not in ('open', 'read')
                            or getattr(exc, 'filename', None) != str(self.session_dir / 'state.json')):
                        raise
                    if read_recovery is None:
                        read_recovery = dict(error=exc, attempts=read_stats.get('attempts', exc.read_attempts),
                            started=time.monotonic() - exc.read_elapsed_ms / 1000)
                        notice = recovery_notice('state_read_wait')
                    else:
                        read_recovery['attempts'] += read_stats.get('attempts', exc.read_attempts)
                        for field in ('read_last_winerror', 'read_last_errno', 'read_last_stage', 'read_last_message'):
                            if hasattr(exc, field):
                                setattr(read_recovery['error'], field, getattr(exc, field))
                    if time.monotonic() - read_recovery['started'] >= _STATE_READ_RECOVERY_TIMEOUT:
                        raise recovery_failure()
                    available, state = False, None
                else:
                    if read_recovery is not None:
                        read_recovery['attempts'] += read_stats.get('attempts', 0)
                        if state is None:
                            available = False
                        else:
                            notice = recovery_notice('state_read_resumed')
                            read_recovery = None
                if state is not None and _is_clean(state):
                    self._finish_stopped(state)
                    self._on_state(dict(state))
            # Observability must neither hold the bridge request gate nor turn
            # a recovered read into an unknown game transaction.
            if notice is not None and on_notice is not None:
                try:
                    on_notice(notice)
                except Exception:
                    pass
            return available
        watcher = StateNotifications(self.session_dir, changed, failed)
        self._state_notifications = watcher
        try:
            watcher.start()
        except Exception:
            self._state_notifications = None
            raise

    def stop_state_events(self) -> None:
        watcher = self._state_notifications
        if watcher is not None:
            watcher.close()

    @contextmanager
    def _lease_edit(self):
        claim = self.lease_path.with_suffix(".claim")
        stream = claim.open("a+b")
        try:
            if stream.tell() == 0:
                stream.write(b'\0')
                stream.flush()
            stream.seek(0)
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            stream.close()
            raise BridgeUnknown("对象连接租约正在更新，请再次点击重试。") from exc
        try:
            yield
        finally:
            try:
                if os.name == 'nt':
                    stream.seek(0)
                    msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
            finally:
                stream.close()

    @property
    def terminal(self) -> bool:
        return self._terminal

    @property
    def is_unknown(self) -> bool:
        return self._unknown

    @property
    def first_unknown(self) -> dict:
        with self._ledger_lock:
            return dict(self._first_unknown or {})

    @property
    def last_state(self) -> dict:
        return dict(self._last_state)

    @property
    def recovery_status(self) -> dict:
        status = dict(self._recovery_status or {'classification': 'none', 'process_restart_required': False,
            'reason': '', 'session_id': self.nonce, 'pid': self.identity.pid,
            'process_creation_filetime': self.identity.creation_time})
        if self._first_unknown is not None:
            status['first_unknown'] = dict(self._first_unknown)
        if self._same_process_recovery is not None:
            status['same_process_recovery'] = dict(self._same_process_recovery)
        diagnostics = {}
        if self._historical_recovery:
            diagnostics['historical_unknown'] = [dict(item) for item in self._historical_recovery]
        if self._domain_validation_errors:
            diagnostics['validation_errors'] = dict(self._domain_validation_errors)
        if self._business_failures:
            diagnostics['business_unknown'] = {key: dict(value) for key, value in self._business_failures.items()}
        if diagnostics:
            status['business_diagnostics'] = diagnostics
        return status

    def _capture_recovery(self, state: dict) -> None:
        recovery = state.get('recovery')
        recovery = recovery if isinstance(recovery, dict) else {}
        restart = (state.get('process_restart_required') is True or recovery.get('process_restart_required') is True
                   or recovery.get('classification') == 'restart_required' or state.get('lifecycle') == 'recovery_unknown')
        if self._recovery_status.get('process_restart_required'):
            return  # An ordinary later snapshot cannot erase this process boundary.
        self._recovery_status = {'classification': 'restart_required' if restart else
            'pending_mutation' if recovery.get('classification') == 'pending_mutation' else 'none',
            'process_restart_required': restart, 'reason': recovery.get('reason') or
                ('bridge_recovery_unknown' if restart else ''),
            'session_id': self.nonce, 'pid': self.identity.pid,
            'process_creation_filetime': self.identity.creation_time, 'replay_allowed': False}
        if isinstance(recovery.get('battle_launch'), dict):
            self._recovery_status['battle_launch'] = dict(recovery['battle_launch'])

    def _restart_error(self) -> BridgeUnknown:
        error = BridgeUnknown('当前游戏进程有未确认战斗，请先退出并重新启动游戏；重新连接不会解除该状态。')
        error.recovery_status = self.recovery_status
        return error

    def _check_restart_boundary(self) -> None:
        """Retain domain history without making it an admission rule for a link.

        The new native component proves its own transport and thread ownership.
        A historical UNKNOWN remains evidence for business action admission only.
        """
        history = []
        for path in self.root.glob('*/recovery_unknown.json'):
            try:
                marker = _read_json(path)
                session_id = path.parent.name
                if (not isinstance(marker, dict) or marker.get('protocol') != PROTOCOL
                        or marker.get('bridge_id') != BRIDGE_ID
                        or not re.fullmatch('[0-9a-f]{32}', session_id)
                        or marker.get('nonce') != session_id or marker.get('session_id') != session_id
                        or marker.get('pid') != self.identity.pid
                        or marker.get('process_creation_filetime') != self.identity.creation_time
                        or marker.get('process_restart_required') is not True):
                    continue
                ledger = _read_json(path.parent / 'session.json') or {}
                state = _read_json(path.parent / 'state.json') or {}
                item = dict(session_id=session_id, pid=self.identity.pid,
                    process_creation_filetime=self.identity.creation_time,
                    reason=marker.get('reason') or 'native_recovery_unknown',
                    historical_process_restart_required=True, old_outcome='UNKNOWN', replay_allowed=False,
                    marker_path=str(path))
                if (ledger.get('session_id') == session_id and ledger.get('pid') == self.identity.pid
                        and ledger.get('process_creation_filetime') == self.identity.creation_time
                        and isinstance(ledger.get('first_unknown'), dict)):
                    item['first_unknown'] = dict(ledger['first_unknown'])
                if (state.get('session_id') == session_id and state.get('pid') == self.identity.pid
                        and state.get('process_creation_filetime') == self.identity.creation_time):
                    operation = (state.get('daily_soul') or {}).get('operation') or {}
                    old_id = operation.get('operation_id')
                    if isinstance(old_id, str) and re.fullmatch('[0-9a-f]{32}', old_id):
                        item['old_operation_id'] = old_id
                    if isinstance(state.get('battle_launch'), dict):
                        item['battle_launch'] = dict(state['battle_launch'])
                history.append(item)
            except (OSError, ValueError, TypeError, AttributeError) as exc:
                history.append(dict(marker_path=str(path), evidence_read_error=str(exc), replay_allowed=False))
        self._historical_recovery = history

    def _common_valid(self, data: dict) -> None:
        expected = {"protocol": PROTOCOL, "bridge_id": BRIDGE_ID, "nonce": self.nonce,
                    "session_id": self.nonce, "pid": self.identity.pid,
                    "process_creation_filetime": self.identity.creation_time,
                    "verified_native_tid": self.identity.tid}
        for key, value in expected.items():
            if type(data.get(key)) is not type(value) or data.get(key) != value:
                raise BridgeUnknown(f"桥接回执身份不匹配：{key}")

    def _write_ledger(self, state: str, **changes) -> None:
        with self._ledger_lock:
            if self._unknown and state not in ("stopped", "target_exited"):
                state = "unknown"
            self._ledger.update({"protocol": PROTOCOL, "bridge_id": BRIDGE_ID,
                                 "session_id": self.nonce, "nonce": self.nonce,
                                 **self.identity.as_dict(), "state": state,
                                 "last_command_seq": self._seq, "updated_at": _now(),
                                 "bootstrap_started": self._bootstrap_started, **changes})
            _atomic_json(self.ledger_path, self._ledger)

    def _acquire_lease(self) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        self.lease_path.parent.mkdir(exist_ok=True)
        # Each explicit connection receives a fresh command channel. Previous
        # session records retain their evidence without owning future clicks.
        with self._lease_edit():
            self._check_restart_boundary()
            # All new clients observe the same process-scoped title exclusion,
            # including ordinary connections that normally supersede a lease.
            if self.title_operation and self.title_owner_path.exists():
                raise BridgeError('title_exclusive_session_present')
            existing = _read_json(self.lease_path)
            if self.title_operation:
                current = _read_json(self.lease_path)
                if current:
                    previous_nonce = current.get('session_id')
                    if not isinstance(previous_nonce, str) or not re.fullmatch('[0-9a-f]{32}', previous_nonce):
                        raise BridgeError('existing_bridge_lease_invalid')
                    prior = self.root / previous_nonce / 'session.json'
                    ledger = _read_json(prior)
                    if (not ledger or ledger.get('session_id') != previous_nonce
                            or ledger.get('pid') != self.identity.pid
                            or ledger.get('process_creation_filetime') != self.identity.creation_time
                            or ledger.get('state') not in ('stopped', 'target_exited')):
                        raise BridgeError('existing_bridge_cleanup_required')
                attempt = _read_json(self.title_attempt_path)
                if attempt and not (attempt.get('outcome') == 'NOT_INVOKED'
                                    and attempt.get('cleanup_confirmed') is True):
                    raise BridgeError('title_prior_attempt_requires_player_observation')
                _atomic_json(self.title_attempt_path, {'interface': TITLE_START_INTERFACE,
                    **self.identity.as_dict(), 'operation_id': self.title_operation,
                    'session_id': self.nonce, 'outcome': 'UNKNOWN', 'cleanup_confirmed': False,
                    'updated_at': _now()})
                self._title_admitted = True
            try:
                _atomic_json(self.lease_path, {"protocol": PROTOCOL, "bridge_id": BRIDGE_ID,
                             "session_id": self.nonce, **self.identity.as_dict(),
                             "owner_pid": os.getpid(), "created_at": _now(),
                             "title_exclusive": bool(self.title_operation)})
                self._owns_lease = True
                if self.title_operation:
                    _atomic_bytes(self.title_owner_path, self._title_descriptor())
            except Exception:
                # No host can exist before this acquisition returns. Preserve
                # an UNKNOWN process marker if this rollback itself fails.
                if self.title_operation and self._owns_lease:
                    self.lease_path.unlink()
                    if self.title_owner_path.exists() and self.title_owner_path.read_bytes() == self._title_descriptor():
                        self.title_owner_path.unlink()
                    self._owns_lease = False
                raise

    def _release_lease(self) -> None:
        if not self._owns_lease:
            return
        with self._lease_edit():
            current = _read_json(self.lease_path)
            if not current or current.get("session_id") != self.nonce:
                if self.title_operation:
                    raise BridgeUnknown('title_lease_owner_changed')
                self._owns_lease = False
                return
            if self.title_operation and self.title_owner_path.exists():
                if self.title_owner_path.read_bytes() != self._title_descriptor():
                    raise BridgeUnknown('title_exclusive_owner_changed')
                self.title_owner_path.unlink()
            self.lease_path.unlink()
            self._owns_lease = False

    def _title_descriptor(self) -> bytes:
        value = self.identity
        return (f'{TITLE_START_INTERFACE}\n{self.nonce}\n{self.title_operation}\n{self.title_epoch}\n'
                f'{value.pid}\n{value.creation_time}\n{value.hwnd}\n{value.tid}\nexclusive\n').encode('ascii')

    def record_title_outcome(self, outcome: str, *, cleanup_confirmed: bool) -> None:
        """Persistent process boundary; a new operation ID cannot bypass UNKNOWN."""
        if not self._title_admitted or not self.title_operation or outcome not in ('NOT_INVOKED', 'INVOKED', 'UNKNOWN'):
            raise BridgeError('title_outcome_invalid')
        with self._lease_edit():
            current = _read_json(self.title_attempt_path)
            if (not current or current.get('session_id') != self.nonce
                    or current.get('operation_id') != self.title_operation
                    or current.get('pid') != self.identity.pid
                    or current.get('process_creation_filetime') != self.identity.creation_time):
                raise BridgeUnknown('title_persistent_operation_owner_changed')
            _atomic_json(self.title_attempt_path, {'interface': TITLE_START_INTERFACE,
                **self.identity.as_dict(), 'operation_id': self.title_operation,
                'session_id': self.nonce, 'outcome': outcome,
                'cleanup_confirmed': cleanup_confirmed, 'updated_at': _now()})

    def retire_for_retry(self) -> None:
        """Retire this command channel before a new explicit connection."""
        self._check_restart_boundary()
        if self.title_operation and not self._terminal:
            raise BridgeError('title_sessions_require_confirmed_cleanup')
        self.stop_state_events()
        cleanup_error = ''
        try:
            if not self._terminal and self._owns_lease and self._bootstrap_started:
                self.request('stop', timeout=2.0)
        except Exception as exc:
            cleanup_error = str(exc)
        finally:
            self._terminal = True
            try:
                if self.session_dir.exists():
                    self._write_ledger('superseded', superseded_at=_now(),
                                       retry_cleanup_error=cleanup_error)
            finally:
                self._release_lease()

    def _stage(self) -> None:
        manifest = _read_json(self.assets / "manifest.json")
        if self.title_operation and (not manifest or manifest.get('title_start_interface') != TITLE_START_INTERFACE):
            raise BridgeError('title_start_assets_unsupported')
        if not manifest or manifest.get("protocol") != PROTOCOL or manifest.get("native_protocol") != 2 or manifest.get("bridge_id") != BRIDGE_ID or manifest.get("campaign_interface") != CAMPAIGN_INTERFACE:
            raise BridgeError("对象桥接资源缺失或协议版本不受支持。")
        if Path(manifest.get("session_root", "")).resolve() != self.root:
            raise BridgeError("对象桥接构建的会话目录不匹配。")
        game = manifest.get("supported_game")
        if (not isinstance(game, dict) or game.get("js_exe_sha256") != JS_SHA256
                or game.get("mono_sha256") != MONO_SHA256 or not isinstance(game.get("managed"), dict)
                or any(game["managed"].get(name) != digest for name, digest in MANAGED_HASHES.items())):
            raise BridgeError("对象桥接支持的游戏版本与状态读取器不一致。")
        if (manifest.get("heartbeat_seconds") != 5 or manifest.get("lease_seconds") != 20
                or manifest.get("recovery_seconds") != 10):
            raise BridgeError("对象桥接租约或恢复协议参数不匹配。")
        files = manifest.get("files")
        if not isinstance(files, dict) or set(files) != set(ASSET_NAMES):
            raise BridgeError("对象桥接资源清单不完整。")
        verified = {}
        for name in ASSET_NAMES:
            digest = files[name]
            path = self.assets / name
            if not isinstance(digest, str) or not re.fullmatch("[0-9a-f]{64}", digest) or not path.is_file() or path.is_symlink():
                raise BridgeError("对象桥接资源清单无效。")
            data = path.read_bytes()
            if hashlib.sha256(data).hexdigest() != digest:
                raise BridgeError(f"对象桥接资源校验失败：{name}")
            verified[name] = data
        bundle = hashlib.sha256(json.dumps(files, sort_keys=True, separators=(",", ":")).encode("ascii")).hexdigest()[:32]
        directory = self.root / bundle / "bin"
        directory.mkdir(parents=True, exist_ok=True)
        for name, data in verified.items():
            target = directory / name
            try:
                with target.open("xb") as stream:
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
            except FileExistsError:
                pass
            if hashlib.sha256(target.read_bytes()).hexdigest() != files[name]:
                raise BridgeError("对象桥接缓存资源校验失败；保留原文件，请检查安装。")
        staged_manifest = directory / "manifest.json"
        if not staged_manifest.exists():
            try:
                with staged_manifest.open("x", encoding="ascii") as stream:
                    json.dump(manifest, stream, ensure_ascii=True)
                    stream.flush()
                    os.fsync(stream.fileno())
            except FileExistsError:
                pass
        elif _read_json(staged_manifest) != manifest:
            raise BridgeError("对象桥接缓存清单不一致；不覆盖已加载资源。")
        self.bin_dir = directory
        self.session_dir.mkdir(exist_ok=False)
        if self.title_operation:
            _atomic_bytes(self.session_dir / 'title-session.txt', self._title_descriptor())
        self._write_ledger("staged", asset_hashes=dict(files), build_id=manifest.get("build_id", ""))

    def _drain_host(self) -> None:
        try:
            for line in self.host.stdout:
                if len(line) > 4096:
                    self._native_error = "native_status_line_too_long"
                    continue
                try:
                    value = json.loads(line)
                    if isinstance(value, dict):
                        self.native_events.append(value)
                except (ValueError, UnicodeError):
                    self._native_error = "native_status_invalid"
        except (OSError, ValueError):
            self._native_error = "native_status_closed"

    def _state(self, *, read_stats: dict | None = None,
               read_timeout: float = _READ_RETRY_TIMEOUT) -> dict | None:
        state = _read_json(self.session_dir / "state.json", read_stats=read_stats, retry_timeout=read_timeout,
                           retry_missing=bool(self._last_state))
        if state is None:
            return None
        self._common_valid(state)
        if self._same_process_recovery is not None and not same_json(state.get('same_process_recovery'), self._same_process_recovery):
            raise BridgeUnknown('same_process_recovery_grant_missing_or_changed')
        lifecycle = state.get("lifecycle")
        if lifecycle not in {"bootstrapping", "ready", "action", "restoring", "recovery", "stopping", "stopped", "recovery_unknown", "failed"}:
            raise BridgeUnknown("桥接生命周期回执无效。")
        self._capture_recovery(state)
        if self.title_operation and state.get('ready') is True:
            self._validate_title_state(state)
        # Domain schemas decide business admission, never whether the
        # transport can connect or keep its lease alive.
        errors = {}
        if not self.title_operation and state.get('ready') is True and (
                not isinstance(state.get('campaign'), dict)
                or state['campaign'].get('interface') != CAMPAIGN_INTERFACE):
            errors['campaign'] = 'campaign_interface_unsupported'
        from .daily_popup import valid_popup
        validators = {'development': _development_valid, 'player_refresh': _player_refresh_valid,
            'daily_refresh': _daily_refresh_valid, 'daily_popup': valid_popup,
            'daily_reward': _reward_valid, 'daily_timed': _timed_valid, 'daily_stone': _stone_valid,
            'daily_mingshi': _mingshi_valid, 'daily_refine': _refine_valid, 'daily_arena': _arena_valid,
            'daily_soul': _soul_valid, 'daily_wood': _wood_valid}
        for key, validator in validators.items():
            if state.get(key) is not None:
                try:
                    if not validator(state[key]):
                        if key == 'daily_soul':
                            from .daily_soul import _snapshot_error
                            errors[key] = _snapshot_error(state[key])
                        else:
                            errors[key] = 'snapshot_schema_invalid'
                except (ValueError, TypeError, KeyError, AttributeError) as exc:
                    errors[key] = 'snapshot_schema_invalid:' + type(exc).__name__
            enabled = key + '_enabled'
            if enabled in state and type(state[enabled]) is not bool:
                errors[key] = 'capability_flag_invalid'
        for key in ('daily_stone', 'daily_mingshi', 'daily_refine', 'daily_arena', 'daily_soul', 'daily_wood'):
            previous, current = self._last_state.get(key), state.get(key)
            enabled_key = key + '_enabled'
            if self._last_state.get(enabled_key) is True and state.get(enabled_key) is not True:
                errors[key] = 'capability_changed'
            if isinstance(previous, dict) and current is None and state.get(enabled_key) is True:
                errors[key] = 'snapshot_missing'
            if (isinstance(previous, dict) and isinstance(current, dict)
                    and type(previous.get('revision')) is int and type(current.get('revision')) is int
                    and (current['revision'] < previous['revision']
                         or current['revision'] == previous['revision'] and current != previous)):
                errors[key] = 'snapshot_revision_or_content_regressed'
        self._domain_validation_errors = errors
        diagnostics = state.get('business_diagnostics')
        diagnostics = dict(diagnostics) if isinstance(diagnostics, dict) else {}
        if errors:
            diagnostics['validation_errors'] = dict(errors)
        if self._historical_recovery:
            diagnostics['historical_unknown'] = [dict(item) for item in self._historical_recovery]
        if diagnostics:
            state['business_diagnostics'] = diagnostics
        sequence = state.get("last_command_seq")
        if type(sequence) is not int or not 0 <= sequence <= self._seq:
            raise BridgeUnknown("桥接状态序号超出当前控制器请求范围。")
        if self._last_state.get("last_command_seq", 0) > state.get("last_command_seq", -1):
            raise BridgeUnknown("桥接状态序号倒退。")
        updated = state.get("updated_utc")
        observed = datetime.now(timezone.utc)
        age = None
        try:
            instant = datetime.fromisoformat(updated.replace("Z", "+00:00"))
            age = (observed - instant).total_seconds()
            if not _is_clean(state) and not -2.0 <= age <= 10.0:
                raise ValueError("stale state")
        except (AttributeError, TypeError, ValueError) as exc:
            error = BridgeUnknown("桥接状态回执已过期或时间格式无效。")
            error.state_validation_predicate = 'transport_state_freshness'
            error.state_validation_snapshot = dict(updated_utc=updated, observed_utc=observed.isoformat(),
                age_seconds=age, last_command_seq=state.get('last_command_seq'))
            error.filename = str(self.session_dir / 'state.json')
            raise error from exc
        self._last_state = state
        self._on_state(dict(state))
        return state

    def _validate_title_state(self, state: dict) -> dict:
        title = state.get('title_start')
        if (not isinstance(title, dict) or title.get('interface') != TITLE_START_INTERFACE
                or title.get('operation_id') != self.title_operation
                or title.get('outcome') not in ('NOT_INVOKED', 'INVOKED', 'UNKNOWN')
                or title.get('hwnd') != self.identity.hwnd
                or type(title.get('consumed')) is not bool
                or type(title.get('instance_id')) is not int
                or not isinstance(title.get('gate'), str) or not isinstance(title.get('reason'), str)):
            raise BridgeUnknown('title_receipt_invalid')
        return title

    def title_observe(self) -> dict:
        if not self.title_operation:
            raise BridgeError('title_session_required')
        return self.request('title-observe')

    def title_start(self) -> dict:
        if not self.title_operation:
            raise BridgeError('title_session_required')
        # Publication starts the durable ambiguity boundary, before command I/O.
        self.record_title_outcome('UNKNOWN', cleanup_confirmed=False)
        return self._request('title-start')

    def _target_status(self) -> str:
        return self._status_reader(self.identity.pid, self.identity.creation_time)

    def target_exited(self) -> bool:
        if self._target_status() != "gone":
            return False
        self._terminal = True
        self._close_battle_guard(target_gone=True)
        self.stop_state_events()
        self._unknown = False
        if self.ledger_path.parent.exists():
            self._write_ledger("target_exited", component_destroyed=False, reason="process_exit_boundary")
        self._release_lease()
        return True

    def mark_unknown(self, reason: str | Exception) -> None:
        self.stop_state_events()
        with self._ledger_lock:
            self._unknown = True
            details = {"message": str(reason), "exception_type": type(reason).__name__ if isinstance(reason, Exception) else "",
                       "observed_at": _now(), "command_seq": self._seq,
                       "command_name": self._ledger.get("pending_command"), "stage": self._pending_stage}
            if isinstance(reason, Exception):
                for field in ("winerror", "errno", "filename", "filename2", "publication_attempts", "publication_elapsed_ms", "publication_stage",
                              "read_attempts", "read_elapsed_ms", "read_stage",
                              'read_first_winerror', 'read_first_errno', 'read_first_stage', 'read_first_message',
                              'read_last_winerror', 'read_last_errno', 'read_last_stage', 'read_last_message'):
                    value = getattr(reason, field, None)
                    if isinstance(value, (str, int, float)) and not isinstance(value, bool):
                        details[field] = value
                predicate = getattr(reason, 'state_validation_predicate', None)
                snapshot = getattr(reason, 'state_validation_snapshot', None)
                if isinstance(predicate, str) and isinstance(snapshot, dict):
                    details['state_validation_predicate'] = predicate
                    details['state_validation_snapshot'] = snapshot
            if self._first_unknown is None:
                self._first_unknown = details
            if self.session_dir.exists():
                self._write_ledger("unknown", reason=self._first_unknown["message"],
                                   first_unknown=dict(self._first_unknown), last_unknown=details,
                                   host_pid=getattr(self.host, "pid", None))

    def connect(self, cancel: threading.Event | None = None, timeout: float = 12.0) -> dict:
        self.revalidate()
        self._acquire_lease()
        try:
            self._stage()
            self.revalidate()
            self._check_restart_boundary()
            if cancel is not None and cancel.is_set():
                self._write_ledger("stopped", component_destroyed=True, reason="cancelled_before_bootstrap")
                self._terminal = True
                self._release_lease()
                return {"lifecycle": "stopped", "component_destroyed": True}
            self._bootstrap_started = True
            self._write_ledger("connecting")
            command = [str(self.bin_dir / "bridge_host.exe"), "--run", f"{self.identity.hwnd:x}",
                       str(self.identity.pid), str(self.identity.tid), self.nonce, str(self.identity.creation_time)]
            try:
                self.host = self._popen(command, cwd=str(self.bin_dir), stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            except OSError:
                # Popen did not return a child. No native host was started.
                self._bootstrap_started = False
                raise
            self._write_ledger("connecting", host_pid=self.host.pid)
            threading.Thread(target=self._drain_host, name="object-native-output", daemon=True).start()
            deadline = time.monotonic() + timeout
            reads = _ReceiptReads(self.session_dir)
            while time.monotonic() < deadline:
                if self.target_exited():
                    raise BridgeError("游戏进程已退出。")
                self.revalidate()
                state = reads.read('state.json', lambda wait: self._state(read_timeout=wait), deadline)
                if state and state.get("lifecycle") == "ready" and state.get("ready") is True and state.get("main_thread_verified") is True and state.get("component_destroyed") is False:
                    self.revalidate()
                    self._write_ledger("ready")
                    if cancel is not None and cancel.is_set():
                        return self.stop()
                    return state
                if state and state.get("lifecycle") in ("recovery_unknown", "failed"):
                    raise BridgeUnknown("对象连接未能完成，恢复状态尚未确认。")
                if state and _is_clean(state):
                    self._finish_stopped(state)
                    return state
                time.sleep(0.05)
            reads.raise_pending()
            raise BridgeUnknown("对象连接等待超时，请再次点击重新连接。")
        except Exception as exc:
            if self._owns_lease:
                if not self._bootstrap_started:
                    if self.session_dir.exists():
                        self._write_ledger("stopped", component_destroyed=True, reason="bootstrap_not_started")
                    self._release_lease()
                elif not self.target_exited():
                    self.mark_unknown(exc)
            raise

    @property
    def same_process_recovery(self) -> dict:
        return dict(self._same_process_recovery or {})

    def recover_soul_connection(self, character_id: str = '', *, old_session_id: str | None = None,
                                cancel: threading.Event | None = None, timeout: float = 12.0) -> dict:
        """Compatibility entry: connection is independent of soul settlement."""
        return self.connect(cancel=cancel, timeout=timeout)

    def _check_domain_admission(self, name: str) -> None:
        domain = {'player-state-refresh': 'player_refresh', 'daily-state-refresh': 'daily_refresh'}.get(name)
        if domain is None and name.endswith(('-observe', '-command')):
            domain = name.rsplit('-', 1)[0].replace('-', '_')
        reason = self._domain_validation_errors.get(domain)
        if reason:
            raise BridgeAdmissionClosed('business_observation_invalid:' + domain + ':' + reason)

    def _send(self, name: str, *, campaign_fields: dict | None = None, development_fields: dict | None = None,
              guild_fields: dict | None = None, reward_fields: dict | None = None,
              timed_fields: dict | None = None, stone_fields: dict | None = None,
              mingshi_fields: dict | None = None, refine_fields: dict | None = None,
              arena_fields: dict | None = None, soul_fields: dict | None = None, wood_fields: dict | None = None,
              script_fields: dict | None = None, popup_fields: dict | None = None,
              admission=None, battle_epoch=None) -> int:
        self._check_domain_admission(name)
        for failure in self._business_failures.values():
            old_id = failure.get('operation_id')
            if old_id and any(isinstance(fields, dict) and fields.get('operation_id') == old_id
                    for fields in (campaign_fields, development_fields, guild_fields, reward_fields,
                                   timed_fields, stone_fields, mingshi_fields, refine_fields, arena_fields,
                                   soul_fields, wood_fields, script_fields, popup_fields)):
                raise BridgeAdmissionClosed('business_operation_unknown_replay_forbidden')
        for history in self._historical_recovery:
            old_id = history.get('old_operation_id')
            if old_id and any(isinstance(fields, dict) and fields.get('operation_id') == old_id
                    for fields in (campaign_fields, development_fields, guild_fields, reward_fields,
                                   timed_fields, stone_fields, mingshi_fields, refine_fields, arena_fields,
                                   soul_fields, wood_fields, script_fields, popup_fields)):
                raise BridgeAdmissionClosed('historical_operation_unknown_replay_forbidden')
        grant = self._same_process_recovery
        if grant is not None:
            for fields in (campaign_fields, development_fields, guild_fields, reward_fields,
                           timed_fields, stone_fields, mingshi_fields, refine_fields, arena_fields,
                           soul_fields, wood_fields, script_fields, popup_fields):
                if isinstance(fields, dict) and fields.get('operation_id') == grant['old_operation_id']:
                    raise BridgeAdmissionClosed('same_process_recovery_old_operation_quarantined')
            soul_mutation = (name == 'daily-soul-command' or name == 'development-command' and
                isinstance(development_fields, dict) and str(development_fields.get('action', '')).startswith('growth_soul_'))
            if soul_mutation and grant['soul_mutations_quarantined']:
                raise BridgeAdmissionClosed('same_process_recovery_soul_mutations_quarantined')
        if self.title_operation and name not in ('title-observe', 'title-start', 'heartbeat', 'stop'):
            raise BridgeError('title_only_session')
        if name in ('title-observe', 'title-start') and not self.title_operation:
            raise BridgeError('title_session_required')
        if name not in COMMANDS or self._terminal:
            raise BridgeError("对象连接命令无效或连接已经停止。")
        # Defense at the actual publication boundary, including private callers.
        # Validation precedes sequence allocation, ledger writes and game I/O.
        if name in ("campaign-command", "challenge-command", "elite-command"):
            campaign_fields = _validate_campaign_command_fields(campaign_fields, challenge=name == 'challenge-command', elite=name == 'elite-command',
                divinity_return=name == 'challenge-command' and _divinity_home_return_matches(self._last_state, campaign_fields))
        elif campaign_fields is not None:
            raise BridgeError("该命令不接受征战参数。")
        if name in ("development-observe", "development-command", "player-state-refresh", "daily-state-refresh"):
            if name != "development-observe" or development_fields is not None:
                development_fields = _validate_development_fields(development_fields, observe=name == "development-observe", refresh=name == "player-state-refresh", daily_refresh=name == 'daily-state-refresh')
            if name == "player-state-refresh":
                self._check_refresh_admission()
            elif name == 'daily-state-refresh':
                self._check_daily_refresh_admission()
        elif development_fields is not None:
            raise BridgeError("该命令不接受养成参数。")
        if name == 'daily-guild-command':
            guild_fields = _validate_guild_fields(guild_fields)
        elif guild_fields is not None:
            raise BridgeError('该命令不接受同盟参数。')
        if name == 'daily-reward-command':
            reward_fields = _validate_reward_fields(reward_fields)
            if self._last_state.get('daily_reward_enabled') is not True:
                raise BridgeError('日常宝箱功能尚未启用。')
        elif reward_fields is not None:
            raise BridgeError('该命令不接受日常宝箱参数。')
        if name == 'daily-timed-command':
            timed_fields = _validate_timed_fields(timed_fields)
            if self._last_state.get('daily_timed_enabled') is not True:
                raise BridgeError('限时日常功能尚未启用。')
        elif timed_fields is not None:
            raise BridgeError('该命令不接受限时日常参数。')
        if name == 'daily-stone-command':
            stone_fields = _validate_stone_fields(stone_fields)
            if self._last_state.get('daily_stone_enabled') is not True:
                raise BridgeError('采石功能尚未启用。')
        elif stone_fields is not None:
            raise BridgeError('该命令不接受采石参数。')
        if name == 'daily-mingshi-command':
            mingshi_fields = _validate_mingshi_fields(mingshi_fields)
            if self._last_state.get('daily_mingshi_enabled') is not True:
                raise BridgeError('铭石功能尚未启用。')
        elif mingshi_fields is not None:
            raise BridgeError('该命令不接受铭石参数。')
        if name == 'daily-refine-command':
            refine_fields = _validate_refine_fields(refine_fields)
            if self._last_state.get('daily_refine_enabled') is not True:
                raise BridgeError('炼魂功能尚未启用。')
        elif refine_fields is not None:
            raise BridgeError('该命令不接受炼魂参数。')
        if name == 'daily-arena-command':
            arena_fields = _validate_arena_fields(arena_fields)
            if self._last_state.get('daily_arena_enabled') is not True:
                raise BridgeError('竞技宝箱功能尚未启用。')
        elif arena_fields is not None:
            raise BridgeError('该命令不接受竞技宝箱参数。')
        if name == 'daily-soul-command':
            soul_fields = _validate_soul_fields(soul_fields)
            if self._last_state.get('daily_soul_enabled') is not True:
                raise BridgeError('聚魂功能尚未启用。')
            if not _soul_command_admitted(self._last_state.get('daily_soul'), soul_fields):
                raise BridgeAdmissionClosed('soul_current_observation_or_ownership_changed_before_submit')
        elif soul_fields is not None:
            raise BridgeError('该命令不接受聚魂参数。')
        if name == 'daily-wood-command':
            wood_fields = _validate_wood_fields(wood_fields)
            if self._last_state.get('daily_wood_enabled') is not True:
                raise BridgeError('木人试炼功能尚未启用。')
        elif wood_fields is not None:
            raise BridgeError('该命令不接受木人试炼参数。')
        if name == 'daily-script':
            if (not isinstance(script_fields, dict) or set(script_fields) != set(WOOD_FIELDS)
                    or not re.fullmatch(r'[a-z_]{1,32}', script_fields['action'])
                    or not re.fullmatch(r'[0-9a-f]{32}', script_fields['operation_id'])
                    or not _character_id_valid(script_fields['character_id'])
                    or type(script_fields['task_id']) is not int or script_fields['task_id'] not in (8, 9, 10, 11, 14, 15, 17, 23, 24, 109, 201, 202, 203, 204, 205, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310)
                    or type(script_fields['expected_revision']) is not int or script_fields['expected_revision'] < 1):
                raise BridgeError('日常脚本参数无效。')
            reward_actions = {
                201: {'observe', 'open', 'claim', 'close_reward', 'close_message', 'close'},
                202: {'observe', 'open', 'claim', 'close_reward', 'close_message', 'close'},
                203: {'observe', 'open', 'claim', 'close_reward', 'close_message', 'close'},
                204: {'observe', 'open', 'claim', 'close_reward', 'close_message', 'close'},
                205: {'observe', 'open', 'claim', 'close_reward', 'close_message', 'close'},
                301: {'observe', 'open', 'resume', 'claim_signin', 'reward_close', 'close'},
                302: {'observe', 'open', 'resume', 'claim_salary', 'claim_arena', 'claim_all_salary', 'reward_close', 'close'},
                303: {'observe', 'open', 'resume', 'copy_free', 'close'},
                304: {'observe', 'open', 'resume', 'claim_salary', 'close'},
                305: {'observe', 'open', 'resume', 'claim_salary', 'claim_arena', 'claim_point', 'close'},
                306: {'observe', 'open', 'begin', 'collect', 'close_reward', 'cleanup_no_reply'},
                307: {'observe', 'open', 'begin', 'collect', 'close_reward', 'cleanup_no_reply'},
                308: {'observe', 'open', 'open_harvest', 'collect', 'open_product', 'produce_short', 'produce_medium',
                      'produce_long', 'close_reward', 'close_product', 'finish'},
                309: {'observe', 'open', 'begin', 'claim', 'close_reward', 'close_unavailable', 'cleanup_no_reply'},
                310: {'observe', 'open', 'enter', 'open_chest', 'claim', 'close_reward',
                      'close_chest', 'return', 'close_selector', 'finish'},
            }
            if script_fields['task_id'] in reward_actions and script_fields['action'] not in reward_actions[script_fields['task_id']]:
                raise BridgeError('日常领取动作无效。')
        elif script_fields is not None:
            raise BridgeError('该命令不接受日常脚本参数。')
        if name == 'daily-popup-command':
            if (not isinstance(popup_fields, dict) or set(popup_fields) != set(POPUP_FIELDS)
                    or popup_fields.get('action') != 'confirm' or popup_fields.get('window_type') != 'BattleResultUI'
                    or not isinstance(popup_fields.get('operation_id'), str)
                    or not re.fullmatch(r'[0-9a-f]{32}', popup_fields['operation_id'])
                    or not _character_id_valid(popup_fields.get('character_id'))
                    or type(popup_fields.get('instance_id')) is not int or not -2147483648 <= popup_fields['instance_id'] <= 2147483647
                    or popup_fields['instance_id'] == 0
                    or any(type(popup_fields.get(key)) is not int or not 1 <= popup_fields[key] <= 999999999
                           for key in ('appearance_generation', 'expected_revision'))):
                raise BridgeError('奖励确认参数无效。')
            popup_fields = dict(popup_fields)
        elif popup_fields is not None:
            raise BridgeError('该命令不接受奖励确认参数。')
        if self._unknown and name != "stop":
            raise BridgeUnknown("本次对象连接结果未知，请再次点击重新连接。")
        self._pending_stage = "identity"
        self.revalidate()
        battle_command = (name in ('campaign-command', 'challenge-command', 'elite-command') or
                          name == 'daily-script' and script_fields.get('action') != 'observe')
        if battle_command:
            # A caller captures before releasing its workflow admission lock.
            # A paused/resumed request must retain that old, cancelled epoch.
            if battle_epoch is None:
                battle_epoch = self.capture_battle_epoch()
            if not isinstance(battle_epoch, str) or not re.fullmatch('[0-9a-f]{32}', battle_epoch):
                raise BridgeAdmissionClosed('battle_launch_epoch_missing')
        if admission is not None and not admission():
            raise BridgeAdmissionClosed('木人试炼命令发令期限已过。' if wood_fields is not None else '聚魂命令发令期限已过。')
        if self._seq >= 999_999_999:
            raise BridgeUnknown("对象连接命令序号已耗尽。")
        self._seq += 1
        self._pending_stage = "ledger"
        self._write_ledger("stopping" if name == "stop" else "command_pending", pending_command=name)
        data = f"version={PROTOCOL}\nnonce={self.nonce}\nsequence={self._seq}\ncommand={name}\n"
        if battle_command:
            data += f'battle_epoch={battle_epoch}\n'
        if campaign_fields is not None:
            data += "".join(f"{key}={campaign_fields[key]}\n" for key in ("action", "expected_revision", "campaign_id", "troop_id", "attempt_id"))
        if name == "elite-command":
            data += f"star={campaign_fields['star']}\ndaily_target={campaign_fields['daily_target']}\n"
        if development_fields is not None:
            data += "".join(f"{key}={development_fields[key]}\n" for key in DEVELOPMENT_FIELDS)
            if development_fields['action'].startswith('star_'):
                data += f"max_practice={development_fields['max_practice']}\n"
        if guild_fields is not None:
            data += ''.join(f'{key}={guild_fields[key]}\n' for key in GUILD_FIELDS)
        if reward_fields is not None:
            data += ''.join(f'{key}={reward_fields[key]}\n' for key in REWARD_FIELDS)
        if timed_fields is not None:
            data += ''.join(f'{key}={timed_fields[key]}\n' for key in TIMED_FIELDS)
        if stone_fields is not None:
            data += ''.join(f'{key}={stone_fields[key]}\n' for key in STONE_FIELDS)
        if mingshi_fields is not None:
            data += ''.join(f'{key}={mingshi_fields[key]}\n' for key in MINGSHI_FIELDS)
        if refine_fields is not None:
            data += ''.join(f'{key}={refine_fields[key]}\n' for key in REFINE_FIELDS)
        if arena_fields is not None:
            data += ''.join(f'{key}={arena_fields[key]}\n' for key in ARENA_FIELDS)
        if soul_fields is not None:
            data += ''.join(f'{key}={soul_fields[key]}\n' for key in SOUL_FIELDS)
        if wood_fields is not None:
            data += ''.join(f'{key}={wood_fields[key]}\n' for key in WOOD_FIELDS)
        if script_fields is not None:
            data += ''.join(f'{key}={script_fields[key]}\n' for key in WOOD_FIELDS)
        if popup_fields is not None:
            data += ''.join(f'{key}={popup_fields[key]}\n' for key in POPUP_FIELDS)
        data = data.encode("ascii")
        self._pending_stage = "command_publish"
        _atomic_bytes(self.session_dir / "command.txt", data)
        self._pending_stage = "ack_wait"
        return self._seq

    def _ack(self, seq: int, name: str, *, read_timeout: float = _READ_RETRY_TIMEOUT) -> dict | None:
        ack = _read_json(self.session_dir / "ack.json", retry_timeout=read_timeout,
                         retry_missing=bool(self._last_state))
        if ack is None:
            return None
        self._common_valid(ack)
        actual = ack.get("command_seq")
        if type(actual) is not int or actual < 1:
            raise BridgeUnknown("桥接命令回执序号无效。")
        if actual < seq:
            return None
        if actual != seq or ack.get("command_name") != name or ack.get("status") not in ("ACCEPTED", "COMPLETED", "REJECTED", "UNKNOWN"):
            raise BridgeUnknown("桥接命令回执与当前请求不匹配。")
        return ack

    def _finish_stopped(self, state: dict) -> None:
        if not _is_clean(state):
            raise BridgeUnknown("停止回执未证明组件销毁和已执行动作的恢复。")
        self.stop_state_events()
        self._close_battle_guard()
        action = state["action"]
        business = state.get("business", {})
        self._write_ledger("stopped", component_destroyed=True, restore_verified=action.get("restore_verified") is True,
                           no_action=action.get("mutation_attempted") is False,
                           no_verification_action=action.get("mutation_attempted") is False, business=business,
                           pending_command=None, final_state=state)
        self._terminal = True
        self._unknown = False
        self._release_lease()
        self._on_state(dict(state))

    def _close_battle_guard(self, *, target_gone: bool = False) -> None:
        with self._battle_guard_lock:
            if self._battle_guard is not None:
                self._battle_guard.close(confirmed_target_exit=target_gone)

    def request(self, name: str, *, timeout: float = 5.0,
                cancel: threading.Event | None = None, _campaign_fields: dict | None = None) -> dict:
        if name in ("title-start", "campaign-command", "challenge-command", "elite-command", "development-command", "player-state-refresh", "daily-state-refresh", "daily-guild-command", "daily-reward-command", "daily-timed-command", "daily-stone-command", "daily-mingshi-command", "daily-refine-command", "daily-arena-command", "daily-soul-command", "daily-wood-command", "daily-popup-command"):
            raise BridgeError("业务命令必须通过固定类型接口提交。")
        if _campaign_fields is not None:
            raise BridgeError("该命令不接受征战参数。")
        return self._request(name, timeout=timeout, cancel=cancel)

    def _request(self, name: str, *, timeout: float = 5.0,
                 cancel: threading.Event | None = None, _campaign_fields: dict | None = None,
                 _development_fields: dict | None = None, _guild_fields: dict | None = None,
                 _reward_fields: dict | None = None, _timed_fields: dict | None = None,
                 _stone_fields: dict | None = None, _mingshi_fields: dict | None = None,
                 _refine_fields: dict | None = None, _arena_fields: dict | None = None,
                 _soul_fields: dict | None = None, _wood_fields: dict | None = None,
                 _script_fields: dict | None = None, _popup_fields: dict | None = None, _admission=None,
                 _battle_epoch=None) -> dict:
        with self._request_lock:
            self._check_domain_admission(name)
            # Validate an immutable field copy while serializing this connection
            # request. Native admission protects current in-flight game work.
            if name == 'daily-timed-observe' and self._last_state.get('daily_timed_enabled') is not True:
                raise BridgeError('限时日常功能尚未启用。')
            if name == 'daily-reward-observe' and self._last_state.get('daily_reward_enabled') is not True:
                raise BridgeError('日常宝箱功能尚未启用。')
            if name == 'daily-stone-observe' and self._last_state.get('daily_stone_enabled') is not True:
                raise BridgeError('采石功能尚未启用。')
            if name == 'daily-mingshi-observe' and self._last_state.get('daily_mingshi_enabled') is not True:
                raise BridgeError('铭石功能尚未启用。')
            if name == 'daily-refine-observe' and self._last_state.get('daily_refine_enabled') is not True:
                raise BridgeError('炼魂功能尚未启用。')
            if name == 'daily-arena-observe' and self._last_state.get('daily_arena_enabled') is not True:
                raise BridgeError('竞技宝箱功能尚未启用。')
            if name == 'daily-soul-observe' and self._last_state.get('daily_soul_enabled') is not True:
                raise BridgeError('聚魂功能尚未启用。')
            if name == 'daily-wood-observe' and self._last_state.get('daily_wood_enabled') is not True:
                raise BridgeError('木人试炼功能尚未启用。')
            if name in ("campaign-command", "challenge-command", "elite-command"):
                _campaign_fields = _validate_campaign_command_fields(_campaign_fields, challenge=name == 'challenge-command', elite=name == 'elite-command',
                    divinity_return=name == 'challenge-command' and _divinity_home_return_matches(self._last_state, _campaign_fields))
            elif _campaign_fields is not None:
                raise BridgeError("该命令不接受征战参数。")
            if name in ("development-observe", "development-command", "player-state-refresh", "daily-state-refresh"):
                if name != "development-observe" or _development_fields is not None:
                    _development_fields = _validate_development_fields(_development_fields, observe=name == "development-observe", refresh=name == "player-state-refresh", daily_refresh=name == 'daily-state-refresh')
                if name == "player-state-refresh":
                    self._check_refresh_admission()
                elif name == 'daily-state-refresh':
                    self._check_daily_refresh_admission()
            elif _development_fields is not None:
                raise BridgeError("该命令不接受养成参数。")
            if name == 'daily-guild-command':
                _guild_fields = _validate_guild_fields(_guild_fields)
            elif _guild_fields is not None:
                raise BridgeError('该命令不接受同盟参数。')
            if name == 'daily-reward-command':
                _reward_fields = _validate_reward_fields(_reward_fields)
                if self._last_state.get('daily_reward_enabled') is not True:
                    raise BridgeError('日常宝箱功能尚未启用。')
            elif _reward_fields is not None:
                raise BridgeError('该命令不接受日常宝箱参数。')
            if name == 'daily-timed-command':
                _timed_fields = _validate_timed_fields(_timed_fields)
                if self._last_state.get('daily_timed_enabled') is not True:
                    raise BridgeError('限时日常功能尚未启用。')
            elif _timed_fields is not None:
                raise BridgeError('该命令不接受限时日常参数。')
            if name == 'daily-stone-command':
                _stone_fields = _validate_stone_fields(_stone_fields)
                if self._last_state.get('daily_stone_enabled') is not True:
                    raise BridgeError('采石功能尚未启用。')
            elif _stone_fields is not None:
                raise BridgeError('该命令不接受采石参数。')
            if name == 'daily-mingshi-command':
                _mingshi_fields = _validate_mingshi_fields(_mingshi_fields)
                if self._last_state.get('daily_mingshi_enabled') is not True:
                    raise BridgeError('铭石功能尚未启用。')
            elif _mingshi_fields is not None:
                raise BridgeError('该命令不接受铭石参数。')
            if name == 'daily-refine-command':
                _refine_fields = _validate_refine_fields(_refine_fields)
                if self._last_state.get('daily_refine_enabled') is not True:
                    raise BridgeError('炼魂功能尚未启用。')
            elif _refine_fields is not None:
                raise BridgeError('该命令不接受炼魂参数。')
            if name == 'daily-arena-command':
                _arena_fields = _validate_arena_fields(_arena_fields)
                if self._last_state.get('daily_arena_enabled') is not True:
                    raise BridgeError('竞技宝箱功能尚未启用。')
            elif _arena_fields is not None:
                raise BridgeError('该命令不接受竞技宝箱参数。')
            if name == 'daily-soul-command':
                _soul_fields = _validate_soul_fields(_soul_fields)
                if self._last_state.get('daily_soul_enabled') is not True:
                    raise BridgeError('聚魂功能尚未启用。')
            elif _soul_fields is not None:
                raise BridgeError('该命令不接受聚魂参数。')
            if name == 'daily-wood-command':
                _wood_fields = _validate_wood_fields(_wood_fields)
                if self._last_state.get('daily_wood_enabled') is not True:
                    raise BridgeError('木人试炼功能尚未启用。')
            elif _wood_fields is not None:
                raise BridgeError('该命令不接受木人试炼参数。')
            current_name = "stop" if name == "disable-restore" and cancel is not None and cancel.is_set() else name
            business_validation = False
            try:
                if name in ("campaign-command", "challenge-command", "elite-command", "development-command", "player-state-refresh", "daily-state-refresh", "daily-guild-command", "daily-reward-command", "daily-timed-command", "daily-stone-command", "daily-mingshi-command", "daily-refine-command", "daily-arena-command", "daily-soul-command", "daily-wood-command", "daily-popup-command") and cancel is not None and cancel.is_set():
                    return {"command": name, "cancelled_before_submit": True, "state": self.last_state}
                if _popup_fields is not None:
                    seq = self._send(current_name, popup_fields=_popup_fields, admission=_admission)
                elif _script_fields is not None:
                    seq = self._send(current_name, script_fields=_script_fields, battle_epoch=_battle_epoch)
                elif _wood_fields is not None:
                    seq = self._send(current_name, wood_fields=_wood_fields, admission=_admission)
                elif _soul_fields is not None:
                    seq = self._send(current_name, soul_fields=_soul_fields, admission=_admission)
                elif _arena_fields is not None:
                    seq = self._send(current_name, arena_fields=_arena_fields)
                elif _refine_fields is not None:
                    seq = self._send(current_name, refine_fields=_refine_fields)
                elif _mingshi_fields is not None:
                    seq = self._send(current_name, mingshi_fields=_mingshi_fields)
                elif _stone_fields is not None:
                    seq = self._send(current_name, stone_fields=_stone_fields)
                elif _timed_fields is not None:
                    seq = self._send(current_name, timed_fields=_timed_fields)
                elif _reward_fields is not None:
                    seq = self._send(current_name, reward_fields=_reward_fields)
                elif _guild_fields is not None:
                    seq = self._send(current_name, guild_fields=_guild_fields)
                elif _development_fields is not None:
                    seq = self._send(current_name, development_fields=_development_fields)
                elif _campaign_fields is not None:
                    seq = self._send(current_name, campaign_fields=_campaign_fields, battle_epoch=_battle_epoch)
                else:
                    seq = self._send(current_name)
                requested_seq = seq
                deadline = time.monotonic() + timeout
                reads = _ReceiptReads(self.session_dir)
                business_validation = False
                while time.monotonic() < deadline:
                    business_validation = False
                    if self.target_exited():
                        raise BridgeError("游戏进程已退出。")
                    self.revalidate()
                    state = reads.read('state.json', lambda wait: self._state(read_timeout=wait), deadline)
                    if state and _is_clean(state):
                        # Lease expiry can destroy the component before a new
                        # queued command is consumed. This proves cleanup, never
                        # success of the unacknowledged current action.
                        self._finish_stopped(state)
                        return {"command": "stop", "state": state,
                                "requested_command": name, "requested_seq": requested_seq,
                                "interrupted_command": current_name, "reason": "component_stopped"}
                    ack = reads.read('ack.json', lambda wait: self._ack(seq, current_name, read_timeout=wait), deadline)
                    if ack and ack["status"] == "UNKNOWN":
                        raise BridgeUnknown("桥接命令恢复结果未知。")
                    if ack and ack["status"] in ("COMPLETED", "REJECTED"):
                        # State and ack are independently atomically published.
                        # Wait for the state belonging to this exact command;
                        # a prior successful action cannot prove this one.
                        if not state or state.get("last_command_seq") != seq:
                            time.sleep(0.05)
                            continue
                        result = ack.get("result")
                        if not isinstance(result, dict):
                            raise BridgeUnknown("桥接完成回执缺少结构化结果。")
                        business_validation = ack['status'] == 'COMPLETED' and (
                            current_name.startswith(('daily-', 'campaign-', 'challenge-', 'elite-', 'development-'))
                            or current_name == 'player-state-refresh')
                        if current_name in ('daily-popup-observe', 'daily-popup-command') and ack['status'] == 'COMPLETED':
                            from .daily_popup import valid_popup
                            popup = result.get('daily_popup')
                            if not valid_popup(popup):
                                raise BridgeUnknown('奖励确认观察回执无效。')
                            if current_name == 'daily-popup-command':
                                command = popup['command']
                                if (not _popup_fields or any(command.get(key) != _popup_fields[key]
                                        for key in ('operation_id', 'instance_id', 'appearance_generation'))
                                        or command.get('status') != 'COMPLETED' or command.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('奖励确认回执与当前窗口不一致。')
                        if current_name == "daily-observe" and ack["status"] == "COMPLETED":
                            from .daily import valid_observation
                            observed = result.get("daily")
                            if not valid_observation(observed):
                                raise BridgeUnknown("日常观察回执缺失或无效。")
                            character_id = (state.get("campaign") or {}).get("character_id")
                            if (observed["available"] and character_id and character_id != "0"
                                    and observed["character_id"] != character_id):
                                raise BridgeUnknown("日常观察的角色与当前连接不一致。")
                        if current_name in ('daily-guild-observe', 'daily-guild-command') and ack['status'] == 'COMPLETED':
                            observed, state_guild = result.get('daily_guild'), state.get('daily_guild')
                            if (not _guild_valid(observed) or not _guild_valid(state_guild)
                                    or observed['character_id'] != state_guild['character_id']
                                    or observed['guild_id'] != state_guild['guild_id']):
                                raise BridgeUnknown('同盟观察回执缺失、无效或身份不一致。')
                            if state_guild['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            if current_name == 'daily-guild-command':
                                operation = observed['operation'] or {}
                                gui = state.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if (type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and
                                        gui_phase in ('queued', 'consumed', 'owned'))):
                                    time.sleep(.05)
                                    continue
                                if (observed['character_id'] != _guild_fields['character_id']
                                        or observed['guild_id'] != _guild_fields['guild_id']
                                        or any(operation.get(key) != _guild_fields[key] for key in
                                            ('operation_id', 'task_id', 'donation_type', 'max_premium'))
                                        or gui.get('command_seq') != seq or gui.get('phase') != 'completed'
                                        or gui.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('同盟动作回执未关联到本次命令。')
                        if current_name in ('daily-reward-observe', 'daily-reward-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_reward'), state.get('daily_reward')
                            if (not _reward_valid(observed) or not _reward_valid(current)
                                    or type(result.get('daily_reward_enabled')) is not bool
                                    or result['daily_reward_enabled'] is not state.get('daily_reward_enabled')
                                    or observed['character_id'] != current['character_id']):
                                raise BridgeUnknown('日常宝箱观察回执缺失、无效或身份不一致。')
                            if current['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            if current_name == 'daily-reward-command':
                                gui = state.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                    time.sleep(.05)
                                    continue
                                if (result['daily_reward_enabled'] is not True
                                        or observed['character_id'] != _reward_fields['character_id']
                                        or observed['operation_id'] != _reward_fields['operation_id']
                                        or observed['category_id'] != _reward_fields['category_id']
                                        or observed['sequence'] != seq or current['sequence'] != seq
                                        or gui_seq != seq or gui_phase != 'completed'
                                        or gui.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('日常宝箱动作回执未关联到本次命令。')
                        if current_name in ('daily-timed-observe', 'daily-timed-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_timed'), state.get('daily_timed')
                            enabled = result.get('daily_timed_enabled')
                            if type(enabled) is not bool or enabled is not state.get('daily_timed_enabled'):
                                raise BridgeUnknown('限时日常功能标记不一致。')
                            if not enabled and observed is None and current is None and current_name == 'daily-timed-observe':
                                pass
                            else:
                                if (not _timed_valid(observed) or not _timed_valid(current)
                                        or observed['character_id'] != current['character_id']):
                                    raise BridgeUnknown('限时日常观察回执缺失、无效或身份不一致。')
                                if current['revision'] < observed['revision']:
                                    time.sleep(.05)
                                    continue
                                if current_name == 'daily-timed-command':
                                    op = observed['operation']
                                    gui = state.get('campaign_gui') or {}
                                    gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                    if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                        time.sleep(.05)
                                        continue
                                    if (not enabled or observed['character_id'] != _timed_fields['character_id']
                                            or op['operation_id'] != _timed_fields['operation_id']
                                            or op['task_id'] != _timed_fields['task_id'] or op['sequence'] != seq
                                            or op['stage'] != _timed_fields['action'] or gui_seq != seq
                                            or gui_phase != 'completed' or gui.get('invocation_attempted') is not True):
                                        raise BridgeUnknown('限时日常动作回执未关联到本次命令。')
                        if current_name in ('daily-stone-observe', 'daily-stone-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_stone'), state.get('daily_stone')
                            enabled = result.get('daily_stone_enabled')
                            if (enabled is not True or state.get('daily_stone_enabled') is not True
                                    or not _stone_valid(observed) or not _stone_valid(current)
                                    or observed['view']['role_id'] != current['view']['role_id']):
                                raise BridgeUnknown('采石观察回执缺失、无效或身份不一致。')
                            if current['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            if current['revision'] == observed['revision'] and current != observed:
                                raise BridgeUnknown('采石状态与同版本回执不一致。')
                            old_op, new_op = observed['operation'], current['operation']
                            if (any(old_op[key] and not new_op[key] for key in
                                    ('mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed'))
                                    or old_op['status'] in ('COMPLETED', 'REJECTED', 'UNKNOWN')
                                       and new_op['status'] != old_op['status']):
                                raise BridgeUnknown('采石回执后的操作证据不一致。')
                            if current_name == 'daily-stone-observe':
                                previous_op, current_op = observed['operation'], current['operation']
                                if (current_op['operation_id'] != previous_op['operation_id']
                                        or current_op['sequence'] != previous_op['sequence']
                                        or current['view']['cycle'] != observed['view']['cycle']
                                        and not (_stone_pending_busy(current) or _stone_pending_busy(observed))):
                                    raise BridgeUnknown('采石观察期间操作或周期发生变化。')
                            if current_name == 'daily-stone-command':
                                op = observed['operation']
                                gui = state.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                    time.sleep(.05)
                                    continue
                                if (observed['view']['role_id'] != _stone_fields['character_id']
                                        or op['operation_id'] != _stone_fields['operation_id'] or op['sequence'] != seq
                                        or current['view']['cycle'] != observed['view']['cycle']
                                        and not (_stone_pending_busy(current) or _stone_pending_busy(observed))
                                        or current['operation']['operation_id'] != _stone_fields['operation_id']
                                        or current['operation']['sequence'] != seq
                                        or observed['revision'] <= _stone_fields['expected_revision']
                                        or gui_seq != seq or gui_phase != 'completed'
                                        or gui.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('采石动作回执未关联到本次命令。')
                        if current_name in ('daily-mingshi-observe', 'daily-mingshi-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_mingshi'), state.get('daily_mingshi')
                            if (result.get('daily_mingshi_enabled') is not True
                                    or state.get('daily_mingshi_enabled') is not True
                                    or not _mingshi_valid(observed) or not _mingshi_valid(current)
                                    or observed['view']['role_id'] != current['view']['role_id']):
                                raise BridgeUnknown('铭石观察回执缺失、无效或身份不一致。')
                            if current['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            if current['revision'] == observed['revision'] and current != observed:
                                raise BridgeUnknown('铭石状态与同版本回执不一致。')
                            old_op, new_op = observed['operation'], current['operation']
                            if (observed['view']['cycle'] != current['view']['cycle']
                                    or old_op['operation_id'] != new_op['operation_id']
                                    or old_op['sequence'] != new_op['sequence']
                                    or any(old_op[key] and not new_op[key] for key in
                                           ('mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed'))
                                    or old_op['status'] in ('SUCCEEDED', 'REJECTED', 'UNKNOWN')
                                       and new_op['status'] != old_op['status']):
                                raise BridgeUnknown('铭石观察期间操作或周期发生变化。')
                            if current_name == 'daily-mingshi-command':
                                gui = state.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                    time.sleep(.05)
                                    continue
                                if (observed['view']['role_id'] != _mingshi_fields['character_id']
                                        or old_op['operation_id'] != _mingshi_fields['operation_id']
                                        or old_op['sequence'] != seq or observed['revision'] <= _mingshi_fields['expected_revision']
                                        or gui_seq != seq or gui_phase != 'completed'
                                        or gui.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('铭石动作回执未关联到本次命令。')
                        if current_name in ('daily-refine-observe', 'daily-refine-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_refine'), state.get('daily_refine')
                            if (result.get('daily_refine_enabled') is not True
                                    or state.get('daily_refine_enabled') is not True
                                    or not _refine_valid(observed) or not _refine_valid(current)):
                                raise BridgeUnknown('炼魂观察回执缺失或无效。')
                            if current['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            if current['revision'] == observed['revision'] and current != observed:
                                raise BridgeUnknown('炼魂状态与同版本回执不一致。')
                            old_view, new_view = observed['view'], current['view']
                            old_op, new_op = observed['operation'], current['operation']
                            old_busy, new_busy = _refine_busy(observed), _refine_busy(current)
                            role_unknown = (old_busy and old_view['role_id'] == '0'
                                            or new_busy and new_view['role_id'] == '0')
                            if (old_view['role_id'] != new_view['role_id'] and not role_unknown
                                    or old_view['cycle'] != new_view['cycle'] and not (old_busy or new_busy)
                                    or old_op['operation_id'] != new_op['operation_id']
                                    or old_op['sequence'] != new_op['sequence']
                                    or old_op['operation_id'] and old_op.get('mode', 'coin') != new_op.get('mode', 'coin')
                                    or any(old_op[key] and not new_op[key] for key in
                                           ('mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed'))
                                    or old_op['account_progress_observed'] and
                                       any(old_op[key] != new_op[key] for key in ('coins_spent', 'practice_credit'))
                                    or old_op['account_progress_observed'] and
                                       old_op.get('experience_spent', 0) != new_op.get('experience_spent', 0)
                                    or old_op['panel_closed'] and new_op['ui_ownership_held']
                                    or old_op['status'] in ('COMPLETED', 'REJECTED', 'UNKNOWN')
                                       and new_op['status'] != old_op['status']):
                                raise BridgeUnknown('炼魂观察期间操作、角色或周期发生变化。')
                            if current_name == 'daily-refine-command':
                                gui = state.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                    time.sleep(.05)
                                    continue
                                busy_closed = (_refine_fields['action'] == 'close' and _refine_busy(observed)
                                               and old_op['status'] == 'COMPLETED'
                                               and old_view['role_id'] in ('0', _refine_fields['character_id']))
                                if (old_view['role_id'] != _refine_fields['character_id'] and not busy_closed
                                        or old_op['operation_id'] != _refine_fields['operation_id']
                                        or old_op['sequence'] != seq or observed['revision'] <= _refine_fields['expected_revision']
                                        or gui_seq != seq or gui_phase != 'completed'
                                        or gui.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('炼魂动作回执未关联到本次命令。')
                        if current_name in ('daily-arena-observe', 'daily-arena-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_arena'), state.get('daily_arena')
                            if (result.get('daily_arena_enabled') is not True or state.get('daily_arena_enabled') is not True
                                    or not _arena_valid(observed) or not _arena_valid(current)
                                    or observed['character_id'] != current['character_id']):
                                raise BridgeUnknown('竞技宝箱观察回执缺失、无效或身份不一致。')
                            if current['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            if (current['revision'] == observed['revision'] and current != observed
                                    or observed['refresh_cycle'] != current['refresh_cycle']
                                    or observed['operation_id'] != current['operation_id']
                                    or observed['sequence'] != current['sequence']
                                    or any(observed[k] != current[k] for k in
                                           ('rearmed_operation_id', 'rearmed_sequence', 'rearmed_revision'))
                                    or any(observed[k] and not current[k] for k in
                                           ('invoked', 'callback_returned', 'settled', 'activity_push_returned'))
                                    or any(observed[k] is not None and observed[k] != current[k] for k in
                                           ('gold_before', 'counter_before', 'receipt_result', 'receipt_gold_consume'))
                                    or observed['navigation_pending'] and not current['navigation_pending']
                                       and current['status'] not in ('COMPLETED', 'REJECTED', 'UNKNOWN', 'STOPPED', 'BLOCKED', 'UNAVAILABLE')
                                    or observed['status'] in ('COMPLETED', 'UNKNOWN', 'STOPPED')
                                       and current['status'] != observed['status']):
                                raise BridgeUnknown('竞技宝箱回执后的状态或证据不一致。')
                            if current_name == 'daily-arena-command':
                                if _arena_fields['action'] == 'rearm':
                                    from .daily_arena_chest import _rearmed_snapshot
                                    if not _rearmed_snapshot(observed, character_id=_arena_fields['character_id'],
                                            operation_id=_arena_fields['operation_id'],
                                            expected_revision=_arena_fields['expected_revision'], sequence=seq):
                                        raise BridgeUnknown('竞技宝箱重新扫描回执未关联到已完成操作。')
                                else:
                                    gui, ack_gui = state.get('campaign_gui') or {}, result.get('campaign_gui') or {}
                                    gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                    if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                        time.sleep(.05)
                                        continue
                                    if (observed['character_id'] != _arena_fields['character_id']
                                            or observed['operation_id'] != _arena_fields['operation_id']
                                            or observed['sequence'] != seq or observed['revision'] <= _arena_fields['expected_revision']
                                            or gui_seq != seq or gui_phase != 'completed' or gui.get('invocation_attempted') is not True
                                            or ack_gui.get('command_seq') != seq or ack_gui.get('phase') != 'completed'
                                            or ack_gui.get('invocation_attempted') is not True):
                                        raise BridgeUnknown('竞技宝箱动作回执未关联到本次命令。')
                        if current_name in ('daily-soul-observe', 'daily-soul-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_soul'), state.get('daily_soul')
                            if (result.get('daily_soul_enabled') is not True or state.get('daily_soul_enabled') is not True
                                    or not _soul_valid(observed) or not _soul_valid(current)):
                                raise BridgeUnknown('聚魂观察回执缺失或无效。')
                            if current['revision'] < observed['revision']:
                                time.sleep(.05)
                                continue
                            old_op, new_op = observed['operation'], current['operation']
                            idle_unbound = (not old_op['operation_id'] and not new_op['operation_id']
                                            and old_op['status'] == new_op['status'] == 'IDLE')
                            from .daily_soul import _receipt_matches_operation
                            finished_bound = (old_op['status'] == new_op['status'] == 'COMPLETED'
                                and _receipt_matches_operation(observed) and _receipt_matches_operation(current)
                                and observed['historical_receipt'] == current['historical_receipt'])
                            if (current['revision'] == observed['revision'] and current != observed
                                    or observed['character_id'] != current['character_id'] and not (idle_unbound or finished_bound)
                                    or observed['cycle'] != current['cycle'] and not (idle_unbound or finished_bound)
                                    or old_op['operation_id'] != new_op['operation_id']
                                    or old_op['sequence'] != new_op['sequence'] or old_op['stage'] != new_op['stage']
                                    or any(old_op[k] and not new_op[k] for k in
                                           ('invoked', 'mutation_attempted', 'start_verified', 'harvest_verified'))
                                    or old_op['counter_after'] is not None and old_op['counter_after'] != new_op['counter_after']
                                    or old_op['status'] in ('COMPLETED', 'UNKNOWN', 'STOPPED', 'REJECTED')
                                       and new_op['status'] != old_op['status']):
                                raise BridgeUnknown('聚魂回执后的状态或证据不一致。')
                            if current_name == 'daily-soul-command':
                                gui, ack_gui = state.get('campaign_gui') or {}, result.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                    time.sleep(.05)
                                    continue
                                if (observed['character_id'] != _soul_fields['character_id']
                                        or old_op['operation_id'] != _soul_fields['operation_id']
                                        or old_op['sequence'] != seq or old_op['stage'] != _soul_fields['action']
                                        or old_op.get('mode') != _soul_fields['soul_mode']
                                        or old_op.get('free_only') is not bool(_soul_fields['free_only'])
                                        or observed['revision'] <= _soul_fields['expected_revision']
                                        or gui_seq != seq or gui_phase != 'completed' or gui.get('invocation_attempted') is not True
                                        or ack_gui.get('command_seq') != seq or ack_gui.get('phase') != 'completed'
                                        or ack_gui.get('invocation_attempted') is not True):
                                    raise BridgeUnknown('聚魂动作回执未关联到本次命令。')
                        if current_name in ('daily-wood-observe', 'daily-wood-command') and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_wood'), state.get('daily_wood')
                            if (_wood_valid(observed) and _wood_valid(current)
                                    and current['revision'] < observed['revision']):
                                time.sleep(.05)
                                continue
                            if current_name == 'daily-wood-command':
                                gui = state.get('campaign_gui') or {}
                                gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                                if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                    time.sleep(.05)
                                    continue
                            if not _wood_receipt_valid(result, state, _wood_fields, seq):
                                raise BridgeUnknown('木人试炼回执后的状态或命令证据不一致。')
                        if current_name in ("campaign-observe", "campaign-command", "challenge-observe", "challenge-command", "elite-observe", "elite-command"):
                            scope = 'elite' if current_name.startswith('elite-') else 'challenge' if current_name.startswith('challenge-') else 'campaign'
                            valid = _elite_valid if scope == 'elite' else _challenge_valid if scope == 'challenge' else _campaign_valid
                            observed, state_observed = result.get(scope), state.get(scope)
                            if (not valid(observed) or not valid(state_observed)
                                    or observed['character_id'] != state_observed['character_id']):
                                raise BridgeUnknown("征战观察回执缺失、无效或与本次状态不一致。")
                            # ACK and state are separate atomic publications.
                            # The scene can advance again after the ACK; wait
                            # for an at-least-as-new state instead of treating
                            # a queued/older UI snapshot as a failed command.
                            if state_observed['revision'] < observed['revision']:
                                time.sleep(0.05)
                                continue
                            if current_name in ("campaign-command", "challenge-command", "elite-command"):
                                command = result.get(scope + '_command')
                                if (not isinstance(command, dict) or command.get("command_seq") != seq
                                        or command.get("action") != _campaign_fields["action"]
                                        or command.get("attempt_id") != _campaign_fields["attempt_id"]
                                        or command.get("invoked") is not (ack["status"] == "COMPLETED")):
                                    raise BridgeUnknown("征战动作完成回执未关联到本次命令。")
                                gui = state.get(scope + '_gui') or {}
                                if gui.get('command_seq') == seq and gui.get('phase') in ('queued', 'consumed', 'invoking'):
                                    time.sleep(0.05)
                                    continue
                            self._write_ledger("ready", **{scope: state_observed}, business=state.get("business", {}))
                        if current_name in ("development-observe", "development-command"):
                            observed, state_observed = result.get("development"), state.get("development")
                            if (not _development_valid(observed) or not _development_valid(state_observed)
                                    or observed['character_id'] != state_observed['character_id']):
                                raise BridgeUnknown("养成观察回执缺失、无效或与本次状态不一致。")
                            if state_observed['revision'] < observed['revision']:
                                time.sleep(0.05)
                                continue
                            if current_name == "development-command":
                                command = observed["last_command"]
                                statuses = {"INVOKED"} if ack["status"] == "COMPLETED" else {"REJECTED", "NOT_INVOKED"}
                                if (not command or command["sequence"] != seq or command["action"] != _development_fields["action"]
                                        or command["operation_id"] != _development_fields["operation_id"]
                                        or command["status"] not in statuses):
                                    raise BridgeUnknown("养成动作完成回执未关联到本次命令。")
                                if state_observed['last_command'] != command:
                                    time.sleep(0.05)
                                    continue
                            self._write_ledger("ready", development=state_observed, business=state.get("business", {}))
                        if current_name == "player-state-refresh":
                            observed, state_observed = result.get("player_refresh"), state.get("player_refresh")
                            if (not _player_refresh_valid(observed) or observed != state_observed
                                    or observed["sequence"] != seq or observed["operation_id"] != _development_fields["operation_id"]
                                    or observed["expected_revision"] != _development_fields["expected_revision"]):
                                raise BridgeUnknown("状态刷新未关联到本次固定命令。")
                            statuses = {"WAITING", "OBSERVED"} if ack["status"] == "COMPLETED" else {"REJECTED", "STOPPED"}
                            if observed["status"] not in statuses:
                                raise BridgeUnknown("状态刷新调用结果未确认。")
                            self._write_ledger("ready", player_refresh=observed, business=state.get("business", {}))
                        if current_name == 'daily-state-refresh' and ack['status'] == 'COMPLETED':
                            observed, current = result.get('daily_refresh'), state.get('daily_refresh')
                            if (not _daily_refresh_valid(observed) or not _daily_refresh_valid(current)
                                    or observed['sequence'] != seq or current['sequence'] != seq
                                    or observed['operation_id'] != _development_fields['operation_id']
                                    or current['operation_id'] != _development_fields['operation_id']
                                    or observed['expected_revision'] != _development_fields['expected_revision']
                                    or current['expected_revision'] != _development_fields['expected_revision']
                                    or observed['character_id'] != current['character_id']
                                    or observed['invoked'] is not True
                                    or observed['status'] not in ('WAITING', 'OBSERVED')
                                    or current['status'] not in ('WAITING', 'OBSERVED')):
                                raise BridgeUnknown('日常刷新回执未关联到本次固定命令。')
                            gui = state.get('campaign_gui') or {}
                            gui_seq, gui_phase = gui.get('command_seq'), gui.get('phase')
                            if type(gui_seq) is int and (gui_seq < seq or gui_seq == seq and gui_phase in ('queued', 'consumed', 'owned')):
                                time.sleep(.05)
                                continue
                            if gui_seq != seq or gui_phase != 'completed' or gui.get('invocation_attempted') is not True:
                                raise BridgeUnknown('日常刷新界面调用未确认。')
                        if current_name == "stop":
                            if (ack["status"] != "COMPLETED" or not _is_clean(state)
                                    or result.get("component_destroyed") is not True or result.get("state") != "stopped"):
                                raise BridgeUnknown("停止请求尚未确认恢复和组件销毁。")
                            self._finish_stopped(state)
                        elif ack["status"] == "REJECTED":
                            self._write_ledger("ready", pending_command=None)
                        else:
                            if current_name == "disable-restore":
                                action, state_action = result.get("action"), state.get("action")
                                if (not isinstance(action, dict) or not isinstance(state_action, dict)
                                        or action.get("command_seq") != seq or state_action.get("command_seq") != seq
                                        or action.get("outcome") not in ("PASS", "FAIL")
                                        or action.get("restore_verified") is not True
                                        or state_action.get("outcome") != action["outcome"]
                                        or state_action.get("restore_verified") is not True):
                                    raise BridgeUnknown("本次动作的完成和恢复回执未关联到当前命令。")
                            self._write_ledger("ready", pending_command=None)
                        return {"command": current_name, "ack": ack, "state": state or {},
                                "requested_command": name, "requested_seq": requested_seq}
                    if cancel is not None and cancel.is_set() and current_name != "stop" and ack and ack["status"] == "ACCEPTED":
                        # An accepted transaction may be superseded only by the
                        # explicit stop request; never overwrite an unacked file.
                        current_name = "stop"
                        seq = self._send("stop")
                        deadline = time.monotonic() + 12.0
                    time.sleep(0.05)
                reads.raise_pending()
                raise BridgeUnknown("桥接命令等待超时；本次请求不会自动重试。")
            except BridgeAdmissionClosed:
                raise
            except Exception as exc:
                if business_validation and isinstance(exc, (BridgeUnknown, ValueError, TypeError, KeyError, AttributeError)):
                    domain = current_name.rsplit('-', 1)[0].replace('-', '_')
                    operation = next((fields.get('operation_id') for fields in (
                        _campaign_fields, _development_fields, _guild_fields, _reward_fields, _timed_fields,
                        _stone_fields, _mingshi_fields, _refine_fields, _arena_fields, _soul_fields,
                        _wood_fields, _script_fields, _popup_fields) if isinstance(fields, dict)), None)
                    failure = dict(domain=domain, command_name=current_name, command_seq=seq,
                        operation_id=operation, outcome='UNKNOWN', replay_allowed=False,
                        reason=str(exc), observed_at=_now(), ack_status='COMPLETED')
                    self._business_failures.setdefault(domain, failure)
                    self._write_ledger('ready', pending_command=None,
                        business_unknown={key: dict(value) for key, value in self._business_failures.items()})
                    error = BridgeBusinessError('business_receipt_invalid:' + domain + ':' + str(exc))
                    error.business_failure = failure
                    raise error from exc
                if not self._terminal and not self.target_exited():
                    self.mark_unknown(exc)
                raise

    def heartbeat(self) -> dict:
        return self.request("heartbeat", timeout=3.0)

    def daily_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        """Read this request's daily projection; it does not perform activities."""
        return self.request("daily-observe", timeout=timeout, cancel=cancel)

    def daily_guild_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        return self.request('daily-guild-observe', timeout=timeout, cancel=cancel)

    def daily_guild_command(self, action: str, *, expected_revision: int, character_id: str, guild_id: str,
                            task_id: int, operation_id: str, donation_type: int, max_premium: int,
                            timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      guild_id=guild_id, task_id=task_id, operation_id=operation_id,
                      donation_type=donation_type, max_premium=max_premium)
        return self._request('daily-guild-command', timeout=timeout, cancel=cancel, _guild_fields=fields)

    def daily_reward_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_reward_enabled') is not True:
            raise BridgeError('日常宝箱功能尚未启用。')
        return self.request('daily-reward-observe', timeout=timeout, cancel=cancel)

    def daily_reward_command(self, action: str, *, expected_revision: int, character_id: str,
                             category_id: int, operation_id: str, timeout: float = 5.0,
                             cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      category_id=category_id, operation_id=operation_id)
        return self._request('daily-reward-command', timeout=timeout, cancel=cancel, _reward_fields=fields)

    def daily_timed_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_timed_enabled') is not True:
            raise BridgeError('限时日常功能尚未启用。')
        return self.request('daily-timed-observe', timeout=timeout, cancel=cancel)

    def daily_timed_command(self, action: str, *, expected_revision: int, character_id: str,
                            task_id: int, operation_id: str, timeout: float = 5.0,
                            cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        return self._request('daily-timed-command', timeout=timeout, cancel=cancel, _timed_fields=fields)

    def daily_stone_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_stone_enabled') is not True:
            raise BridgeError('采石功能尚未启用。')
        return self.request('daily-stone-observe', timeout=timeout, cancel=cancel)

    def daily_stone_command(self, action: str, *, expected_revision: int, character_id: str,
                            task_id: int, operation_id: str, timeout: float = 5.0,
                            cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        return self._request('daily-stone-command', timeout=timeout, cancel=cancel, _stone_fields=fields)

    def daily_mingshi_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_mingshi_enabled') is not True:
            raise BridgeError('铭石功能尚未启用。')
        return self.request('daily-mingshi-observe', timeout=timeout, cancel=cancel)

    def daily_mingshi_command(self, action: str, *, expected_revision: int, character_id: str,
                              task_id: int, operation_id: str, timeout: float = 5.0,
                              cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        return self._request('daily-mingshi-command', timeout=timeout, cancel=cancel, _mingshi_fields=fields)

    def daily_refine_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_refine_enabled') is not True:
            raise BridgeError('炼魂功能尚未启用。')
        return self.request('daily-refine-observe', timeout=timeout, cancel=cancel)

    def daily_refine_command(self, action: str, *, expected_revision: int, character_id: str,
                             task_id: int, operation_id: str, timeout: float = 5.0,
                             cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        return self._request('daily-refine-command', timeout=timeout, cancel=cancel, _refine_fields=fields)

    def daily_arena_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_arena_enabled') is not True:
            raise BridgeError('竞技宝箱功能尚未启用。')
        return self.request('daily-arena-observe', timeout=timeout, cancel=cancel)

    def daily_arena_command(self, action: str, *, expected_revision: int, character_id: str,
                            operation_id: str, timeout: float = 5.0,
                            cancel: threading.Event | None = None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      operation_id=operation_id)
        return self._request('daily-arena-command', timeout=timeout, cancel=cancel, _arena_fields=fields)

    def daily_soul_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_soul_enabled') is not True:
            raise BridgeError('聚魂功能尚未启用。')
        return self.request('daily-soul-observe', timeout=timeout, cancel=cancel)

    def daily_soul_command(self, action: str, *, expected_revision: int, character_id: str,
                           task_id: int, operation_id: str, soul_mode: int = 1, free_only: int = 1, timeout: float = 5.0,
                           cancel: threading.Event | None = None, admission=None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id, soul_mode=soul_mode, free_only=free_only)
        return self._request('daily-soul-command', timeout=timeout, cancel=cancel, _soul_fields=fields,
                             _admission=admission)

    def daily_wood_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if self._last_state.get('daily_wood_enabled') is not True:
            raise BridgeError('木人试炼功能尚未启用。')
        return self.request('daily-wood-observe', timeout=timeout, cancel=cancel)

    def daily_popup_observe(self, *, timeout: float = 5.0) -> dict:
        if not isinstance(self._last_state.get('daily_popup'), dict):
            raise BridgeError('当前桥接不支持公共奖励确认。')
        return self.request('daily-popup-observe', timeout=timeout)

    def daily_popup_confirm(self, *, window_type: str, instance_id: int, appearance_generation: int,
                            expected_revision: int, character_id: str, operation_id: str,
                            timeout: float = 5.0, admission=None) -> dict:
        fields = dict(action='confirm', window_type=window_type, instance_id=instance_id,
                      appearance_generation=appearance_generation, expected_revision=expected_revision,
                      character_id=character_id, operation_id=operation_id)
        return self._request('daily-popup-command', timeout=timeout, _popup_fields=fields, _admission=admission)

    def daily_script(self, action: str, *, task_id: int, character_id: str,
                     operation_id: str, expected_revision: int, timeout: float = 5.0, battle_epoch=None) -> dict:
        if action != 'observe' and battle_epoch is None:
            battle_epoch = self.capture_battle_epoch()
        fields = dict(action=action, operation_id=operation_id, character_id=character_id,
                      task_id=task_id, expected_revision=expected_revision)
        return self._request('daily-script', timeout=timeout, _script_fields=fields, _battle_epoch=battle_epoch)

    def daily_wood_command(self, action: str, *, expected_revision: int, character_id: str,
                           task_id: int, operation_id: str, timeout: float = 5.0,
                           cancel: threading.Event | None = None, admission=None) -> dict:
        fields = dict(action=action, expected_revision=expected_revision, character_id=character_id,
                      task_id=task_id, operation_id=operation_id)
        return self._request('daily-wood-command', timeout=timeout, cancel=cancel, _wood_fields=fields,
                             _admission=admission)

    def campaign_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        return self.request("campaign-observe", timeout=timeout, cancel=cancel)

    def campaign_command(self, action: str, *, expected_revision: int, campaign_id: int = 0,
                         troop_id: int = 0, attempt_id: str = "", timeout: float = 5.0,
                         cancel: threading.Event | None = None, battle_epoch=None) -> dict:
        if battle_epoch is None:
            battle_epoch = self.capture_battle_epoch()
        fields = {"action": action, "expected_revision": expected_revision, "campaign_id": campaign_id,
                  "troop_id": troop_id, "attempt_id": attempt_id}
        return self._request("campaign-command", timeout=timeout, cancel=cancel, _campaign_fields=fields, _battle_epoch=battle_epoch)

    def challenge_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        return self.request('challenge-observe', timeout=timeout, cancel=cancel)

    def home_navigation_command(self, action: str, *, scope: str, expected_revision: int,
                                campaign_id: int, star: int = 0, timeout: float = 5.0,
                                battle_epoch=None) -> dict:
        if (action not in HOME_NAVIGATION_ACTIONS | {'continue'}
                or scope not in ('challenge', 'elite') and not (scope == 'campaign' and action == 'continue')):
            raise BridgeError('未知的返回主界面动作。')
        troop_id = 0
        if action == 'continue':
            snap = self._last_state.get(scope) or {}
            if (not (_home_battle_guide_ready(snap, scope) or _home_loss_advice_ready(snap, scope)) or snap.get('revision') != expected_revision
                    or snap.get('campaign_id') != campaign_id):
                raise BridgeAdmissionClosed('home_loss_advice_changed_before_submit')
            troop_id = snap['troop_id']
        if scope == 'campaign':
            return self.campaign_command(action, expected_revision=expected_revision, campaign_id=campaign_id,
                                         troop_id=troop_id, timeout=timeout, battle_epoch=battle_epoch)
        if scope == 'elite':
            return self.elite_command(action, expected_revision=expected_revision, campaign_id=campaign_id,
                                      troop_id=troop_id, star=star, timeout=timeout, battle_epoch=battle_epoch)
        return self.challenge_command(action, expected_revision=expected_revision, campaign_id=campaign_id,
                                      troop_id=troop_id, timeout=timeout, battle_epoch=battle_epoch)

    def challenge_command(self, action: str, *, expected_revision: int, campaign_id: int = 0,
                          troop_id: int = 0, attempt_id: str = '', timeout: float = 5.0,
                          cancel: threading.Event | None = None, battle_epoch=None) -> dict:
        if battle_epoch is None:
            battle_epoch = self.capture_battle_epoch()
        fields = {'action': action, 'expected_revision': expected_revision, 'campaign_id': campaign_id,
                  'troop_id': troop_id, 'attempt_id': attempt_id}
        return self._request('challenge-command', timeout=timeout, cancel=cancel, _campaign_fields=fields, _battle_epoch=battle_epoch)

    def development_observe(self, *, fields: dict | None = None, timeout: float = 5.0,
                            cancel: threading.Event | None = None) -> dict:
        return self._request("development-observe", timeout=timeout, cancel=cancel, _development_fields=fields)

    def elite_observe(self, *, timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        if (self._last_state.get('elite') or {}).get('elite_interface') != ELITE_INTERFACE:
            raise BridgeError('elite_native_interface_unavailable')
        return self.request('elite-observe', timeout=timeout, cancel=cancel)

    def elite_command(self, action: str, *, expected_revision: int, star: int, daily_target: int = 10,
                      campaign_id: int = 0, troop_id: int = 0, attempt_id: str = '', timeout: float = 5.0,
                      cancel: threading.Event | None = None, battle_epoch=None) -> dict:
        if (self._last_state.get('elite') or {}).get('elite_interface') != ELITE_INTERFACE:
            raise BridgeError('elite_native_interface_unavailable')
        if battle_epoch is None:
            battle_epoch = self.capture_battle_epoch()
        fields = dict(action=action, expected_revision=expected_revision, campaign_id=campaign_id,
                      troop_id=troop_id, attempt_id=attempt_id, star=star, daily_target=daily_target)
        return self._request('elite-command', timeout=timeout, cancel=cancel,
                             _campaign_fields=fields, _battle_epoch=battle_epoch)

    def development_command(self, action: str, *, expected_revision: int, operation_id: str,
                            target_id: int, desired_level: int, max_requests: int,
                            max_money: int = 0, max_stone: int = 0, max_food: int = 0,
                            mode: str = "", max_practice: int | None = None, timeout: float = 5.0,
                            cancel: threading.Event | None = None) -> dict:
        fields = {"action": action, "expected_revision": expected_revision, "operation_id": operation_id,
                  "target_id": target_id, "desired_level": desired_level, "max_requests": max_requests,
                  "max_money": max_money, "max_stone": max_stone, "max_food": max_food, "mode": mode}
        if action.startswith('star_'):
            fields['max_practice'] = max_practice
        return self._request("development-command", timeout=timeout, cancel=cancel, _development_fields=fields)

    def player_state_refresh(self, *, expected_revision: int, operation_id: str,
                             timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        fields = dict(action="player_state_refresh", expected_revision=expected_revision, operation_id=operation_id,
            target_id=0, desired_level=0, max_requests=0, max_money=0, max_stone=0, max_food=0, mode="")
        return self._request("player-state-refresh", timeout=timeout, cancel=cancel, _development_fields=fields)

    def daily_state_refresh(self, *, expected_revision: int, operation_id: str,
                            timeout: float = 5.0, cancel: threading.Event | None = None) -> dict:
        fields = dict(action='daily_state_refresh', expected_revision=expected_revision, operation_id=operation_id,
                      target_id=0, desired_level=0, max_requests=0, max_money=0, max_stone=0, max_food=0, mode='')
        return self._request('daily-state-refresh', timeout=timeout, cancel=cancel, _development_fields=fields)

    def _check_daily_refresh_admission(self) -> None:
        current, business = self._last_state.get('daily_refresh'), self._last_state.get('business')
        if not _daily_refresh_valid(current) or current['status'] != 'IDLE':
            raise BridgeError('本会话的日常刷新已经使用或尚未就绪。')
        if (type(business) is not dict or any(business.get(key) is not False for key in
                ('pending_attempt', 'pending_development', 'pending_player_refresh', 'pending_daily_guild'))):
            raise BridgeError('当前连接仍有未决操作，不能刷新日常状态。')

    def _check_refresh_admission(self) -> None:
        """A refresh belongs only to this connection's current observation."""
        current = self._last_state.get("player_refresh")
        business = self._last_state.get("business")
        if not _player_refresh_valid(current) or current["status"] != "IDLE":
            raise BridgeError("本会话的状态刷新已经使用或尚未就绪。")
        if (type(business) is not dict or business.get("pending_attempt") is not False
                or business.get("pending_development") is not False
                or business.get("pending_player_refresh") is True):
            raise BridgeError("当前连接仍有未决操作，不能刷新角色状态。")

    def verify(self, cancel: threading.Event | None = None) -> dict:
        return self.request("disable-restore", timeout=13.0, cancel=cancel)

    def stop(self) -> dict:
        if self._terminal:
            return {"state": self.last_state, "command": "stop"}
        self.cancel_battle_admission()
        if self.target_exited():
            return {"state": {"lifecycle": "stopped", "component_destroyed": False}, "command": "stop"}
        if not self._owns_lease or not self._bootstrap_started:
            raise BridgeUnknown("当前控制器不拥有原对象连接，不能覆盖其命令通道。")
        state = self._state()
        if state and _is_clean(state):
            self._finish_stopped(state)
            return {"state": state, "command": "stop"}
        return self.request("stop", timeout=12.0)
