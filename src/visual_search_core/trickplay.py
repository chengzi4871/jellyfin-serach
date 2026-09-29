from __future__ import annotations

from .models import FrameLocation, TrickplayMetadata


def locate_frame(frame_index: int, metadata: TrickplayMetadata) -> FrameLocation:
    """Map a logical frame number to its sprite tile and timestamp."""
    if frame_index < 0 or frame_index >= metadata.thumbnail_count:
        raise ValueError("frame_index is outside thumbnail_count")
    if metadata.tile_width <= 0 or metadata.tile_height <= 0:
        raise ValueError("tile dimensions must be positive")
    capacity = metadata.tile_width * metadata.tile_height
    cell = frame_index % capacity
    return FrameLocation(
        frame_index=frame_index,
        tile_index=frame_index // capacity,
        row=cell // metadata.tile_width,
        column=cell % metadata.tile_width,
        timestamp_ms=frame_index * metadata.interval_ms,
    )


def uniform_sample_indices(thumbnail_count: int, target_count: int) -> list[int]:
    """Return deterministic, evenly distributed frame indices."""
    if thumbnail_count <= 0 or target_count <= 0:
        return []
    if target_count >= thumbnail_count:
        return list(range(thumbnail_count))
    if target_count == 1:
        return [0]
    return [round(i * (thumbnail_count - 1) / (target_count - 1)) for i in range(target_count)]
