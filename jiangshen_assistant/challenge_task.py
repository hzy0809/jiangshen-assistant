"""Ordinary challenge policy using the shared current-run battle lifecycle."""
from __future__ import annotations

from dataclasses import replace
import json

from .bridge_client import _challenge_valid
from .campaign import CampaignError, GuideKind
from .campaign_task import CampaignCoordinator


class ChallengeCoordinator(CampaignCoordinator):
    TASK_LABEL = '挑战'
    RUN_DIRECTORY = 'challenge_runs'
    LOSS_MODE = 'cumulative'
    SUPPORT_GROWTH = False
    GENERAL_ACTIONS = frozenset()

    def __init__(self, objects, runner, data_dir, **kwargs):
        super().__init__(objects, runner, data_dir, **kwargs)
        self._submitted_sequences = {}
        self._result_navigation = None
        self._entry_navigation = None

    def _launch_locked(self, *, expected_character_id=None, expected_user_id=None, expected_zone_id=None,
                       allow_growth=True):
        self._submitted_sequences = {}
        self._result_navigation = None
        self._entry_navigation = None
        return super()._launch_locked(expected_character_id=expected_character_id,
                                      expected_user_id=expected_user_id, expected_zone_id=expected_zone_id,
                                      allow_growth=allow_growth)

    def _acquire(self, run_id):
        return self.objects.acquire_campaign(run_id, scope='challenge')

    def _loss_message(self, losses):
        return '本场挑战失败，累计失败 %s/3。' % losses

    @staticmethod
    def _result_page_key(snap):
        if snap.get('phase') != 'RESULT':
            return None
        return (snap.get('campaign_id'), snap.get('result_kind'),
                (snap.get('ui_context') or {}).get('current_root_instance_id'))

    def _submit(self, token, snap, action, attempt_id=''):
        page = self._result_page_key(snap)
        wire = super()._submit(token, snap, action, attempt_id)
        ack = wire.get('ack') or {}
        if action == 'enter_challenge' and ack.get('status') == 'COMPLETED':
            receipt = self._receipt(wire, action, attempt_id)
            if receipt.get('invoked') is True:
                entry = ((ack.get('result') or {}).get('challenge') or {}).get('entry') or {}
                if (entry.get('command_seq') != receipt['command_seq']
                        or entry.get('campaign_id') != snap.get('campaign_id')):
                    raise CampaignError('challenge_entry_receipt_mismatch')
                self._entry_navigation = (receipt['command_seq'], snap['campaign_id'])
        if page is not None and action in ('ack_result', 'next_chapter') and ack.get('status') == 'COMPLETED':
            receipt = self._receipt(wire, action, attempt_id)
            if receipt.get('invoked') is True:
                # A correlated callback invocation is not a completed page
                # transition. Keep this old UI reserved across revisions.
                self._result_navigation = (action, page, self._clock(), receipt['command_seq'])
        return wire

    def _navigation_wait(self, campaign, snap):
        pending = self._result_navigation
        if pending is None:
            return False
        action, page, started, _sequence = pending
        phase = snap.get('phase')
        different_result = phase == 'RESULT' and self._result_page_key(snap) != page
        destinations = {'CHALLENGE_LIST'} if action == 'next_chapter' else {
            'CAMPAIGN', 'TROOP', 'FORMATION', 'CHALLENGE_LIST', 'SCENARIO'}
        if phase in destinations or different_result:
            self._result_navigation = None
            return False
        if phase not in ('RESULT', 'MAIN', 'CAMPAIGN'):
            self._result_navigation = None
            return False
        ui = snap.get('ui_context') or {}
        returning_to_destination = (action == 'next_chapter' and phase in ('MAIN', 'CAMPAIGN')
            and snap.get('available') is False and snap.get('campaign_type') == 0
            and snap.get('gate') == 'normal_challenge_type2_required'
            and ui.get('current_root_level') in ('Campaign', 'Capital')
            and ui.get('current_root_active') is True and ui.get('selection_gate') == 'PASS')
        if snap.get('gate') != 'PASS' and not returning_to_destination and not self._game_transition(snap):
            # The owned return never hides an unrelated popup or unknown gate.
            return False
        if self._clock() - max(started, self._yield_requested_at or started) >= self._transition_timeout:
            self._result_navigation = None
            campaign.stop('challenge_result_navigation_timeout')
            self.stop(reason=campaign.state.reason)
        return True

    def _loss_limit_scene_restored(self):
        # The shared terminal cleanup bypasses the ordinary ACTIVE planner and
        # waits for physical restoration itself. Retire the ACK reservation only
        # after that proof; never feed it back into normal challenge navigation.
        self._result_navigation = None

    def _observation(self, campaign, snap):
        if not _challenge_valid(snap):
            raise CampaignError('challenge_observation_invalid')
        observation = super()._observation(campaign, snap)
        # Normal battle story/loss-advice uses the shared continuation path.
        # Recruitment and unrelated tutorial flows belong to other tasks.
        if (snap.get('tutorial') or {}).get('kind') == 'GENERAL':
            observation = replace(observation, guide=GuideKind.UNKNOWN)
        return observation

    def _plan_action(self, campaign, snap, observation):
        if snap['phase'] == 'CHALLENGE_LIST':
            rows = sorted((row for row in snap['challenge_candidates']
                           if row['campaign_type'] == 2 and row['finished'] is False
                           and row['unlocked'] is True and row['gate'] == 'PASS'),
                          key=lambda row: row['display_index'])
            if not rows:
                campaign.stop('challenge_no_eligible_normal')
                self.stop(reason=campaign.state.reason)
                return None, snap, False
            return 'enter_challenge', {**snap, 'campaign_id': rows[0]['campaign_id'], 'troop_id': 0}, False
        if (snap['phase'] not in ('MAIN', 'BLOCKED') and snap['campaign_type'] != 2):
            campaign.stop('challenge_normal_only')
            self.stop(reason=campaign.state.reason)
            return None, snap, False
        return super()._plan_action(campaign, snap, observation)

    def _game_transition(self, snap):
        if not super()._game_transition(snap):
            return False
        entry = snap.get('entry') or {}
        if entry.get('status') == 'WAITING':
            # Readiness comes from native flow; this is only this run's
            # command ownership, never a second loading/animation classifier.
            observed = (entry.get('command_seq'), entry.get('campaign_id'))
            if self._entry_navigation is None or observed != self._entry_navigation:
                raise CampaignError('challenge_entry_receipt_mismatch')
        return True

    def _receipt(self, wire, action, attempt_id):
        ack = wire.get('ack') or {}
        receipt = (ack.get('result') or {}).get('challenge_command') or {}
        seq = wire.get('requested_seq', wire.get('seq'))
        if (type(seq) is not int or ack.get('command_seq') != seq or receipt.get('command_seq') != seq
                or receipt.get('action') != action or receipt.get('attempt_id') != attempt_id):
            raise CampaignError('challenge_ack_correlation_failed')
        if action == 'start' and ack.get('status') == 'COMPLETED' and receipt.get('invoked') is True:
            self._submitted_sequences[attempt_id] = seq
        return receipt

    def _settled_result(self, run_id, context_key, attempt, *, stage_key=None):
        result = super()._settled_result(run_id, context_key, attempt, stage_key=stage_key, challenge=True)
        try:
            character_id = json.loads(context_key)['character_id']
        except (ValueError, TypeError, KeyError):
            character_id = None
        own_receipt = (character_id is not None and attempt.get('character_id') == character_id
                       and self._submitted_sequences.get(attempt.get('attempt_id')) == attempt.get('command_seq'))
        return replace(result, confirmed=result.confirmed and own_receipt)

    def _rejected_action(self, campaign, reason):
        campaign.stop(reason)
        self.stop(reason=reason)

    def _unpending_error(self, campaign, reason):
        campaign.stop(reason)
