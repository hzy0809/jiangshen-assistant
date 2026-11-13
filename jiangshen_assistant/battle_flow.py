"""Shared per-battle handoff boundary, independent of activity settlement policy."""
from __future__ import annotations


class BattleYieldRequested(Exception):
    """A yield won the next-start gate before the native action was submitted."""


def battle_flow(observed, *, pending=False, reservation=False, navigation=False, paused=False):
    """Describe why a live owner can or cannot hand off its action channel.

    Settled receipts alone do not establish a handoff: the result page, native
    story flow, business operations and owned navigation must also be clear.
    Activity coordinators retain all result counting and callback decisions.
    """
    snap = observed.get('campaign') or {}
    phase = snap.get('phase', '')
    attempt = snap.get('attempt') or {}
    native = snap.get('flow') or {}
    business = (observed.get('state') or {}).get('business') or {}
    reason = ''
    if pending or reservation:
        reason = 'battle_pending'
    elif navigation:
        reason = 'navigation_pending'
    elif phase == 'RESULT':
        reason = 'result_page_pending'
    elif phase not in ('MAIN', 'CAMPAIGN', 'CHALLENGE_LIST', 'TROOP', 'FORMATION'):
        reason = 'scene_not_idle'
    elif attempt.get('status') not in ('NONE', 'SETTLED', 'CONTINUED'):
        reason = 'attempt_not_resolved'
    elif native.get('status') != 'IDLE':
        reason = 'native_flow_pending'
    elif snap.get('available') is not True or snap.get('gate') != 'PASS':
        reason = 'scene_gate_closed'
    elif any(business.get(key) is not False for key in
             ('pending_attempt', 'pending_development', 'pending_player_refresh')):
        reason = 'business_not_idle'
    elif any(value is True for key, value in business.items() if key.startswith('pending_')):
        reason = 'business_pending'
    elif observed.get('error'):
        reason = 'owner_state_unknown'
    elif observed.get('stopping') or not paused and observed.get('admission_open') is not True:
        reason = 'owner_admission_closed'
    return {'phase': phase, 'attempt_id': attempt.get('attempt_id', ''),
            'attempt_status': attempt.get('status', ''), 'handoff_ready': not reason,
            'handoff_reason': reason}
