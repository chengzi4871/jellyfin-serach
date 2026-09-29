from __future__ import annotations


def backoff_seconds(attempt: int, schedule: tuple[int, ...] = (10, 30, 60, 300)) -> int:
    if attempt < 0:
        raise ValueError("attempt must be non-negative")
    return schedule[min(attempt, len(schedule) - 1)]
