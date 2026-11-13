from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path

from .campaign import Checkpoint, ConsumedResult, Intent, Mode, Outcome


class CheckpointError(ValueError):
    pass


def _object(value, keys):
    if type(value) is not dict or set(value) != set(keys):
        raise CheckpointError('unexpected checkpoint fields')
    return value


def _string(value, *, empty=False):
    if type(value) is not str or (not empty and not value):
        raise CheckpointError('invalid string')
    return value


def _int(value, minimum):
    if type(value) is not int or value < minimum:
        raise CheckpointError('invalid integer')
    return value


def _enum(enum, value):
    try:
        return enum(value)
    except (ValueError, TypeError) as exc:
        raise CheckpointError('invalid enum') from exc


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise CheckpointError('duplicate JSON key')
        result[key] = value
    return result


def encode(s: Checkpoint) -> dict:
    p = s.pending
    return {'version': 4 if s.loss_mode == 'cumulative' else 3,
            **({'loss_mode': s.loss_mode} if s.loss_mode == 'cumulative' else {}),
            'run_id': s.run_id, 'context_key': s.context_key,
            'losses': s.losses, 'mode': s.mode.value, 'reason': s.reason,
            'last_revision': s.last_revision, 'guide_step': s.guide_step,
            'completed_guides': list(s.completed_guides),
            'pending': None if p is None else {'intent_id': p.intent_id, 'kind': p.kind,
                'revision': p.revision, 'stage_key': p.stage_key, 'step_key': p.step_key,
                'action_key': p.action_key, 'attempt_id': p.attempt_id, 'uncertain': p.uncertain},
            'consumed': [{'attempt_id': c.attempt_id, 'report_id': c.report_id,
                          'outcome': c.outcome.value} for c in s.consumed]}


def decode(data: object) -> Checkpoint:
    keys = ('version', 'run_id', 'context_key', 'losses', 'mode', 'reason',
            'last_revision', 'guide_step', 'completed_guides', 'pending', 'consumed')
    cumulative = isinstance(data, dict) and type(data.get('version')) is int and data['version'] == 4
    d = _object(data, keys + (('loss_mode',) if cumulative else ()))
    if type(d['version']) is not int or d['version'] not in (3, 4):
        raise CheckpointError('unsupported checkpoint version')
    loss_mode = d.get('loss_mode', 'consecutive')
    if cumulative and loss_mode != 'cumulative':
        raise CheckpointError('invalid loss mode')
    run_id, context = _string(d['run_id']), _string(d['context_key'])
    losses, mode = _int(d['losses'], 0), _enum(Mode, d['mode'])
    reason, revision = _string(d['reason'], empty=True), _int(d['last_revision'], -1)
    guide_step = _string(d['guide_step'], empty=True)
    if type(d['completed_guides']) is not list:
        raise CheckpointError('invalid completed guides')
    guides = tuple(_string(x) for x in d['completed_guides'])
    if len(set(guides)) != len(guides):
        raise CheckpointError('duplicate guide')
    pending = None
    if d['pending'] is not None:
        p = _object(d['pending'], ('intent_id', 'kind', 'revision', 'stage_key', 'step_key', 'action_key', 'attempt_id', 'uncertain'))
        if type(p['uncertain']) is not bool:
            raise CheckpointError('invalid intent uncertainty')
        pending = Intent(_string(p['intent_id']), _string(p['kind']), _int(p['revision'], 0),
                         _string(p['stage_key'], empty=True), _string(p['step_key'], empty=True),
                         _string(p['action_key'], empty=True), _string(p['attempt_id'], empty=True), p['uncertain'])
        if pending.revision > revision:
            raise CheckpointError('invalid pending revision')
        if pending.kind == 'challenge':
            if not pending.stage_key or pending.step_key or pending.action_key:
                raise CheckpointError('invalid challenge')
        elif pending.kind == 'reviewed_guide':
            if pending.stage_key or pending.attempt_id or not pending.step_key or not pending.action_key or pending.step_key in guides:
                raise CheckpointError('invalid guide')
        else:
            raise CheckpointError('invalid intent kind')
    if type(d['consumed']) is not list:
        raise CheckpointError('invalid consumed results')
    consumed = []
    for item in d['consumed']:
        c = _object(item, ('attempt_id', 'report_id', 'outcome'))
        result = ConsumedResult(_string(c['attempt_id']), _string(c['report_id']), _enum(Outcome, c['outcome']))
        if result.outcome == Outcome.UNKNOWN:
            raise CheckpointError('unknown consumed result')
        consumed.append(result)
    streak = 0
    for index, result in enumerate(consumed):
        streak = streak + 1 if result.outcome == Outcome.LOSS else (streak if cumulative else 0)
        if streak >= 3 and index != len(consumed) - 1:
            raise CheckpointError('results after loss limit')
    if (len({x.attempt_id for x in consumed}) != len(consumed) or
        len({x.report_id for x in consumed}) != len(consumed) or
        losses != streak or losses > 3 or
        (losses == 3) != (mode == Mode.LOSS_LIMIT) or
        (losses == 3 and pending is not None) or
        (pending is not None and pending.attempt_id and any(x.attempt_id == pending.attempt_id for x in consumed))):
        raise CheckpointError('inconsistent checkpoint')
    return Checkpoint(run_id, context, losses, mode, reason, pending, tuple(consumed), revision, guide_step, guides, loss_mode)


class CampaignStore:
    """Atomic output record for one explicit run, never startup admission input.

    This class does not acquire an interprocess lock. Keep it in one serialized
    owner for the entire session and use a distinct path for each run.
    """

    def __init__(self, path: str | Path):
        self.path = Path(path)

    def initialize(self, checkpoint: Checkpoint) -> None:
        # Link a fully flushed temporary file exclusively into place: no reader
        # can observe a partial initial checkpoint and an existing run survives.
        data = self._bytes(checkpoint)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        name = None
        try:
            with tempfile.NamedTemporaryFile(mode='wb', dir=self.path.parent, prefix=self.path.name + '.', suffix='.tmp', delete=False) as f:
                name = f.name
                f.write(data)
                f.flush()
                os.fsync(f.fileno())
            os.link(name, self.path)
        finally:
            if name is not None:
                os.unlink(name)

    def _bytes(self, checkpoint: Checkpoint) -> bytes:
        data = encode(checkpoint)
        decode(data)
        return (json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n').encode('utf-8')

    def save(self, checkpoint: Checkpoint) -> None:
        data = self._bytes(checkpoint)
        if not self.path.is_file():
            raise FileNotFoundError(self.path)
        name = None
        try:
            with tempfile.NamedTemporaryFile(mode='wb', dir=self.path.parent, prefix=self.path.name + '.', suffix='.tmp', delete=False) as f:
                name = f.name
                f.write(data)
                f.flush()
                os.fsync(f.fileno())
            os.replace(name, self.path)
            name = None
        finally:
            if name is not None:
                try:
                    os.unlink(name)
                except FileNotFoundError:
                    pass

    def load(self) -> Checkpoint:
        """Read an archive explicitly; live campaign startup never calls this."""
        try:
            with self.path.open('r', encoding='utf-8') as f:
                data = json.load(f, object_pairs_hook=_unique_pairs)
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise CheckpointError('invalid checkpoint JSON') from exc
        return decode(data)
