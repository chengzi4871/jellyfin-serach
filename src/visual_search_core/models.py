from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any


class IndexState(str, Enum):
    PENDING = "Pending"
    PROCESSING = "Processing"
    INDEXED = "Indexed"
    PARTIAL = "Partial"
    FAILED = "Failed"
    SKIPPED = "Skipped"


@dataclass(frozen=True)
class TrickplayMetadata:
    interval_ms: int
    thumbnail_count: int
    tile_width: int
    tile_height: int
    width: int
    height: int


@dataclass(frozen=True)
class FrameLocation:
    frame_index: int
    tile_index: int
    row: int
    column: int
    timestamp_ms: int


@dataclass(frozen=True)
class VideoDocument:
    item_id: str
    library_id: str
    title: str = ""
    original_title: str = ""
    filename: str = ""
    media_source_id: str = ""
    runtime_ticks: int = 0
    trickplay: TrickplayMetadata | None = None

    def text(self, include_filename: bool = True) -> str:
        parts = [
            f"Title: {self.title}" if self.title else "",
            f"Original title: {self.original_title}" if self.original_title else "",
            f"Filename: {self.filename}" if include_filename and self.filename else "",
        ]
        return "\n".join(p for p in parts if p)


@dataclass(frozen=True)
class BestFrame:
    frame_index: int
    timestamp_ms: int
    score: float


@dataclass(frozen=True)
class VideoSearchScore:
    item_id: str
    score: float
    visual_score: float | None
    title_score: float | None
    best_frame: BestFrame | None = None
    metadata: dict[str, Any] = field(default_factory=dict)
