"""Append-only disposition of one cleared target, without settling its attempt.

The old checkpoint and business receipt remain evidence, never a synthetic WIN.
Only a complete proof for the exact unresolved business origin permits admission
to disregard that one fact. SHA-256 binds source bytes; it is not a signature or
an attestation that a supplied snapshot was actually captured from the game.
"""
from __future__ import annotations

import base64
import copy
from datetime import datetime, timezone
import hashlib
import json
import re

from .campaign_store import decode


class ResolutionError(ValueError):
    pass


def _require(condition, message):
    if not condition:
        raise ResolutionError(message)


def _object(value, keys=None):
    _require(type(value) is dict, 'expected JSON object')
    if keys is not None:
        _require(set(value) == set(keys), 'unexpected fields')
    return value


def _integer(value, minimum=1):
    _require(type(value) is int and value >= minimum, 'invalid integer')
    return value


def _text(value, pattern=None):
    _require(type(value) is str and bool(value), 'invalid string')
    if pattern:
        _require(re.fullmatch(pattern, value) is not None, 'invalid string format')
    return value


def _equal(actual, expected, message):
    # Python considers True == 1; proof identities and gates must not.
    _require(type(actual) is type(expected) and actual == expected, message)


def canonical_bytes(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'),
                      ensure_ascii=False, allow_nan=False).encode('utf-8')


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        _require(key not in result, 'duplicate JSON key')
        result[key] = value
    return result


def load_json_bytes(raw):
    _require(type(raw) is bytes, 'source must be exact bytes')
    try:
        return json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs,
                          parse_constant=lambda _: (_ for _ in ()).throw(ResolutionError('nonfinite JSON')))
    except (UnicodeError, ValueError) as exc:
        raise ResolutionError('invalid source JSON') from exc


def _source(content, raw=None):
    _object(content)
    raw = canonical_bytes(content) if raw is None else raw
    _require(canonical_bytes(load_json_bytes(raw)) == canonical_bytes(content), 'source bytes disagree with content')
    return {'content': copy.deepcopy(content), 'sha256': hashlib.sha256(raw).hexdigest(),
            'raw_base64': base64.b64encode(raw).decode('ascii')}


def _read_source(source):
    _object(source, ('content', 'sha256', 'raw_base64'))
    _text(source['sha256'], '[0-9a-f]{64}')
    _text(source['raw_base64'])
    try:
        raw = base64.b64decode(source['raw_base64'], validate=True)
    except ValueError as exc:
        raise ResolutionError('invalid base64 source') from exc
    _equal(hashlib.sha256(raw).hexdigest(), source['sha256'], 'source hash mismatch')
    _require(canonical_bytes(load_json_bytes(raw)) == canonical_bytes(source['content']), 'embedded content mismatch')
    return _object(source['content'])


def _utc(value):
    _text(value)
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError as exc:
        raise ResolutionError('invalid UTC timestamp') from exc
    _require(parsed.tzinfo is not None and parsed.utcoffset().total_seconds() == 0, 'timestamp must be UTC')
    return parsed


