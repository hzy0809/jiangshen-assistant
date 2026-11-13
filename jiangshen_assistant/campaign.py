from __future__ import annotations

from dataclasses import dataclass, replace
from enum import Enum
from typing import Protocol


class Mode(str, Enum):
    PAUSED = 'paused'
    ACTIVE = 'active'
    BLOCKED = 'blocked'
    STOPPED = 'stopped'
    LOSS_LIMIT = 'loss_limit'


class Outcome(str, Enum):
    WIN = 'win'
    LOSS = 'loss'
    UNKNOWN = 'unknown'


class GuideKind(str, Enum):
    NONE = 'none'
    HINT = 'hint'
    REVIEWED = 'reviewed'
    UNKNOWN = 'unknown'


OBSERVED_LOSS_EVIDENCE = 'anchored_current_battle_result_loss'
APPLIED_LOSS_EVIDENCE = 'applied_owned_campaign_loss_receipt'


def valid_settlement_evidence(outcome: str, evidence: str, *, challenge: bool = False) -> bool:
    """Accept applied settlement evidence; a UI-only defeat stays unconfirmed."""
    if outcome not in ('WIN', 'LOSS'):
        return False
    return (evidence in ('captured_authoritative_receipt_applied',
                         'captured_queued_authoritative_receipt_applied') or
            outcome == 'WIN' and evidence == ('anchored_normal_challenge_progress_applied' if challenge else 'anchored_mainline_progress_applied') or
            outcome == 'LOSS' and evidence == APPLIED_LOSS_EVIDENCE)


def result_record_key(attempt: dict) -> str:
    """Keep reportless results distinct by their original attempt identity."""
    if (attempt.get('outcome') == 'LOSS' and attempt.get('report_id') == '0' and
            attempt.get('evidence') == APPLIED_LOSS_EVIDENCE):
        return 'result:' + str(attempt.get('attempt_id', ''))
    return attempt.get('report_id', '')


@dataclass(frozen=True)
class Observation:
    run_id: str
    context_key: str
    revision: int
    eligible_stages: tuple[str, ...] = ()
    guide: GuideKind = GuideKind.NONE
    step_key: str = ''
    action_key: str = ''


@dataclass(frozen=True)
class Intent:
    intent_id: str
    kind: str  # challenge or reviewed_guide
    revision: int
    stage_key: str = ''
    step_key: str = ''
    action_key: str = ''
    attempt_id: str = ''
    uncertain: bool = False


@dataclass(frozen=True)
class Result:
    run_id: str
    context_key: str
    attempt_id: str
    report_id: str
    outcome: Outcome
    confirmed: bool


@dataclass(frozen=True)
class ConsumedResult:
    attempt_id: str
    report_id: str
    outcome: Outcome


@dataclass(frozen=True)
class Checkpoint:
    run_id: str
    context_key: str
    losses: int = 0
    mode: Mode = Mode.PAUSED
    reason: str = 'explicit_start_required'
    pending: Intent | None = None
    consumed: tuple[ConsumedResult, ...] = ()
    last_revision: int = -1
    guide_step: str = ''
    completed_guides: tuple[str, ...] = ()
    loss_mode: str = 'consecutive'


class Store(Protocol):
    def initialize(self, checkpoint: Checkpoint) -> None: ...
    def save(self, checkpoint: Checkpoint) -> None: ...


class CampaignError(RuntimeError):
    pass


