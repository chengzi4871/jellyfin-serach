from __future__ import annotations

from collections import defaultdict
from typing import Iterable, Literal

from .models import BestFrame, VideoSearchScore


def aggregate_frame_scores(
    frame_scores: Iterable[tuple[str, int, int, float]],
    mode: Literal["MAX", "TOP_K_MEAN", "HYBRID"] = "HYBRID",
    top_k: int = 3,
) -> dict[str, VideoSearchScore]:
    """Aggregate frame-level hits into one score per video item."""
    grouped: dict[str, list[tuple[int, int, float]]] = defaultdict(list)
    for item_id, frame_index, timestamp_ms, score in frame_scores:
        grouped[item_id].append((frame_index, timestamp_ms, float(score)))
    result: dict[str, VideoSearchScore] = {}
    for item_id, values in grouped.items():
        values.sort(key=lambda x: x[2], reverse=True)
        best = values[0]
        mean = sum(x[2] for x in values[: max(1, top_k)]) / min(len(values), max(1, top_k))
        if mode == "MAX":
            visual = best[2]
        elif mode == "TOP_K_MEAN":
            visual = mean
        elif mode == "HYBRID":
            visual = 0.7 * best[2] + 0.3 * mean
        else:
            raise ValueError(f"unknown aggregation mode: {mode}")
        result[item_id] = VideoSearchScore(
            item_id=item_id,
            score=visual,
            visual_score=visual,
            title_score=None,
            best_frame=BestFrame(best[0], best[1], best[2]),
        )
    return result


def fuse_scores(
    visual: float | None,
    title: float | None,
    visual_weight: float = 0.75,
    title_weight: float = 0.25,
) -> float:
    """Fuse available modalities and renormalize when one is missing."""
    if visual is None and title is None:
        raise ValueError("at least one modality score is required")
    present = [(visual, visual_weight), (title, title_weight)]
    available = [(score, weight) for score, weight in present if score is not None]
    total_weight = sum(weight for _, weight in available)
    return sum(score * weight for score, weight in available) / total_weight