def _origin(checkpoint, guard):
    try:
        state = decode(checkpoint)
    except (ValueError, TypeError, KeyError) as exc:
        raise ResolutionError('invalid original checkpoint') from exc
    _text(state.run_id, '[0-9a-f]{32}')
    _require(state.mode.value == 'blocked' and state.reason == 'bridge_attempt_unknown', 'checkpoint is not an UNKNOWN block')
    p = _object(checkpoint['pending'])
    _require(p['kind'] == 'challenge' and p['uncertain'] is True, 'pending is not an uncertain challenge')
    attempt = _text(p['attempt_id'], '[0-9a-f]{32}')
    _equal(p['intent_id'], attempt, 'intent/attempt mismatch')
    context = load_json_bytes(state.context_key.encode('utf-8'))
    _object(context, ('character_id', 'creation', 'generation', 'nonce', 'pid'))
    _integer(context['generation'])
    pid, creation = _integer(context['pid']), _integer(context['creation'])
    role = _text(context['character_id'], '[1-9][0-9]{0,19}')
    _require(int(role) <= 2**64 - 1, 'invalid role identity')
    session = _text(context['nonce'], '[0-9a-f]{32}')
    _object(guard)
    _require({'business', 'character_id', 'created_at', 'pid', 'prior_unresolved_business',
              'process_creation_filetime', 'protocol', 'retained_at', 'session_id'} <= set(guard), 'partial original guard')
    _require(set(guard) <= {'business', 'character_id', 'created_at', 'pid', 'prior_unresolved_business',
                           'process_creation_filetime', 'protocol', 'retained_at', 'session_id', 'nonce', 'bridge_id'},
             'unexpected guard fields')
    for key, expected in (('protocol', 3), ('pid', pid), ('process_creation_filetime', creation),
                          ('session_id', session), ('character_id', role)):
        _equal(guard.get(key), expected, 'guard identity mismatch: ' + key)
    if 'nonce' in guard:
        _equal(guard['nonce'], session, 'guard nonce mismatch')
    if 'bridge_id' in guard:
        _equal(guard['bridge_id'], 'jiangshen-assistant-win32-v1', 'guard bridge mismatch')
    _require(_utc(guard['created_at']) <= _utc(guard['retained_at']) <= datetime.now(timezone.utc), 'invalid guard chronology')
    _require(guard.get('prior_unresolved_business') is None, 'inherited unresolved business requires separate review')
    business = _object(guard.get('business'), ('attempt', 'character_id', 'mutation_attempted', 'pending_attempt', 'resource_rollback'))
    for key, expected in (('character_id', role), ('mutation_attempted', True), ('pending_attempt', True), ('resource_rollback', False)):
        _equal(business[key], expected, 'original business mismatch: ' + key)
    a = _object(business['attempt'], ('attempt_id', 'campaign_id', 'character_id', 'command_seq', 'evidence',
                                     'outcome', 'report_id', 'status', 'troop_id'))
    for key, expected in (('attempt_id', attempt), ('character_id', role), ('outcome', 'UNKNOWN'), ('status', 'UNKNOWN'), ('report_id', '0')):
        _equal(a[key], expected, 'original attempt mismatch: ' + key)
    campaign, troop, seq = _integer(a['campaign_id']), _integer(a['troop_id']), _integer(a['command_seq'])
    _text(a['evidence'])
    _equal(p['stage_key'], f'{campaign}:{troop}', 'original target mismatch')
    return {'pid': pid, 'process_creation_filetime': creation, 'character_id': role,
            'original_session_id': session, 'attempt_id': attempt, 'command_seq': seq,
            'campaign_id': campaign, 'troop_id': troop}


def _empty_attempt(attempt):
    _object(attempt, ('attempt_id', 'character_id', 'command_seq', 'campaign_id', 'troop_id',
                      'status', 'report_id', 'outcome', 'evidence'))
    for key, expected in (('attempt_id', ''), ('character_id', ''), ('command_seq', 0), ('campaign_id', 0),
                          ('troop_id', 0), ('status', 'NONE'), ('report_id', '0'), ('outcome', 'UNKNOWN'), ('evidence', 'not_started')):
        _equal(attempt[key], expected, 'helper has an attempt: ' + key)


def _count(context, kind, visible, matching):
    count = _object(_object(context.get('counts')).get(kind), ('total', 'visible', 'matching'))
    _integer(count['total'], 0)
    _equal(count['visible'], visible, 'unexpected visible ' + kind)
    _equal(count['matching'], matching, 'unexpected matching ' + kind)
    _require(count['total'] >= max(visible, matching), 'invalid UI count')


