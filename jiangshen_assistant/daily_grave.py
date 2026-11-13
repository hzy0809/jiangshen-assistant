"""Run ordinary grave exploration for daily activity #17."""
from .daily_trial import DailyBattleExecutor, DailyBattleResult


class DailyGraveExecutor(DailyBattleExecutor):
    task_id = 17
    checklist_mode = True
