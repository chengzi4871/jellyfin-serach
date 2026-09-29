from __future__ import annotations

import io
import os
import sys
from typing import Any

import torch
from PIL import Image

from provider import EmbeddingInfo


class QwenEmbeddingProvider:
    """Adapter for the official Qwen3-VL-Embedding repository.

    The repository is installed separately on the Windows Worker machine, so
    the base Worker package remains lightweight and Mock mode remains usable.
    """

    def __init__(self) -> None:
        repo = os.getenv("QWEN_REPO", "")
        model_path = os.getenv("QWEN_MODEL_PATH", "Qwen/Qwen3-VL-Embedding-2B")
        if repo and repo not in sys.path:
            sys.path.insert(0, repo)
        from src.models.qwen3_vl_embedding import Qwen3VLEmbedder  # type: ignore

        self.dimension = int(os.getenv("EMBEDDING_DIMENSION", "1024"))
        self.model = Qwen3VLEmbedder(model_name_or_path=model_path)
        self.info = EmbeddingInfo("Qwen3-VL-Embedding-2B", "official", self.dimension, "cuda" if torch.cuda.is_available() else "cpu")

    def _process(self, payload: dict[str, Any]) -> list[float]:
        with torch.inference_mode():
            vector = self.model.process([payload])[0]
        vector = vector[: self.dimension]
        vector = vector / torch.linalg.vector_norm(vector)
        return vector.detach().float().cpu().tolist()

    def embed_text(self, text: str) -> list[float]:
        return self._process({"text": text, "instruction": "Retrieve videos relevant to the user's query."})

    def embed_image(self, image_bytes: bytes) -> list[float]:
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        return self._process({"image": image})