def _proof(state, identity, *, after):
    _object(state)
    for key, expected in (('protocol', 3), ('bridge_id', 'jiangshen-assistant-win32-v1'),
                          ('pid', identity['pid']), ('process_creation_filetime', identity['process_creation_filetime']),
                          ('lifecycle', 'ready'), ('ready', True), ('main_thread_verified', True), ('component_destroyed', False)):
        _equal(state.get(key), expected, 'proof identity/lifecycle mismatch: ' + key)
    session = _text(state.get('session_id'), '[0-9a-f]{32}')
    _equal(state.get('nonce'), session, 'proof session/nonce mismatch')
    _require(session != identity['original_session_id'], 'proof must identify a fresh helper')
    _text(state.get('assembly'), 'JSAssistantBridge_[0-9a-f]{16}')
    _integer(state.get('verified_native_tid'))
    _equal(state.get('managed_tid'), 1, 'proof not on managed main thread')
    _integer(state.get('frame'), 0)
    _integer(state.get('last_command_seq'), 0)
    _utc(state.get('updated_utc'))
    action = _object(state.get('action'))
    for key, expected in (('mutation_attempted', False), ('command_seq', 0), ('phase', 'none'), ('outcome', 'NONE')):
        _equal(action.get(key), expected, 'helper action attempted')
    gui = _object(state.get('campaign_gui'))
    for key, expected in (('command_seq', 0), ('phase', 'none'), ('invocation_attempted', False)):
        _equal(gui.get(key), expected, 'helper GUI mutation attempted')
    business = _object(state.get('business'), ('character_id', 'mutation_attempted', 'pending_attempt', 'resource_rollback', 'attempt'))
    for key, expected in (('character_id', ''), ('mutation_attempted', False), ('pending_attempt', False), ('resource_rollback', False)):
        _equal(business[key], expected, 'helper business is not empty')
    _empty_attempt(business['attempt'])
    c = _object(state.get('campaign'))
    for key, expected in (('schema', 1), ('interface', 'scoped-campaign-gui-skip-v1'), ('available', True), ('gate', 'PASS'),
                          ('character_id', identity['character_id']), ('campaign_id', identity['campaign_id']),
                          ('troop_id', 0 if after else identity['troop_id']), ('phase', 'CAMPAIGN' if after else 'RESULT'),
                          ('result_kind', '' if after else 'NORMAL')):
        _equal(c.get(key), expected, 'campaign proof mismatch: ' + key)
    _integer(c.get('revision'), 0)
    _empty_attempt(c.get('attempt'))
    tutorial = _object(c.get('tutorial'), ('kind', 'step', 'gate'))
    _require(tutorial == {'kind': 'NONE', 'step': '', 'gate': ''}, 'tutorial not settled')
    ui = _object(c.get('ui_context'))
    root = _integer(ui.get('current_root_instance_id'))
    for key, expected in (('current_root_level', 'Campaign'), ('current_root_active', after), ('global_input_enabled', True),
                          ('selection_gate', 'PASS'), ('candidates_truncated', False)):
        _equal(ui.get(key), expected, 'UI proof mismatch: ' + key)
    for kind, visible, matching in (('root_ui', 1, 1), ('campaign', int(after), int(after)),
                                    ('battle', int(not after), int(not after)), ('normal_result', int(not after), int(not after)),
                                    ('reward', 0, 0), ('scenario', 0, 0)):
        _count(ui, kind, visible, matching)
    selection = _object(c.get('selection_diagnostics'))
    _equal(selection.get('character_campaign_id'), identity['campaign_id'], 'selected campaign mismatch')
    _equal(selection.get('truncated'), False, 'selection truncated')
    flags = _object(selection.get('campaign'))
    for key, expected in (('type', 0), ('enabled', after), ('unlocked', True), ('finished', False), ('m_bDebug', False),
                          ('m_bPostInit', False), ('m_bBorning', False), ('m_bBornWant', False), ('m_bScenarioVisible', False), ('m_bSignActiving', False)):
        _equal(flags.get(key), expected, 'campaign is not stable: ' + key)
    d = _object(c.get('settlement_diagnostics'), ('anchor_scope', 'character_id', 'observer', 'battle', 'result', 'target'))
    _equal(d['anchor_scope'], 'this_helper_instance', 'invalid diagnostic scope')
    _equal(d['character_id'], identity['character_id'], 'diagnostic role mismatch')
    observer = _object(d['observer'], ('status', 'anchors_available', 'zero_observed', 'battle_observed', 'receipt_captured',
                                      'campaign_match', 'battle_match', 'character_match', 'character_id_match', 'report_token_match'))
    for key, value in observer.items():
        _equal(value, 'NONE' if key == 'status' else False if key == 'anchors_available' else None, 'new helper cannot supply historical anchors')
    b = _object(d['battle'])
    _require('packet' in b, 'partial battle diagnostic')
    _integer(b.get('instance_id'))
    parent = _integer(b.get('parent_instance_id'))
    _integer(b.get('owner_root_instance_id'))
    for key, expected in (('previous_root_instance_id', root), ('visible', not after), ('parent_matches_campaign', True),
                          ('parent_owned_by_current', True), ('previous_root_matches_current', True),
                          ('idcampaign', identity['campaign_id']), ('idtroop', identity['troop_id']), ('view_type', 3),
                          ('replay', False), ('cross', False), ('surrender', False), ('waiting', False), ('use_end', False),
                          ('fighting', False), ('pending_present', False), ('packet', None)):
        _equal(b.get(key), expected, 'battle proof mismatch: ' + key)
    _text(b.get('report_id'), '[1-9][0-9]*')
    target = _object(d['target'])
    _require('data_anchor_match' in target, 'partial target diagnostic')
    for key, expected in (('idcampaign', identity['campaign_id']), ('idtroop', identity['troop_id']), ('matches_battle_target', True),
                          ('finish_count', 1), ('finish_max', 1), ('killed', True), ('campaign_finished', False), ('data_present', True),
                          ('data_anchor_match', None)):
        _equal(target.get(key), expected, 'one-time target is not proven cleared: ' + key)
    if after:
        _require(d['result'] is None, 'result still visible')
        _count(ui, 'battle_baseline', 0, 1)
        candidates = ui.get('campaign_candidates')
        _require(type(candidates) is list and all(type(x) is dict for x in candidates), 'invalid campaign candidates')
        owned = [x for x in candidates if x.get('visible') is True]
        _require(len(owned) == 1, 'current campaign not unique')
        for key, expected in (('instance_id', parent), ('owner_matches_current', True), ('hierarchy_matches_current', True), ('owner_root_level', 'Campaign')):
            _equal(owned[0].get(key), expected, 'current campaign ownership mismatch')
    else:
        result = _object(d['result'])
        _require(type(result.get('instance_id')) is int and result['instance_id'] != 0, 'invalid result instance')
        for key, expected in (('report_id', b['report_id']), ('battle_match', True), ('root_match', True), ('battle_view', False)):
            _equal(result.get(key), expected, 'normal result does not match battle')
    return c, ui, b