class Campaign:
    """One explicit run owns its current intents, receipts and loss streak.

    A returned Intent is permission to submit it once, not evidence of submission.
    An uncertain intent stops this run; a later task observes the game afresh.
    The coordinator must serialize this admission with its stop/submit gate.
    """

    def __init__(self, store: Store, state: Checkpoint, *, on_record_warning=None):
        self._store = store
        self._state = state
        self._record_failed = False
        self._on_record_warning = on_record_warning

    @classmethod
    def create(cls, store: Store, run_id: str, context_key: str, *, loss_mode='consecutive', on_record_warning=None) -> Campaign:
        if not run_id or not context_key:
            raise ValueError('run_id and context_key are required')
        if loss_mode not in ('consecutive', 'cumulative'):
            raise ValueError('invalid loss mode')
        state = Checkpoint(run_id=run_id, context_key=context_key, loss_mode=loss_mode)
        campaign = cls(store, state, on_record_warning=on_record_warning)
        try:
            store.initialize(state)
        except Exception as exc:
            campaign._record_failure(exc)
        return campaign

    @property
    def state(self) -> Checkpoint:
        return self._state

    def _save(self, state: Checkpoint) -> None:
        # Current-run decisions live in memory. Disk output cannot admit or
        # reject an action, discard an intent, or roll back a result.
        self._state = state
        if self._record_failed:
            return
        try:
            self._store.save(state)
        except Exception as exc:
            self._record_failure(exc)

    def _record_failure(self, exc: Exception) -> None:
        self._record_failed = True
        if self._on_record_warning:
            try:
                self._on_record_warning('征战记录无法写入；本轮继续按当前游戏状态执行：' + str(exc))
            except Exception:
                pass

    def resume(self, observation: Observation) -> None:
        self._observation(observation)
        s = self._state
        if s.mode != Mode.PAUSED or s.pending is not None:
            raise CampaignError('cannot resume with pending or blocked/stopped run')
        if observation.revision <= s.last_revision:
            raise CampaignError('fresh observation required')
        if observation.guide == GuideKind.UNKNOWN:
            self._save(replace(s, mode=Mode.PAUSED, reason='unknown_guide', guide_step=observation.step_key, last_revision=observation.revision))
            raise CampaignError('unknown guide')
        self._save(replace(s, mode=Mode.ACTIVE, reason='', guide_step=observation.step_key))

    def pause(self, reason: str = 'user_pause') -> None:
        if self._state.mode in (Mode.STOPPED, Mode.LOSS_LIMIT, Mode.BLOCKED):
            return
        self._save(replace(self._state, mode=Mode.PAUSED, reason=reason))

    def stop(self, reason: str = 'user_stop') -> None:
        if self._state.mode == Mode.LOSS_LIMIT:
            return
        self._save(replace(self._state, mode=Mode.STOPPED, reason=reason or 'user_stop'))

    def _observation(self, observation: Observation) -> None:
        if (observation.run_id, observation.context_key) != (self._state.run_id, self._state.context_key):
            raise CampaignError('observation belongs to another run or context')
        if type(observation.revision) is not int or observation.revision < 0:
            raise CampaignError('invalid revision')
        if not isinstance(observation.guide, GuideKind):
            raise CampaignError('invalid guide classification')

    def prepare(self, observation: Observation, intent_id: str) -> Intent:
        self._observation(observation)
        s = self._state
        if s.mode != Mode.ACTIVE or s.pending is not None or s.losses >= 3:
            raise CampaignError('admission closed or work pending')
        if not intent_id or observation.revision <= s.last_revision:
            raise CampaignError('intent id and fresh observation required')
        if observation.guide == GuideKind.UNKNOWN:
            self._save(replace(s, mode=Mode.PAUSED, reason='unknown_guide', guide_step=observation.step_key, last_revision=observation.revision))
            raise CampaignError('unknown guide')
        if observation.guide == GuideKind.REVIEWED:
            if observation.step_key in s.completed_guides:
                self._save(replace(s, mode=Mode.BLOCKED, reason='guide_already_completed'))
                raise CampaignError('guide already completed')
            if not observation.step_key or not observation.action_key:
                raise CampaignError('reviewed guide requires step and action')
            intent = Intent(intent_id, 'reviewed_guide', observation.revision,
                            step_key=observation.step_key, action_key=observation.action_key)
        else:
            if not observation.eligible_stages or not observation.eligible_stages[0]:
                self._save(replace(s, mode=Mode.PAUSED, reason='no_eligible_stage'))
                raise CampaignError('no eligible stage')
            intent = Intent(intent_id, 'challenge', observation.revision,
                            stage_key=observation.eligible_stages[0])
        self._save(replace(s, pending=intent, last_revision=observation.revision,
                           guide_step=observation.step_key))
        return intent

    def mark_submitted(self, intent_id: str, attempt_id: str) -> None:
        p = self._state.pending
        if p is None or p.intent_id != intent_id or p.kind != 'challenge' or p.attempt_id or not attempt_id:
            raise CampaignError('invalid pending challenge')
        if any(c.attempt_id == attempt_id for c in self._state.consumed):
            self._save(replace(self._state, mode=Mode.BLOCKED, reason='attempt_id_reused'))
            raise CampaignError('attempt id reused')
        self._save(replace(self._state, pending=replace(p, attempt_id=attempt_id)))

    def reject_intent(self, intent_id: str, reason: str, *, retry_same_revision: bool = False) -> None:
        """Clear an unsubmitted intent only after a proven non-invocation.

        The caller must establish a correlated ACK with REJECTED and invoked
        False, or that the owning port refused this exact intent before any
        command admission. Unknown, timeout, a cancellation request, an unrelated
        ACK, or absence of a response is not proof.
        An already submitted challenge cannot be cleared by this method.

        An exact rejection may return the unused revision reservation. This
        opt-in atomically pauses admission; only an explicit resume can reuse
        that revision. The native observation and checkpoint schema do not
        change. Terminal or blocked runs never reopen through a rejection.
        """
        if type(retry_same_revision) is not bool:
            raise ValueError('retry_same_revision must be bool')
        p = self._state.pending
        if (p is None or p.intent_id != intent_id or p.kind not in ('challenge', 'reviewed_guide')
                or p.attempt_id or p.uncertain or not reason):
            raise CampaignError('exact unsubmitted intent and rejection reason required')
        reusable = retry_same_revision and self._state.mode in (Mode.ACTIVE, Mode.PAUSED)
        if reusable and p.revision != self._state.last_revision:
            raise CampaignError('rejected revision reservation changed')
        if reusable:
            self._save(replace(self._state, pending=None, mode=Mode.PAUSED, reason=reason,
                               last_revision=p.revision - 1))
        else:
            self._save(replace(self._state, pending=None))

    def guide_completed(self, intent_id: str, observation: Observation) -> None:
        self._observation(observation)
        p = self._state.pending
        if p is None or p.kind != 'reviewed_guide' or p.intent_id != intent_id:
            raise CampaignError('invalid pending guide')
        if observation.revision <= p.revision or (observation.guide != GuideKind.NONE and observation.step_key == p.step_key):
            raise CampaignError('guide transition not confirmed')
        s = self._state
        if observation.guide == GuideKind.UNKNOWN:
            self._save(replace(s, pending=None, mode=Mode.PAUSED, reason='unknown_guide', guide_step=observation.step_key, last_revision=observation.revision, completed_guides=s.completed_guides + (p.step_key,)))
        else:
            # The post-state can be the next reviewed step's only stable
            # revision. It has not been used to submit an action yet; preserve
            # the completed intent's reservation, while completed_guides keeps
            # the old step from being replayed. Unknown steps above still
            # reserve their observation and require an explicit fresh resume.
            self._save(replace(s, pending=None, guide_step=observation.step_key, last_revision=p.revision, completed_guides=s.completed_guides + (p.step_key,)))

    def mark_unknown(self, reason: str) -> None:
        if self._state.pending is None or not reason:
            raise CampaignError('pending intent and reason required')
        mode = self._state.mode if self._state.mode in (Mode.STOPPED, Mode.LOSS_LIMIT) else Mode.BLOCKED
        if self._state.reason == 'conflicting_result':
            reason = self._state.reason
        self._save(replace(self._state, mode=mode, reason=reason,
                           pending=replace(self._state.pending, uncertain=True)))

    def record_result(self, result: Result) -> bool:
        """Return True for a newly consumed result, False for its exact duplicate."""
        s = self._state
        if (result.run_id, result.context_key) != (s.run_id, s.context_key):
            raise CampaignError('result belongs to another run or context')
        if not isinstance(result.outcome, Outcome) or type(result.confirmed) is not bool:
            raise CampaignError('invalid result')
        for consumed in s.consumed:
            if consumed.attempt_id == result.attempt_id or consumed.report_id == result.report_id:
                if (result.confirmed and consumed.attempt_id == result.attempt_id and
                    consumed.report_id == result.report_id and consumed.outcome == result.outcome):
                    return False
                mode = Mode.LOSS_LIMIT if s.losses == 3 else Mode.BLOCKED
                self._save(replace(s, mode=mode, reason='conflicting_result'))
                raise CampaignError('conflicting result')
        p = s.pending
        if p is None or p.kind != 'challenge' or not p.attempt_id or p.attempt_id != result.attempt_id:
            raise CampaignError('result does not match pending attempt')
        if not result.confirmed or result.outcome == Outcome.UNKNOWN or not result.report_id:
            self.mark_unknown('unconfirmed_result')
            return False
        losses = s.losses + 1 if result.outcome == Outcome.LOSS else (s.losses if s.loss_mode == 'cumulative' else 0)
        mode = s.mode
        reason = s.reason
        if losses >= 3:
            mode, reason = Mode.LOSS_LIMIT, ('three_cumulative_losses' if s.loss_mode == 'cumulative' else 'three_consecutive_losses')
        elif mode == Mode.BLOCKED and reason != 'conflicting_result':
            mode, reason = Mode.PAUSED, 'result_reconciled_review_required'
        record = ConsumedResult(result.attempt_id, result.report_id, result.outcome)
        self._save(replace(s, losses=losses, mode=mode, reason=reason,
                           pending=None, consumed=s.consumed + (record,)))
        return True
