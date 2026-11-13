"""An explicit recorded defeat can resolve its exact old observation gap.

The original native UNKNOWN remains in its session. This local decision counts
the visible defeat for the three-loss policy; it asserts no server receipt.
"""
from __future__ import annotations

import hashlib

from .campaign import Mode, Outcome
from .campaign_recovery import canonical_bytes, load_json_bytes
from .campaign_store import decode


def recorded_loss_resolves(fact, proof, checkpoint_bytes, *, pid, process_creation_filetime):
    if (type(proof) is not dict or proof.get('kind') != 'recorded_battle_loss' or
            proof.get('version') != 1 or proof.get('server_receipt') != 'not_captured' or
            not isinstance(proof.get('reason'), str) or not proof['reason']):
        return False
    before = decode(proof['checkpoint_before'])
    current = decode(load_json_bytes(checkpoint_bytes))
    pending = before.pending
    if (before.mode != Mode.BLOCKED or before.reason != 'bridge_attempt_unknown' or
            pending is None or pending.kind != 'challenge' or not pending.uncertain or
            not pending.attempt_id or pending.attempt_id != pending.intent_id or
            current.run_id != before.run_id or proof.get('run_id') != before.run_id):
        return False
    old_owner = load_json_bytes(before.context_key.encode('utf-8'))
    new_owner = load_json_bytes(current.context_key.encode('utf-8'))
    identity = {'pid': pid, 'creation': process_creation_filetime,
                'character_id': old_owner.get('character_id')}
    if (any(type(old_owner.get(k)) is not type(v) or old_owner.get(k) != v or
            type(new_owner.get(k)) is not type(v) or new_owner.get(k) != v
            for k, v in identity.items()) or not identity['character_id']):
        return False
    business = fact.get('business')
    if (fact.get('session_id') != old_owner.get('nonce') or
            proof.get('session_id') != old_owner.get('nonce') or
            fact.get('character_id') != identity['character_id'] or
            type(business) is not dict or business.get('pending_attempt') is not True or
            business.get('mutation_attempted') is not True or
            business.get('pending_development') is True or business.get('pending_player_refresh') is True or
            business.get('character_id') != identity['character_id'] or
            hashlib.sha256(canonical_bytes(business)).hexdigest() != proof.get('business_sha256')):
        return False
    attempt = business.get('attempt')
    if type(attempt) is not dict:
        return False
    if (attempt.get('attempt_id') != pending.attempt_id or
            attempt.get('character_id') != identity['character_id'] or
            attempt.get('status') != 'UNKNOWN' or attempt.get('outcome') != 'UNKNOWN' or
            attempt.get('report_id') != '0' or
            pending.stage_key != '%s:%s' % (attempt.get('campaign_id'), attempt.get('troop_id'))):
        return False
    key = 'result:' + pending.attempt_id
    if (proof.get('result_key') != key or len(current.consumed) <= len(before.consumed) or
            current.consumed[:len(before.consumed)] != before.consumed):
        return False
    recorded = current.consumed[len(before.consumed)]
    return (recorded.attempt_id == pending.attempt_id and recorded.report_id == key and
            recorded.outcome == Outcome.LOSS and
            (current.pending is None or current.pending.attempt_id != pending.attempt_id))
