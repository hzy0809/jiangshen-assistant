"""Daily-policy adapter and compatibility exports for independent soul gameplay."""
from . import soul_executor as _gameplay
from .soul_executor import SoulExecutor


class DailySoulExecutor(SoulExecutor):
    """The daily workflow supplies its free/resource policy to the shared game."""

    def execute(self, task_id: int, *, free_only: bool = True, **kwargs):
        if type(task_id) is not int or task_id != 5:
            raise ValueError('daily_soul_task_unsupported')
        return super().execute(free_only=free_only, **kwargs)


def __getattr__(name):
    return getattr(_gameplay, name)
