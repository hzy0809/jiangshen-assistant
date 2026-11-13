"""Owner-admitted handling of known stock reward confirmations.

This has no polling thread and no claim/receipt dependency. The workflow owns
when service may run; native UI-thread admission owns the exact final window.
"""
from __future__ import annotations

from dataclasses import dataclass
import copy
import json
import os
from pathlib import Path
import threading
import uuid


INTERFACE = 'scoped-daily-popup-v1'
WINDOW_TYPES = frozenset(('BattleResultUI', 'MessageBox', 'SpecialGetUI', 'ConsumeUI'))
_ATTEMPT_LOCK = threading.RLock()


def valid_popup(value: object) -> bool:
    if (not isinstance(value, dict) or value.get('interface') != INTERFACE
            or type(value.get('revision')) is not int or value['revision'] < 1
            or type(value.get('event_sequence')) is not int or value['event_sequence'] < 0
            or value.get('status') not in ('IDLE', 'OPEN', 'BLOCKED', 'STOPPED')
            or not isinstance(value.get('reason'), str) or not isinstance(value.get('character_id'), str)
            or not isinstance(value.get('windows'), list) or not isinstance(value.get('command'), dict)):
        return False
    for window in value['windows']:
        if (not isinstance(window, dict) or window.get('window_type') not in WINDOW_TYPES
                or type(window.get('instance_id')) is not int or window['instance_id'] == 0
                or type(window.get('appearance_generation')) is not int or window['appearance_generation'] < 0
                or type(window.get('event_sequence')) is not int or window['event_sequence'] < 0
                or window['event_sequence'] > value['event_sequence']
                or window.get('status') not in ('AVAILABLE', 'OWNED', 'LOADING', 'BLOCKED', 'ATTEMPTED', 'STOPPED')
                or any(type(window.get(name)) is not bool for name in ('visible', 'loading', 'attempted'))
                or any(not isinstance(window.get(name), str) for name in ('classification', 'callback', 'reason', 'appearance_stamp'))
                or not window['appearance_stamp']):
            return False
    event = value.get('last_event')
    if event is not None and (not isinstance(event, dict) or event.get('window_type') not in WINDOW_TYPES
            or type(event.get('instance_id')) is not int or event['instance_id'] == 0
            or type(event.get('appearance_generation')) is not int or event['appearance_generation'] < 1
            or not isinstance(event.get('appearance_stamp'), str) or not event['appearance_stamp']
            or event.get('event_sequence') != value['event_sequence']):
        return False
    command = value['command']
    return (isinstance(command.get('operation_id'), str) and command.get('status') in ('', 'REJECTED', 'COMPLETED', 'UNKNOWN')
            and isinstance(command.get('reason'), str) and type(command.get('instance_id')) is int
            and type(command.get('appearance_generation')) is int and type(command.get('invocation_attempted')) is bool)


@dataclass(frozen=True)
class DailyPopupAppearance:
    context_key: str
    window_type: str
    instance_id: int
    appearance_generation: int
    event_sequence: int
    appearance_stamp: str = ''