def _validate_proof(checkpoint, guard, before, after):
    identity = _origin(checkpoint, guard)
    bc, bui, bb = _proof(before, identity, after=False)
    ac, aui, ab = _proof(after, identity, after=True)
    _require(_utc(guard['created_at']) <= _utc(before['updated_utc']), 'proof predates original business')
    for key in ('session_id', 'nonce', 'assembly', 'verified_native_tid', 'managed_tid'):
        _equal(after.get(key), before.get(key), 'proof helper changed: ' + key)
    _equal(aui['current_root_instance_id'], bui['current_root_instance_id'], 'campaign root changed')
    for key in ('instance_id', 'parent_instance_id', 'owner_root_instance_id', 'previous_root_instance_id', 'idcampaign', 'idtroop', 'report_id', 'view_type'):
        _equal(ab[key], bb[key], 'hidden battle changed: ' + key)
    _require(_utc(after['updated_utc']) > _utc(before['updated_utc']), 'proof time did not advance')
    _require(after['frame'] > before['frame'] and ac['revision'] > bc['revision'], 'proof state did not advance')
    _require(after['last_command_seq'] >= before['last_command_seq'], 'proof command sequence regressed')
    identity['helper_session_id'] = before['session_id']
    return identity


def prepare_resolution(original_checkpoint: dict, guard: dict, before_state: dict, after_state: dict, *,
                       checkpoint_bytes=None, guard_bytes=None, before_bytes=None, after_bytes=None) -> dict:
    """Build a reviewable candidate. Dict callers use canonical bytes by default.

    File callers must pass the exact original bytes to preserve file identity.
    This function has no filesystem or game effects.
    """
    identity = _validate_proof(original_checkpoint, guard, before_state, after_state)
    result = {'version': 1, 'disposition': 'target_already_cleared',
              'run_id': original_checkpoint['run_id'], 'context_key': original_checkpoint['context_key'],
              'identity': identity, 'created_utc': datetime.now(timezone.utc).isoformat(),
              'historical_outcome': 'UNKNOWN', 'consumes_result': False, 'resets_losses': False,
              'sources': {'checkpoint': _source(original_checkpoint, checkpoint_bytes), 'guard': _source(guard, guard_bytes),
                          'before': _source(before_state, before_bytes), 'after': _source(after_state, after_bytes)}}
    validate_resolution(result)
    return result


