from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol, Sequence


@dataclass(frozen=True)
class EmbeddingInfo:
    model: str
    model_version: str
    dimension: int
    device: str


class EmbeddingProvider(Protocol):
    info: EmbeddingInfo

    def embed_text(self, text: str) -> list[float]: ...
    def embed_image(self, image_bytes: bytes) -> list[float]: ...


class MockEmbeddingProvider:
    """Deterministic provider for API, queue and integration tests."""

    def __init__(self, dimension: int = 1024):
        self.info = EmbeddingInfo("mock", "1", dimension, "cpu")

    def _embed(self, payload: bytes) -> list[float]:
        import hashlib
        digest = hashlib.sha256(payload).digest()
        return [((digest[i % len(digest)] / 255.0) * 2.0 - 1.0) for i in range(self.info.dimension)]

    def embed_text(self, text: str) -> list[float]:
        return self._embed(text.encode("utf-8"))

    def embed_image(self, image_bytes: bytes) -> list[float]:
        return self._embed(image_bytes)
