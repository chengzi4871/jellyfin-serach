from __future__ import annotations

import hashlib


def text_fingerprint(*, title: str, original_title: str, filename: str, model: str, model_version: str) -> str:
    value = "\n".join((title, original_title, filename, model, model_version))
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def visual_fingerprint(*, item_id: str, media_source_id: str, runtime_ticks: int, thumbnail_count: int,
                       interval_ms: int, width: int, height: int, model: str, model_version: str,
                       sampling_config_version: str) -> str:
    value = "\n".join(map(str, (item_id, media_source_id, runtime_ticks, thumbnail_count,
                                  interval_ms, width, height, model, model_version,
                                  sampling_config_version)))
    return hashlib.sha256(value.encode("utf-8")).hexdigest()