def validate_resolution(resolution: dict) -> None:
    """Recheck every embedded byte and semantic obligation; no approval marker."""
    r = _object(resolution, ('version', 'disposition', 'run_id', 'context_key', 'identity', 'created_utc',
                             'historical_outcome', 'consumes_result', 'resets_losses', 'sources'))
    for key, expected in (('version', 1), ('disposition', 'target_already_cleared'), ('historical_outcome', 'UNKNOWN'),
                          ('consumes_result', False), ('resets_losses', False)):
        _equal(r[key], expected, 'unsupported resolution semantics: ' + key)
    s = _object(r['sources'], ('checkpoint', 'guard', 'before', 'after'))
    checkpoint, guard, before, after = (_read_source(s[key]) for key in ('checkpoint', 'guard', 'before', 'after'))
    identity = _validate_proof(checkpoint, guard, before, after)
    _require(canonical_bytes(r['identity']) == canonical_bytes(identity), 'resolution identity differs from proof')
    _equal(r['run_id'], checkpoint['run_id'], 'resolution run mismatch')
    _equal(r['context_key'], checkpoint['context_key'], 'resolution context mismatch')
    created = _utc(r['created_utc'])
    _require(_utc(after['updated_utc']) <= created <= datetime.now(timezone.utc), 'resolution timestamp invalid or future')


def resolve_checkpoint(checkpoint_bytes: bytes, resolution: dict) -> bool:
    """Whether this exact checkpoint has a complete independent disposition."""
    try:
        validate_resolution(resolution)
        source = resolution['sources']['checkpoint']
        return (type(checkpoint_bytes) is bytes and hashlib.sha256(checkpoint_bytes).hexdigest() == source['sha256']
                and canonical_bytes(load_json_bytes(checkpoint_bytes)) == canonical_bytes(source['content']))
    except (ResolutionError, ValueError, TypeError, KeyError, OverflowError, RecursionError):
        return False


def resolved_business(guard_fact: dict, resolution: dict, *, pid: int, process_creation_filetime: int) -> bool:
    """Match exactly one fact with explicit process ownership from the caller.

    Retention timestamps may change. The complete business content, frozen role,
    original session, process identity and all proof obligations may not.
    Inherited facts must be visited individually by the caller.
    """
    try:
        validate_resolution(resolution)
        _object(guard_fact)
        _require(set(guard_fact) <= {'business', 'character_id', 'created_at', 'pid', 'prior_unresolved_business',
                                    'process_creation_filetime', 'protocol', 'retained_at', 'session_id', 'nonce', 'bridge_id'},
                 'unexpected business fact fields')
        guard = resolution['sources']['guard']['content']
        _integer(pid)
        _integer(process_creation_filetime)
        _equal(pid, guard['pid'], 'caller PID mismatch')
        _equal(process_creation_filetime, guard['process_creation_filetime'], 'caller process creation mismatch')
        for key in ('session_id', 'character_id'):
            _equal(guard_fact.get(key), guard[key], 'business identity mismatch')
        for key in ('protocol', 'pid', 'process_creation_filetime', 'nonce', 'bridge_id'):
            if key in guard_fact:
                expected = guard.get(key, guard['session_id'] if key == 'nonce' else 'jiangshen-assistant-win32-v1')
                _equal(guard_fact[key], expected, 'business process identity mismatch')
        _require(guard_fact.get('prior_unresolved_business') is None, 'inherited business not resolved')
        _require(canonical_bytes(guard_fact.get('business')) == canonical_bytes(guard['business']), 'business content changed')
        return True
    except (ResolutionError, ValueError, TypeError, KeyError, OverflowError, RecursionError):
        return False
