"""Durable battle-start admission within one explicitly started task run.

Only this directory is admission input. Historical campaign/challenge output
ledgers are never scanned or promoted into unresolved starts automatically.
An OS lock serializes each role/mode/target transaction within its run. Older
run directories remain evidence and are never inputs to a newly started run.
"""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import time
import uuid

from .campaign import CampaignError, APPLIED_LOSS_EVIDENCE, OBSERVED_LOSS_EVIDENCE, valid_settlement_evidence


class BattleStartError(CampaignError):
    pass


def _pairs(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise BattleStartError('battle_start_duplicate_json_key')
        value[key] = item
    return value


def _hex(value):
    return type(value) is str and re.fullmatch('[0-9a-f]{32}', value) is not None


def _role(value):
    return (type(value) is str and re.fullmatch('[1-9][0-9]{0,19}', value) is not None
            and int(value) <= 2**64 - 1)


def _positive(value):
    return type(value) is int and value > 0


def _context_role(context_key):
    try:
        context = json.loads(context_key, object_pairs_hook=_pairs)
    except (ValueError, TypeError, UnicodeError) as exc:
        raise BattleStartError('battle_start_context_invalid') from exc
    if type(context) is not dict or not _role(context.get('character_id')):
        raise BattleStartError('battle_start_context_invalid')
    return context['character_id']


@dataclass(frozen=True)
class BattleStartKey:
    character_id: str
    mode: str
    campaign_id: int
    troop_id: int
    user_id: str = ''
    zone_id: str = ''

    def __post_init__(self):
        if (not _role(self.character_id) or self.mode not in ('MAINLINE', 'NORMAL_CHALLENGE', 'ELITE_CHALLENGE')
                or not _positive(self.campaign_id) or not _positive(self.troop_id)
                or (not _role(self.user_id) or not _role(self.zone_id) if self.mode == 'ELITE_CHALLENGE'
                    else self.user_id != '' or self.zone_id != '')):
            raise BattleStartError('battle_start_identity_invalid')

    @classmethod
    def from_observation(cls, context_key, snapshot, mode):
        role = _context_role(context_key)
        if (snapshot.get('character_id') != role
                or snapshot.get('mode', mode) != mode):
            raise BattleStartError('battle_start_identity_mismatch')
        if mode == 'ELITE_CHALLENGE':
            context = json.loads(context_key, object_pairs_hook=_pairs)
            if any(not _role(snapshot.get(key)) or snapshot[key] != context.get(key) for key in ('user_id', 'zone_id')):
                raise BattleStartError('battle_start_account_identity_mismatch')
            return cls(role, mode, snapshot.get('campaign_id'), snapshot.get('troop_id'),
                       snapshot['user_id'], snapshot['zone_id'])
        return cls(role, mode, snapshot.get('campaign_id'), snapshot.get('troop_id'))

    def encode(self):
        value = dict(character_id=self.character_id, mode=self.mode,
                     campaign_id=self.campaign_id, troop_id=self.troop_id)
        if self.mode == 'ELITE_CHALLENGE':
            value.update(user_id=self.user_id, zone_id=self.zone_id)
        return value

    def matches_context(self, context_key):
        if _context_role(context_key) != self.character_id:
            return False
        if self.mode != 'ELITE_CHALLENGE':
            return True
        context = json.loads(context_key, object_pairs_hook=_pairs)
        return context.get('user_id') == self.user_id and context.get('zone_id') == self.zone_id


@dataclass(frozen=True)
class BattleStartReservation:
    key: BattleStartKey
    intent_id: str
    run_id: str
    context_key: str


_RECORD_FIELDS = frozenset(('intent_id', 'run_id', 'context_key', 'status', 'command_seq',
                            'outcome', 'report_id', 'evidence', 'reason'))
_ACTIVE = frozenset(('RESERVED', 'SUBMITTED', 'UNKNOWN'))
_CLOSED = frozenset(('COMPLETED', 'REJECTED', 'CONTINUED'))
_PRE_SUBMIT_PROOFS = frozenset(('admission_closed_before_submit', 'cancelled_before_submit'))


class BattleStartStore:
    """One durable active intent per stable battle target; no implicit recovery.

    File contents are fsynced before atomic publication. A failed write leaves
    the previous active record in place. Terminal records retain exact evidence
    and IDs, so a duplicate cannot clear a different, later active intent.
    """

    def __init__(self, directory, *, run_id=None):
        self.root_directory = Path(directory)
        if run_id is not None and not _hex(run_id):
            raise BattleStartError('battle_start_run_identity_invalid')
        self.run_id = run_id
        self.directory = (self.root_directory / 'rounds' / run_id
                          if run_id is not None else self.root_directory)

    def for_run(self, run_id):
        """Select a fresh run namespace without altering any earlier evidence."""
        return type(self)(self.root_directory, run_id=run_id)

    def path_for(self, key):
        wire = json.dumps(key.encode(), sort_keys=True, separators=(',', ':')).encode('ascii')
        return self.directory / (hashlib.sha256(wire).hexdigest() + '.json')

    def anchor_path_for(self, key):
        return self.path_for(key).with_suffix('.anchor')

    @contextmanager
    def _locked(self, key):
        self.directory.mkdir(parents=True, exist_ok=True)
        lock_path = self.path_for(key).with_suffix('.lock')
        try:
            descriptor = os.open(lock_path, os.O_RDWR | os.O_CREAT | os.O_EXCL, 0o600)
            first_lock = True
        except FileExistsError:
            descriptor = os.open(lock_path, os.O_RDWR)
            first_lock = False
        with os.fdopen(descriptor, 'r+b', buffering=0) as lock:
            # Only the atomic creator may initialize a wholly absent target.
            # A surviving old lock without a record is evidence of loss, not
            # proof of first use. Never delete/recreate the lock to retry.
            attempts = 101 if first_lock else 1
            for attempt in range(attempts):
                try:
                    if lock.seek(0, os.SEEK_END) == 0:
                        lock.write(b'\0')
                        lock.flush()
                    lock.seek(0)
                    if os.name == 'nt':
                        import msvcrt
                        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                    else:
                        import fcntl
                        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except OSError as exc:
                    if attempt + 1 == attempts:
                        raise BattleStartError('battle_start_lock_unavailable') from exc
                    # A competing opener may inspect the new lock before its
                    # creator. Let it leave, then retain the creator's right
                    # to perform the one initial durable empty publication.
                    time.sleep(.005)
            try:
                yield first_lock
            finally:
                lock.seek(0)
                if os.name == 'nt':
                    import msvcrt
                    msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(lock.fileno(), fcntl.LOCK_UN)

    def _record(self, record, key, *, active):
        if (type(record) is not dict or set(record) != _RECORD_FIELDS
                or not _hex(record['intent_id']) or not _hex(record['run_id'])
                or type(record['context_key']) is not str
                or not key.matches_context(record['context_key'])
                or type(record['status']) is not str or type(record['outcome']) is not str
                or record['status'] not in (_ACTIVE if active else _CLOSED)
                or type(record['command_seq']) is not int or record['command_seq'] < 0
                or type(record['reason']) is not str or type(record['evidence']) is not str
                or type(record['report_id']) is not str):
            raise BattleStartError('battle_start_record_invalid')
        if record['status'] == 'COMPLETED':
            if record['outcome'] == 'LOSS' and record['evidence'] == OBSERVED_LOSS_EVIDENCE:
                # This native/UI observation is not an accepted settlement.
                # Also fail closed when reading a terminal record from the old
                # policy, rather than silently re-admitting its target.
                raise BattleStartError('loss_observed_settlement_unconfirmed')
            if (not _positive(record['command_seq'])
                    or not self._valid_result_fields(record, key)):
                raise BattleStartError('battle_start_terminal_invalid')
        elif record['status'] == 'CONTINUED':
            if (key.mode != 'ELITE_CHALLENGE' or not _positive(record['command_seq'])
                    or record['outcome'] != 'UNKNOWN'
                    or re.fullmatch('0|[1-9][0-9]{0,19}', record['report_id']) is None
                    or int(record['report_id']) > 2**64 - 1
                    or record['evidence'] != 'elite_owned_result_receipt_unconfirmed'
                    or record['reason'] not in ('elite_receipt_missing_continuation_authorized',
                        'elite_native_rejection_processed', 'elite_current_page_progress_confirmed')):
                raise BattleStartError('battle_start_elite_continuation_invalid')
        elif (record['outcome'] != 'UNKNOWN' or record['report_id'] != '0'
              or record['evidence'] != '' or not record['reason'] and record['status'] == 'REJECTED'
              or record['status'] == 'RESERVED' and record['command_seq'] != 0
              or record['status'] == 'SUBMITTED' and not _positive(record['command_seq'])):
            raise BattleStartError('battle_start_record_invalid')
        if record['status'] == 'REJECTED':
            proof, separator, reason = record['reason'].partition(': ')
            if (not separator or not reason
                    or proof not in _PRE_SUBMIT_PROOFS | {'rejected_without_invocation'}
                    or (not _positive(record['command_seq']) if proof == 'rejected_without_invocation'
                        else record['command_seq'] != 0)):
                raise BattleStartError('battle_start_rejection_record_invalid')
        return record

    @staticmethod
    def _valid_result_fields(record, key):
        report = record['report_id']
        applied_reportless_loss = (key.mode in ('MAINLINE', 'NORMAL_CHALLENGE') and report == '0'
                                   and record['outcome'] == 'LOSS' and record['evidence'] == APPLIED_LOSS_EVIDENCE)
        return (re.fullmatch('[0-9]+', report) is not None
                and (applied_reportless_loss or int(report) > 0 and record['evidence'] != APPLIED_LOSS_EVIDENCE)
                and record['evidence'] != OBSERVED_LOSS_EVIDENCE
                and (key.mode != 'ELITE_CHALLENGE' or record['evidence'] == 'captured_authoritative_receipt_applied')
                and valid_settlement_evidence(record['outcome'], record['evidence'],
                                              challenge=key.mode in ('NORMAL_CHALLENGE', 'ELITE_CHALLENGE')))

    @staticmethod
    def _identity_matches(identity, key):
        return (type(identity) is dict and set(identity) == set(key.encode())
                and type(identity['campaign_id']) is int and type(identity['troop_id']) is int
                and identity == key.encode())

    @staticmethod
    def _read_json(path, label):
        try:
            raw = path.read_bytes()
        except FileNotFoundError:
            return None, None
        try:
            return json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs), raw
        except (ValueError, UnicodeError) as exc:
            raise BattleStartError('battle_start_' + label + '_corrupt') from exc

    def _validate_data(self, data, key):
        version = data.get('version') if type(data) is dict else None
        if version == 2:
            raise BattleStartError('battle_start_unbound_v2_requires_review')
        keys = {'version', 'identity', 'active', 'closed'} | ({'anchor_id', 'generation'} if version == 3 else set())
        if (type(data) is not dict or set(data) != keys
                or type(version) is not int or version not in (1, 3)
                or not self._identity_matches(data['identity'], key)
                or version == 3 and (not _hex(data['anchor_id'])
                    or type(data['generation']) is not int or data['generation'] < 0)
                or type(data['closed']) is not list):
            raise BattleStartError('battle_start_record_identity_or_schema_invalid')
        ids = set()
        reports = set()
        records = [(item, False) for item in data['closed']]
        if data['active'] is not None:
            records.append((data['active'], True))
        for record, active in records:
            self._record(record, key, active=active)
            if record['intent_id'] in ids:
                raise BattleStartError('battle_start_duplicate_intent')
            ids.add(record['intent_id'])
            if record['status'] == 'COMPLETED' and record['report_id'] != '0':
                if record['report_id'] in reports:
                    raise BattleStartError('battle_start_duplicate_report')
                reports.add(record['report_id'])
        return data

    def _load_anchor(self, key):
        anchor, raw = self._read_json(self.anchor_path_for(key), 'anchor')
        if raw is None:
            return None
        if (type(anchor) is not dict or set(anchor) != {'version', 'identity', 'anchor_id', 'generation', 'record_sha256'}
                or type(anchor['version']) is not int or anchor['version'] != 2
                or not self._identity_matches(anchor['identity'], key) or not _hex(anchor['anchor_id'])
                or type(anchor['generation']) is not int or anchor['generation'] < 0
                or type(anchor['record_sha256']) is not str
                or re.fullmatch('[0-9a-f]{64}', anchor['record_sha256']) is None):
            raise BattleStartError('battle_start_anchor_invalid')
        return anchor

    def _load(self, key, *, first_lock=False):
        data, raw = self._read_json(self.path_for(key), 'record')
        anchor = self._load_anchor(key)
        if raw is None:
            if anchor is not None or not first_lock:
                raise BattleStartError('battle_start_record_missing')
            data = dict(version=3, identity=key.encode(), anchor_id=uuid.uuid4().hex,
                        generation=0, active=None, closed=[])
            return self._publish_pair(key, data)
        # Validate the entire old schema and all identities/results before
        # creating any migration metadata or changing the old record.
        self._validate_data(data, key)
        if data['version'] == 1:
            if anchor is not None:
                raise BattleStartError('battle_start_legacy_anchor_mismatch')
            if data['active'] is None:
                # An unanchored empty/terminal legacy file could itself be a
                # rollback. There is no independent proof that it is current.
                raise BattleStartError('battle_start_legacy_inactive_requires_review')
            data = dict(data, version=3, anchor_id=uuid.uuid4().hex, generation=0)
            return self._publish_pair(key, data)
        return self._verified_pair(key, data=data, raw=raw, anchor=anchor)

    def _verified_pair(self, key, *, data=None, raw=None, anchor=None):
        if raw is None:
            data, raw = self._read_json(self.path_for(key), 'record')
            if raw is None:
                raise BattleStartError('battle_start_record_missing')
            self._validate_data(data, key)
        if data['version'] != 3:
            raise BattleStartError('battle_start_record_not_content_bound')
        if anchor is None:
            anchor = self._load_anchor(key)
        if anchor is None:
            raise BattleStartError('battle_start_anchor_missing')
        if (data['anchor_id'] != anchor['anchor_id'] or data['generation'] != anchor['generation']
                or hashlib.sha256(raw).hexdigest() != anchor['record_sha256']):
            raise BattleStartError('battle_start_record_anchor_mismatch')
        return data

    def _write(self, key, data):
        current = self._verified_pair(key)
        if (current['anchor_id'] != data['anchor_id'] or current['generation'] != data['generation']):
            raise BattleStartError('battle_start_update_generation_mismatch')
        candidate = dict(data, generation=current['generation'] + 1)
        committed = self._publish_pair(key, candidate)
        data['generation'] = committed['generation']
        return committed

    def _publish_pair(self, key, data):
        self._validate_data(data, key)
        if data['version'] != 3:
            raise BattleStartError('battle_start_record_not_content_bound')
        raw = self._bytes(data)
        anchor = dict(version=2, identity=key.encode(), anchor_id=data['anchor_id'],
                      generation=data['generation'], record_sha256=hashlib.sha256(raw).hexdigest())
        # Publish the next exact-content commitment first. An interruption
        # before its record leaves a mismatch that readers must never repair
        # into empty state. Only matching independent readback may admit I/O.
        self._write_anchor(key, anchor)
        self._write_record(key, data)
        return self._readback_pair(key, data)

    def _readback_pair(self, key, expected):
        committed = self._verified_pair(key)
        if committed != expected:
            raise BattleStartError('battle_start_pair_readback_changed')
        return committed

    def _write_record(self, key, data):
        self._atomic_json(self.path_for(key), data)

    def _write_anchor(self, key, anchor):
        self._atomic_json(self.anchor_path_for(key), anchor)

    def _atomic_json(self, path, data):
        name = None
        try:
            with tempfile.NamedTemporaryFile(mode='wb', dir=self.directory,
                    prefix=path.name + '.', suffix='.tmp', delete=False) as stream:
                name = stream.name
                stream.write(self._bytes(data))
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(name, path)
            name = None
        finally:
            if name is not None:
                try:
                    os.unlink(name)
                except OSError:
                    pass

    @staticmethod
    def _bytes(data):
        return (json.dumps(data, sort_keys=True, separators=(',', ':')) + '\n').encode('utf-8')

    @staticmethod
    def _owned(data, reservation):
        active = data['active']
        if (active is None or active['intent_id'] != reservation.intent_id
                or active['run_id'] != reservation.run_id
                or active['context_key'] != reservation.context_key):
            raise BattleStartError('battle_start_reservation_mismatch')
        return active

    def reserve(self, key, run_id, context_key, intent_id):
        if (not _hex(run_id) or not _hex(intent_id)
                or self.run_id is not None and run_id != self.run_id
                or not key.matches_context(context_key)):
            raise BattleStartError('battle_start_reservation_invalid')
        with self._locked(key) as first_lock:
            data = self._load(key, first_lock=first_lock)
            if data['active'] is not None:
                active = data['active']
                raise BattleStartError('battle_start_unresolved: %s (%s)' %
                                       (active['intent_id'], active['status']))
            if any(item['intent_id'] == intent_id for item in data['closed']):
                raise BattleStartError('battle_start_intent_reused')
            data['active'] = dict(intent_id=intent_id, run_id=run_id, context_key=context_key,
                status='RESERVED', command_seq=0, outcome='UNKNOWN', report_id='0', evidence='', reason='')
            self._write(key, data)
        return BattleStartReservation(key, intent_id, run_id, context_key)

    def submitted(self, reservation, command_seq):
        if not _positive(command_seq):
            raise BattleStartError('battle_start_command_seq_invalid')
        with self._locked(reservation.key) as first_lock:
            data = self._load(reservation.key, first_lock=first_lock)
            active = self._owned(data, reservation)
            if active['command_seq'] not in (0, command_seq):
                raise BattleStartError('battle_start_command_seq_mismatch')
            if active['command_seq'] == command_seq:
                return
            active.update(command_seq=command_seq,
                          status='UNKNOWN' if active['status'] == 'UNKNOWN' else 'SUBMITTED')
            self._write(reservation.key, data)

    @staticmethod
    def _attempt_identity(reservation, attempt):
        key = reservation.key
        if (attempt.get('attempt_id') != reservation.intent_id
                or attempt.get('character_id') != key.character_id
                or type(attempt.get('campaign_id')) is not int or attempt['campaign_id'] != key.campaign_id
                or type(attempt.get('troop_id')) is not int or attempt['troop_id'] != key.troop_id
                or not _positive(attempt.get('command_seq'))):
            raise BattleStartError('battle_start_settlement_identity_mismatch')

    def bind_observation(self, reservation, attempt):
        self._attempt_identity(reservation, attempt)
        if attempt.get('status') not in ('SUBMITTED', 'WAITING', 'SETTLED'):
            raise BattleStartError('battle_start_attempt_not_submitted')
        self.submitted(reservation, attempt['command_seq'])

    def _settlement(self, data, reservation, attempt):
        self._attempt_identity(reservation, attempt)
        active = self._owned(data, reservation)
        candidate = dict(active, status='COMPLETED', outcome=attempt.get('outcome'),
                         report_id=attempt.get('report_id'), evidence=attempt.get('evidence'), reason='')
        if attempt.get('status') != 'SETTLED' or active['command_seq'] != attempt['command_seq']:
            raise BattleStartError('battle_start_settlement_unconfirmed')
        self._record(candidate, reservation.key, active=False)
        if any(item['status'] == 'COMPLETED' and item['report_id'] != '0'
               and item['report_id'] == candidate['report_id'] for item in data['closed']):
            raise BattleStartError('battle_start_duplicate_report')
        return candidate

    def validate_settlement(self, reservation, attempt):
        with self._locked(reservation.key) as first_lock:
            self._settlement(self._load(reservation.key, first_lock=first_lock), reservation, attempt)

    def complete(self, reservation, attempt):
        with self._locked(reservation.key) as first_lock:
            data = self._load(reservation.key, first_lock=first_lock)
            for previous in data['closed']:
                if previous['intent_id'] == reservation.intent_id:
                    self._attempt_identity(reservation, attempt)
                    if (previous['status'] == 'COMPLETED' and attempt.get('status') == 'SETTLED'
                            and previous['run_id'] == reservation.run_id
                            and previous['context_key'] == reservation.context_key
                            and all(previous[name] == attempt.get(name) for name in
                                    ('command_seq', 'outcome', 'report_id', 'evidence'))):
                        return False
                    raise BattleStartError('battle_start_conflicting_result')
            candidate = self._settlement(data, reservation, attempt)
            data['closed'].append(candidate)
            data['active'] = None
            self._write(reservation.key, data)
            return True

    def reject(self, reservation, proof, *, command_seq=0, reason=''):
        if (proof not in _PRE_SUBMIT_PROOFS | {'rejected_without_invocation'}
                or type(reason) is not str or not reason
                or type(command_seq) is not int
                or (not _positive(command_seq) if proof == 'rejected_without_invocation' else command_seq != 0)):
            raise BattleStartError('battle_start_rejection_proof_invalid')
        with self._locked(reservation.key) as first_lock:
            data = self._load(reservation.key, first_lock=first_lock)
            active = self._owned(data, reservation)
            if active['status'] != 'RESERVED' or active['command_seq'] != 0:
                raise BattleStartError('battle_start_rejection_after_possible_invocation')
            data['closed'].append(dict(active, status='REJECTED', command_seq=command_seq,
                                       reason=proof + ': ' + reason))
            data['active'] = None
            self._write(reservation.key, data)

    def continue_elite_unconfirmed(self, reservation, attempt):
        """Close an owned elite UI result without asserting server settlement."""
        self._attempt_identity(reservation, attempt)
        if reservation.key.mode != 'ELITE_CHALLENGE' or attempt.get('status') != 'CONTINUED':
            raise BattleStartError('battle_start_elite_continuation_required')
        with self._locked(reservation.key) as first_lock:
            data = self._load(reservation.key, first_lock=first_lock)
            active = self._owned(data, reservation)
            if active['command_seq'] != attempt['command_seq']:
                raise BattleStartError('battle_start_command_seq_mismatch')
            candidate = dict(active, status='CONTINUED', outcome=attempt.get('outcome'),
                report_id=attempt.get('report_id'), evidence=attempt.get('evidence'),
                reason=('elite_native_rejection_processed' if attempt.get('native_rejection') is True else
                        'elite_current_page_progress_confirmed' if attempt.get('page_state_confirmed') is True else
                        'elite_receipt_missing_continuation_authorized'))
            self._record(candidate, reservation.key, active=False)
            data['closed'].append(candidate)
            data['active'] = None
            self._write(reservation.key, data)

    def mark_unknown(self, reservation, reason):
        if type(reason) is not str or not reason:
            raise BattleStartError('battle_start_unknown_reason_required')
        with self._locked(reservation.key) as first_lock:
            data = self._load(reservation.key, first_lock=first_lock)
            active = self._owned(data, reservation)
            if active['status'] == 'UNKNOWN' and active['reason'] == reason:
                return
            active.update(status='UNKNOWN', reason=reason)
            self._write(reservation.key, data)

    def read(self, key):
        """Read verified data, durably initializing a new empty target if needed.

        A surviving lock/anchor without its record is never an empty target.
        Losing the entire directory and every independent marker is beyond
        local detection; this store cannot prove history after that erasure.
        """
        with self._locked(key) as first_lock:
            return self._load(key, first_lock=first_lock)