class DailyPopupHandler:
    def __init__(self, objects, token, log=None, on_appearance=None, *, state_path=None, attempt_scope=None):
        self.objects, self.token = objects, token
        self.attempt_scope = uuid.uuid4().hex if attempt_scope is None else attempt_scope
        if (not isinstance(self.attempt_scope, str) or len(self.attempt_scope) != 32
                or any(char not in '0123456789abcdef' for char in self.attempt_scope)):
            raise ValueError('popup_attempt_scope_invalid')
        self.log, self.on_appearance = log, on_appearance
        self._seen: set[tuple] = set()
        self._attempted: set[tuple] = set()
        self._state_path = Path(state_path) if state_path is not None else (
            Path(objects.data_dir) / 'daily-popup-attempts' / (self.attempt_scope + '.json')
            if getattr(objects, 'data_dir', None) is not None else None)
        self._character_id = ''
        try:
            identity = json.loads(token.context_key)
            self._process = (identity['pid'], identity['creation'])
            self._character_id = identity.get('character_id', '')
            if any(type(value) is not int or value <= 0 for value in self._process):
                self._process = None
        except (ValueError, KeyError, TypeError):
            self._process = None

    def _persistent_key(self, window: dict) -> str:
        if (self._process is None or self._state_path is None or not isinstance(self._character_id, str)
                or not self._character_id.isdecimal() or not self._character_id.strip('0')):
            raise ValueError('popup_attempt_store_identity_unavailable')
        return json.dumps((*self._process, self._character_id, window['window_type'], window['instance_id'], window['appearance_stamp']), separators=(',', ':'))

    def _attempt_record(self, key: str, record=None, *, remove=False, reserve=False, operation_id=None):
        """Write intent before command publication; UNKNOWN remains durable.

        Only a definite native rejection or prepublication admission refusal
        releases an intent. Corrupt/unwritable storage refuses new confirmations.
        """
        with _ATTEMPT_LOCK:
            path = self._state_path
            if path is None:
                raise ValueError('popup_attempt_store_unavailable')
            if path.exists():
                data = json.loads(path.read_text(encoding='utf-8'))
                if (not isinstance(data, dict) or data.get('version') != 1 or data.get('scope') != self.attempt_scope
                        or not isinstance(data.get('attempts'), dict)):
                    raise ValueError('popup_attempt_store_invalid')
            else:
                data = {'version': 1, 'scope': self.attempt_scope, 'attempts': {}}
            previous = data['attempts'].get(key)
            if previous is not None and (not isinstance(previous, dict) or previous.get('status') not in ('ATTEMPTING', 'COMPLETED', 'UNKNOWN')):
                raise ValueError('popup_attempt_record_invalid')
            if (record is None and not remove) or reserve and previous is not None:
                return copy.deepcopy(previous)
            if operation_id is not None and previous is not None and previous.get('operation_id') != operation_id:
                raise ValueError('popup_attempt_record_owner_changed')
            if remove:
                data['attempts'].pop(key, None)
            else:
                data['attempts'][key] = record
            path.parent.mkdir(parents=True, exist_ok=True)
            temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
            try:
                with temporary.open('xb') as stream:
                    stream.write(json.dumps(data, sort_keys=True, separators=(',', ':')).encode('utf-8'))
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, path)
            finally:
                if temporary.exists():
                    temporary.unlink()

    @staticmethod
    def _projection(snapshot: dict) -> dict:
        if not isinstance(snapshot, dict):
            return {}
        if snapshot.get('interface') == INTERFACE:
            return snapshot
        return (snapshot.get('state') or snapshot).get('daily_popup') or {}

    def observe_snapshot(self, snapshot: dict) -> tuple[DailyPopupAppearance, ...]:
        """Consume an event projection, including the initial current scan.

        The sequence/last_event also survives a popup disappearing before the
        next state publication. Current windows always remain the action source.
        """
        value = self._projection(snapshot)
        if not valid_popup(value):
            return ()
        emitted = []
        records = list(value['windows'])
        if value.get('last_event'):
            records.append(value['last_event'])
        for window in records:
            if window['appearance_generation'] <= 0:
                continue
            key = (self.token.context_key, window['window_type'], window['instance_id'], window['appearance_generation'])
            if key in self._seen:
                continue
            self._seen.add(key)
            event = DailyPopupAppearance(*key, window['event_sequence'], window['appearance_stamp'])
            emitted.append(event)
            if self.on_appearance is not None:
                self.on_appearance(event)
        return tuple(emitted)

    def scan(self) -> dict:
        """Fresh scan through this workflow's current connection token."""
        value = self.objects.observe_daily_popup(self.token)
        if not valid_popup(value):
            raise ValueError('popup_observation_invalid')
        self.observe_snapshot(value)
        return value

    def service(self, snapshot=None, *, admission=True) -> dict:
        """Handle at most one exact appearance; no waiting or blind retries.

        Snapshot events are hints. Every admitted action obtains a fresh native
        quote and the native model rechecks it again on the game UI thread.
        """
        from .bridge_client import BridgeAdmissionClosed
        from .objects import CampaignAdmissionClosed

        if snapshot is not None:
            self.observe_snapshot(snapshot)
        if not admission:
            return {'status': 'PAUSED', 'reason': 'popup_admission_closed'}
        try:
            value = self.scan()
        except Exception as error:
            return {'status': 'BLOCKED', 'reason': 'popup_observation_unavailable:' + str(error)}
        windows = value['windows']
        if value['status'] == 'STOPPED':
            return {'status': 'PAUSED', 'reason': value['reason'], 'popup': value}
        if value['status'] == 'BLOCKED':
            return {'status': 'BLOCKED', 'reason': value['reason'], 'popup': value}
        if not windows:
            return {'status': 'IDLE', 'reason': 'no_popup', 'popup': value}
        if len(windows) != 1:
            return {'status': 'BLOCKED', 'reason': 'popup_multiple_windows', 'popup': value}
        window = windows[0]
        key = (self.token.context_key, window['window_type'], window['instance_id'], window['appearance_generation'])
        if window['status'] in ('OWNED', 'LOADING'):
            return {'status': 'WAITING', 'reason': 'popup_owned_or_loading', 'popup': value}
        if window['attempted'] and value['command'].get('status') == 'UNKNOWN':
            return {'status': 'UNKNOWN', 'reason': value['command']['reason'], 'popup': value}
        if window['status'] != 'AVAILABLE' or window['window_type'] != 'BattleResultUI':
            return {'status': 'BLOCKED', 'reason': window['reason'] or 'popup_confirmation_not_available', 'popup': value}
        if key in self._attempted or window['attempted']:
            return {'status': 'BLOCKED', 'reason': 'popup_appearance_already_attempted', 'popup': value}
        operation = uuid.uuid4().hex
        try:
            persistent_key = self._persistent_key(window)
            prior = self._attempt_record(persistent_key, {'status': 'ATTEMPTING', 'operation_id': operation}, reserve=True)
            if prior is not None:
                return {'status': 'UNKNOWN' if prior['status'] != 'COMPLETED' else 'BLOCKED',
                        'reason': 'popup_appearance_attempt_retained', 'popup': value, 'prior_attempt': prior}
        except Exception as error:
            return {'status': 'BLOCKED', 'reason': 'popup_attempt_store_unavailable:' + str(error), 'popup': value}
        self._attempted.add(key)
        try:
            wire = self.objects.submit_daily_popup(self.token,
                window_type=window['window_type'], instance_id=window['instance_id'],
                appearance_generation=window['appearance_generation'], expected_revision=value['revision'],
                character_id=value['character_id'], operation_id=operation)
        except (BridgeAdmissionClosed, CampaignAdmissionClosed):
            self._attempted.discard(key)
            try:
                self._attempt_record(persistent_key, remove=True, operation_id=operation)
            except Exception:
                pass # Retaining a never-invoked intent is safe; never clear uncertain storage.
            return {'status': 'PAUSED', 'reason': 'popup_admission_closed', 'popup': value}
        except Exception as error:
            # Publication or stock invocation can already have occurred. Retain
            # this exact appearance without suppressing a future new popup.
            reason = 'popup_command_unknown:' + str(error)
            try:
                self._attempt_record(persistent_key, {'status': 'UNKNOWN', 'operation_id': operation, 'reason': reason}, operation_id=operation)
            except Exception:
                pass # ATTEMPTING was already durably written before publication.
            return {'status': 'UNKNOWN', 'reason': reason, 'popup': value}
        ack = wire.get('ack') or {}
        observed = (ack.get('result') or {}).get('daily_popup')
        if ack.get('status') == 'REJECTED':
            self._attempted.discard(key)
            try:
                self._attempt_record(persistent_key, remove=True, operation_id=operation)
            except Exception:
                pass
            return {'status': 'WAITING', 'reason': ack.get('reason') or 'popup_admission_changed', 'popup': observed or value}
        command = observed.get('command') if valid_popup(observed) else {}
        if (ack.get('status') != 'COMPLETED' or not command or command.get('operation_id') != operation
                or command.get('instance_id') != window['instance_id']
                or command.get('appearance_generation') != window['appearance_generation']
                or command.get('status') != 'COMPLETED' or command.get('invocation_attempted') is not True):
            return {'status': 'UNKNOWN', 'reason': 'popup_close_receipt_unverified', 'popup': observed or value}
        try:
            self._attempt_record(persistent_key, {'status': 'COMPLETED', 'operation_id': operation}, operation_id=operation)
        except Exception:
            pass # A successful close with its original intent retained still cannot replay.
        self.observe_snapshot(observed)
        return {'status': 'HANDLED', 'reason': command['reason'], 'popup': copy.deepcopy(observed),
                'operation_id': operation, 'window': copy.deepcopy(window)}
