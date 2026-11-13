import json
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch

from jiangshen_assistant.campaign import Campaign, CampaignError, GuideKind, Mode, Observation, Outcome, Result
from jiangshen_assistant.campaign_store import CampaignStore, CheckpointError


class CampaignTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = CampaignStore(Path(self.tmp.name) / 'run.json')
        self.run = Campaign.create(self.store, 'run-1', 'identity/session/generation')
        self.revision = 0
        self.run.resume(self.obs())

    def obs(self, **kwargs):
        self.revision += 1
        return Observation('run-1', 'identity/session/generation', self.revision, ('observed-stage',), **kwargs)

    def challenge(self, n):
        intent = self.run.prepare(self.obs(), f'intent-{n}')
        self.run.mark_submitted(intent.intent_id, f'attempt-{n}')
        return intent

    def result(self, n, outcome, **kwargs):
        values = dict(run_id='run-1', context_key='identity/session/generation', attempt_id=f'attempt-{n}',
                      report_id=f'report-{n}', outcome=outcome, confirmed=True)
        values.update(kwargs)
        return Result(**values)

    def test_only_three_consecutive_losses_stop(self):
        for n, outcome in enumerate((Outcome.LOSS, Outcome.WIN, Outcome.LOSS, Outcome.WIN, Outcome.LOSS), 1):
            self.challenge(n)
            self.assertTrue(self.run.record_result(self.result(n, outcome)))
        self.assertEqual(self.run.state.losses, 1)
        self.assertEqual(self.run.state.mode, Mode.ACTIVE)
        for n in (6, 7):
            self.challenge(n)
            self.run.record_result(self.result(n, Outcome.LOSS))
        self.assertEqual(self.run.state.losses, 3)
        self.assertEqual(self.run.state.mode, Mode.LOSS_LIMIT)
        self.assertEqual(self.run.state.reason, 'three_consecutive_losses')
        self.assertEqual(self.store.load(), self.run.state)
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'eighth')
        with self.assertRaises(CampaignError):
            self.run.resume(self.obs())

    def test_duplicate_old_win_does_not_reset_later_streak(self):
        self.challenge(1)
        old_win = self.result(1, Outcome.WIN)
        self.run.record_result(old_win)
        self.challenge(2)
        self.run.record_result(self.result(2, Outcome.LOSS))
        self.assertFalse(self.run.record_result(old_win))
        self.assertEqual(self.run.state.losses, 1)
        self.assertEqual(self.store.load().losses, 1)

    def test_unknown_does_not_reset_streak_and_restart_preserves_it(self):
        self.challenge(1)
        self.run.record_result(self.result(1, Outcome.LOSS))
        self.challenge(2)
        self.assertFalse(self.run.record_result(self.result(2, Outcome.UNKNOWN)))
        self.assertEqual(self.run.state.losses, 1)
        reopened = Campaign.open(self.store)
        self.assertEqual(reopened.state.losses, 1)
        self.assertEqual(reopened.state.pending.attempt_id, 'attempt-2')
        self.assertTrue(reopened.record_result(self.result(2, Outcome.LOSS)))
        self.assertEqual(reopened.state.losses, 2)
        self.assertEqual(self.store.load().losses, 2)

    def test_exact_duplicate_and_conflicting_result(self):
        self.challenge(1)
        loss = self.result(1, Outcome.LOSS)
        self.assertTrue(self.run.record_result(loss))
        self.assertFalse(self.run.record_result(loss))
        with self.assertRaises(CampaignError):
            self.run.record_result(self.result(1, Outcome.WIN))
        self.assertEqual(self.run.state.losses, 1)
        self.assertEqual(self.run.state.mode, Mode.BLOCKED)
        self.run.pause()
        with self.assertRaises(CampaignError):
            self.run.resume(self.obs())

    def test_stale_context_unmatched_and_unconfirmed_preserve_pending(self):
        self.challenge(1)
        for result in (self.result(1, Outcome.LOSS, context_key='old-context'),
                       self.result(1, Outcome.LOSS, attempt_id='old-attempt')):
            with self.assertRaises(CampaignError):
                self.run.record_result(result)
        self.assertEqual(self.run.state.losses, 0)
        self.assertFalse(self.run.record_result(self.result(1, Outcome.LOSS, confirmed=False)))
        self.assertEqual(self.run.state.mode, Mode.BLOCKED)
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'intent-2')
        self.assertTrue(self.run.record_result(self.result(1, Outcome.WIN)))
        self.assertEqual(self.run.state.mode, Mode.PAUSED)
        self.assertEqual(self.run.state.losses, 0)

    def test_conflict_during_later_pending_attempt_remains_blocked_after_reconciliation(self):
        self.challenge(1)
        self.run.record_result(self.result(1, Outcome.WIN))
        self.challenge(2)
        with self.assertRaises(CampaignError):
            self.run.record_result(self.result(1, Outcome.LOSS))
        self.run.mark_unknown('timeout')
        self.assertEqual(self.run.state.reason, 'conflicting_result')
        self.assertTrue(self.run.record_result(self.result(2, Outcome.WIN)))
        self.assertEqual(self.run.state.mode, Mode.BLOCKED)
        with self.assertRaises(CampaignError):
            self.run.resume(self.obs())

    def test_rejected_unsubmitted_intent_needs_matching_id_and_new_observation(self):
        observed = self.obs()
        self.run.prepare(observed, 'intent-1')
        with self.assertRaises(CampaignError):
            self.run.reject_intent('old-intent', 'rejected')
        self.assertEqual(self.run.state.pending.intent_id, 'intent-1')
        with self.assertRaises(CampaignError):
            self.run.reject_intent('intent-1', '')
        self.run.reject_intent('intent-1', 'rejected_without_invocation')
        self.assertIsNone(self.store.load().pending)
        with self.assertRaises(CampaignError):
            self.run.prepare(observed, 'intent-2')
        self.assertEqual(self.run.prepare(self.obs(), 'intent-2').intent_id, 'intent-2')

    def test_unknown_intent_cannot_be_rejected_even_after_restart_or_stop(self):
        self.run.prepare(self.obs(), 'intent-1')
        self.run.mark_unknown('timeout')
        self.run.stop()
        reopened = Campaign.open(self.store)
        self.assertTrue(reopened.state.pending.uncertain)
        with self.assertRaises(CampaignError):
            reopened.reject_intent('intent-1', 'rejected')
        self.assertIsNotNone(self.store.load().pending)

    def test_submitted_attempt_cannot_be_rejected(self):
        self.challenge(1)
        with self.assertRaises(CampaignError):
            self.run.reject_intent('intent-1', 'rejected')
        self.assertEqual(self.store.load().pending.attempt_id, 'attempt-1')
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'intent-2')

    def test_reject_persistence_failure_prevents_new_admission(self):
        self.run.prepare(self.obs(), 'intent-1')
        with patch.object(self.store, 'save', side_effect=OSError('disk')):
            with self.assertRaises(OSError):
                self.run.reject_intent('intent-1', 'rejected')
        self.assertEqual(self.store.load().pending.intent_id, 'intent-1')
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'intent-2')

    def test_reject_reviewed_guide_retains_revision_and_does_not_mark_completed(self):
        observation = self.obs(guide=GuideKind.REVIEWED, step_key='dialogue', action_key='advance')
        self.run.prepare(observation, 'guide-1')
        self.run.reject_intent('guide-1', 'rejected_without_invocation')
        self.assertEqual(self.store.load().completed_guides, ())
        with self.assertRaises(CampaignError):
            self.run.prepare(observation, 'guide-2')
        later = self.obs(guide=GuideKind.REVIEWED, step_key='dialogue', action_key='advance')
        self.assertEqual(self.run.prepare(later, 'guide-2').step_key, 'dialogue')

    def test_proven_guide_rejection_allows_same_revision_only_after_explicit_resume(self):
        observation = self.obs(guide=GuideKind.REVIEWED, step_key='dialogue', action_key='skip')
        self.run.prepare(observation, 'guide-1')
        self.run.reject_intent('guide-1', 'gui_context_unavailable', retry_same_revision=True)
        self.assertEqual(self.store.load().mode, Mode.PAUSED)
        self.assertEqual(self.store.load().reason, 'gui_context_unavailable')
        self.assertIsNone(self.store.load().pending)
        self.assertEqual(self.store.load().completed_guides, ())
        self.assertEqual(self.store.load().losses, 0)
        with self.assertRaises(CampaignError):
            self.run.prepare(observation, 'automatic-retry')
        reopened = Campaign.open(self.store)
        with self.assertRaises(CampaignError):
            reopened.resume(replace(observation, revision=observation.revision - 1))
        with self.assertRaises(CampaignError):
            reopened.resume(replace(observation, context_key='another-session'))
        reopened.resume(observation)
        self.assertEqual(reopened.prepare(observation, 'guide-2').intent_id, 'guide-2')
        with self.assertRaises(CampaignError):
            reopened.prepare(observation, 'guide-3')

    def test_proven_challenge_rejection_preserves_streak_and_never_clears_submitted_attempt(self):
        self.challenge(1)
        self.run.record_result(self.result(1, Outcome.LOSS))
        observation = self.obs()
        self.run.prepare(observation, 'intent-2')
        self.run.reject_intent('intent-2', 'not_invoked', retry_same_revision=True)
        self.assertEqual(self.store.load().losses, 1)
        self.assertEqual(len(self.store.load().consumed), 1)
        self.run.resume(observation)
        self.run.prepare(observation, 'replacement-intent')
        self.run.mark_submitted('replacement-intent', 'attempt-2')
        with self.assertRaises(CampaignError):
            self.run.reject_intent('replacement-intent', 'not_invoked', retry_same_revision=True)
        self.assertEqual(self.store.load().pending.attempt_id, 'attempt-2')
        self.run.mark_unknown('transport_timeout')
        with self.assertRaises(CampaignError):
            self.run.reject_intent('replacement-intent', 'not_invoked', retry_same_revision=True)
        self.assertTrue(self.store.load().pending.uncertain)

    def test_retry_permission_never_reopens_stopped_run_or_returns_another_reservation(self):
        observation = self.obs()
        self.run.prepare(observation, 'intent-1')
        self.store.save(replace(self.store.load(), last_revision=observation.revision + 1))
        reopened = Campaign.open(self.store)
        with self.assertRaises(CampaignError):
            reopened.reject_intent('intent-1', 'not_invoked', retry_same_revision=True)
        self.assertIsNotNone(self.store.load().pending)
        reopened.stop()
        reopened.reject_intent('intent-1', 'not_invoked', retry_same_revision=True)
        self.assertEqual(self.store.load().mode, Mode.STOPPED)
        self.assertEqual(self.store.load().reason, 'user_stop')
        self.assertIsNone(self.store.load().pending)
        with self.assertRaises(CampaignError):
            reopened.resume(self.obs())

    def test_retry_permission_persistence_failure_keeps_pending_and_blocks_admission(self):
        observation = self.obs(guide=GuideKind.REVIEWED, step_key='dialogue', action_key='skip')
        self.run.prepare(observation, 'guide-1')
        before = self.store.path.read_bytes()
        with patch.object(self.store, 'save', side_effect=OSError('disk')):
            with self.assertRaises(OSError):
                self.run.reject_intent('guide-1', 'not_invoked', retry_same_revision=True)
        self.assertEqual(self.store.path.read_bytes(), before)
        with self.assertRaises(CampaignError):
            self.run.resume(observation)
        reopened = Campaign.open(self.store)
        self.assertEqual(reopened.state.pending.intent_id, 'guide-1')
        with self.assertRaises(CampaignError):
            reopened.prepare(observation, 'guide-2')

    def test_unknown_and_transport_failure_are_not_losses(self):
        self.challenge(1)
        self.assertFalse(self.run.record_result(self.result(1, Outcome.UNKNOWN)))
        self.run.mark_unknown('timeout')
        self.assertEqual(self.run.state.losses, 0)
        self.assertEqual(self.run.state.pending.attempt_id, 'attempt-1')
        with self.assertRaises(CampaignError):
            self.run.resume(self.obs())

    def test_pause_and_stop_keep_pending_and_accept_terminal_result(self):
        self.challenge(1)
        self.run.pause()
        self.assertTrue(self.run.record_result(self.result(1, Outcome.LOSS)))
        self.assertEqual(self.run.state.mode, Mode.PAUSED)
        self.run.resume(self.obs())
        self.challenge(2)
        self.run.stop()
        self.assertIsNotNone(self.run.state.pending)
        self.assertTrue(self.run.record_result(self.result(2, Outcome.WIN)))
        self.assertEqual(self.run.state.mode, Mode.STOPPED)

    def test_restart_keeps_pending_and_never_reopens_admission(self):
        self.challenge(1)
        reopened = Campaign.open(self.store)
        self.assertEqual(reopened.state.mode, Mode.PAUSED)
        self.assertEqual(reopened.state.pending.attempt_id, 'attempt-1')
        with self.assertRaises(CampaignError):
            reopened.resume(self.obs())
        self.assertTrue(reopened.record_result(self.result(1, Outcome.WIN)))
        with self.assertRaises(CampaignError):
            reopened.prepare(self.obs(), 'intent-2')
        reopened.resume(self.obs())
        self.assertEqual(reopened.prepare(self.obs(), 'intent-2').stage_key, 'observed-stage')

    def test_persist_failure_poison_prevents_new_admission(self):
        with patch.object(self.store, 'save', side_effect=OSError('disk')):
            with self.assertRaises(OSError):
                self.run.prepare(self.obs(), 'intent-1')
        self.assertIsNone(self.run.state.pending)
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'intent-2')
        self.assertIsNone(self.store.load().pending)

    def test_third_loss_must_persist_before_any_further_admission(self):
        for n in (1, 2):
            self.challenge(n)
            self.run.record_result(self.result(n, Outcome.LOSS))
        self.challenge(3)
        with patch.object(self.store, 'save', side_effect=OSError('disk')):
            with self.assertRaises(OSError):
                self.run.record_result(self.result(3, Outcome.LOSS))
        self.assertEqual(self.store.load().losses, 2)
        self.assertEqual(self.store.load().pending.attempt_id, 'attempt-3')
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'intent-4')
        recovered = Campaign.open(self.store)
        with self.assertRaises(CampaignError):
            recovered.resume(self.obs())
        recovered.record_result(self.result(3, Outcome.LOSS))
        self.assertEqual(self.store.load().mode, Mode.LOSS_LIMIT)
        with self.assertRaises(CampaignError):
            recovered.prepare(self.obs(), 'intent-4')

    def test_result_persistence_failure_does_not_open_admission(self):
        self.challenge(1)
        with patch.object(self.store, 'save', side_effect=OSError('disk')):
            with self.assertRaises(OSError):
                self.run.record_result(self.result(1, Outcome.LOSS))
        self.assertEqual(self.run.state.losses, 0)
        self.assertIsNotNone(self.store.load().pending)
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(), 'intent-2')

    def test_hint_and_reviewed_guide_and_unknown(self):
        hint = self.run.prepare(self.obs(guide=GuideKind.HINT), 'hint-stage')
        self.assertEqual(hint.kind, 'challenge')
        self.run.mark_submitted(hint.intent_id, 'attempt-1')
        self.run.record_result(self.result(1, Outcome.WIN))
        guide = self.run.prepare(self.obs(guide=GuideKind.REVIEWED, step_key='dialogue-1', action_key='advance'), 'guide')
        self.assertEqual(guide.kind, 'reviewed_guide')
        with self.assertRaises(CampaignError):
            self.run.guide_completed('guide', self.obs(guide=GuideKind.REVIEWED, step_key='dialogue-1', action_key='advance'))
        self.run.guide_completed('guide', self.obs())
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(guide=GuideKind.UNKNOWN, step_key='unknown'), 'unknown')
        self.assertEqual(self.run.state.mode, Mode.PAUSED)
        self.assertEqual(self.store.load().guide_step, 'unknown')

    def test_unknown_guide_requires_fresh_observation_to_resume(self):
        unknown = self.obs(guide=GuideKind.UNKNOWN, step_key='unknown')
        with self.assertRaises(CampaignError):
            self.run.prepare(unknown, 'unknown')
        same_revision = Observation('run-1', 'identity/session/generation', unknown.revision, ('stage',))
        with self.assertRaises(CampaignError):
            self.run.resume(same_revision)
        self.run.resume(self.obs())
        self.assertEqual(self.run.state.mode, Mode.ACTIVE)

    def test_reviewed_step_not_repeated_after_confirmation(self):
        self.run.prepare(self.obs(guide=GuideKind.REVIEWED, step_key='dialogue-1', action_key='advance'), 'guide')
        self.run.guide_completed('guide', self.obs())
        with self.assertRaises(CampaignError):
            self.run.prepare(self.obs(guide=GuideKind.REVIEWED, step_key='dialogue-1', action_key='advance'), 'repeat')
        self.assertEqual(self.run.state.mode, Mode.BLOCKED)

    def test_next_reviewed_step_can_use_its_stable_post_transition_revision(self):
        first = self.obs(guide=GuideKind.REVIEWED, step_key='general-deploy', action_key='general_deploy')
        self.run.prepare(first, 'deploy')
        confirm = self.obs(guide=GuideKind.REVIEWED, step_key='general-confirm', action_key='formation_confirm')
        self.run.guide_completed('deploy', confirm)
        following = self.run.prepare(confirm, 'confirm')
        self.assertEqual((following.action_key, following.revision), ('formation_confirm', confirm.revision))
        self.assertEqual(self.run.state.completed_guides, ('general-deploy',))

    def test_known_guide_post_state_can_begin_battle_without_artificial_revision(self):
        first = self.obs(guide=GuideKind.REVIEWED, step_key='general-confirm', action_key='formation_confirm')
        self.run.prepare(first, 'confirm')
        post = self.obs()
        self.run.guide_completed('confirm', post)
        self.assertEqual(self.run.prepare(post, 'battle').kind, 'challenge')

    def test_latest_observation_chooses_stage_and_rejects_stale(self):
        first = self.obs()
        self.run.prepare(first, 'intent-1')
        self.run.mark_submitted('intent-1', 'attempt-1')
        self.run.record_result(self.result(1, Outcome.WIN))
        with self.assertRaises(CampaignError):
            self.run.prepare(first, 'intent-2')
        next_obs = Observation('run-1', 'identity/session/generation', self.revision + 1, ('unrelated-key',))
        self.assertEqual(self.run.prepare(next_obs, 'intent-2').stage_key, 'unrelated-key')

    def test_store_strict_and_existing_run_never_overwritten(self):
        with self.assertRaises(FileExistsError):
            Campaign.create(self.store, 'other', 'other')
        data = json.loads(self.store.path.read_text(encoding='utf-8'))
        for update in ({'version': 1}, {'version': 2}, {'losses': 2}, {'unexpected': True}, {'losses': True}):
            corrupt = dict(data, **update)
            self.store.path.write_text(json.dumps(corrupt), encoding='utf-8')
            with self.assertRaises(CheckpointError):
                self.store.load()
        self.store.path.write_text('{"version":1,"version":1}', encoding='utf-8')
        with self.assertRaises(CheckpointError):
            self.store.load()

    def test_store_rejects_history_that_continues_after_loss_limit(self):
        data = json.loads(self.store.path.read_text(encoding='utf-8'))
        data['consumed'] = [dict(attempt_id=f'a-{i}', report_id=f'r-{i}', outcome=outcome)
                            for i, outcome in enumerate(('loss', 'loss', 'loss', 'win'))]
        data['losses'] = 0
        data['mode'] = 'active'
        self.store.path.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaises(CheckpointError):
            self.store.load()

    def test_atomic_replace_failure_preserves_old_file(self):
        old = self.store.path.read_bytes()
        with patch('jiangshen_assistant.campaign_store.os.replace', side_effect=OSError('replace')):
            with self.assertRaises(OSError):
                self.run.prepare(self.obs(), 'intent-1')
        self.assertEqual(self.store.path.read_bytes(), old)
        self.assertEqual(list(self.store.path.parent.glob('*.tmp')), [])


if __name__ == '__main__':
    unittest.main()
