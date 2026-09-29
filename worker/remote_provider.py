from __future__ import annotations

import base64
import os
import time
from typing import Any

import httpx

from provider import EmbeddingInfo, EmbeddingProvider


class RemoteEmbeddingProvider:
    """OpenAI-compatible multimodal embedding gateway.

    The endpoint must accept text and image inputs in the same vector space.
    The default request shape uses ``input`` with a multimodal content item.
    """

    def __init__(self) -> None:
        base_url = os.getenv("REMOTE_EMBEDDING_BASE_URL", "").strip().rstrip("/")
        direct_url = os.getenv("REMOTE_EMBEDDING_URL", "").strip()
        self.url = direct_url or (base_url + "/embeddings" if base_url else "")
        if not self.url:
            raise RuntimeError("REMOTE_EMBEDDING_URL is required for remote provider")
        self.api_key = os.getenv("REMOTE_EMBEDDING_API_KEY", "").strip()
        self.model = os.getenv("REMOTE_EMBEDDING_MODEL", "").strip()
        if not self.model:
            raise RuntimeError("REMOTE_EMBEDDING_MODEL is required for remote provider")
        self.protocol = os.getenv("REMOTE_EMBEDDING_PROTOCOL", "openai_multimodal").lower()
        self.timeout = float(os.getenv("REMOTE_EMBEDDING_TIMEOUT", "30"))
        self.retries = max(0, int(os.getenv("REMOTE_EMBEDDING_RETRIES", "2")))
        self.info = EmbeddingInfo(
            "remote",
            os.getenv("REMOTE_EMBEDDING_MODEL_VERSION", self.model),
            int(os.getenv("REMOTE_EMBEDDING_DIMENSION", "1024")),
            "cloud",
        )

    def _payload(self, value: str, kind: str) -> dict[str, Any]:
        if self.protocol in {"openai", "openai_multimodal"}:
            item = {"type": "text", "text": value} if kind == "text" else {
                "type": "image_url", "image_url": {"url": value}
            }
            return {"model": self.model, "input": [item]}
        if self.protocol == "plain":
            return {"model": self.model, "input": value}
        raise RuntimeError(f"unsupported REMOTE_EMBEDDING_PROTOCOL: {self.protocol}")

    @staticmethod
    def _read_vector(body: dict[str, Any]) -> list[float]:
        data = body.get("data")
        if isinstance(data, list) and data and isinstance(data[0], dict):
            vector = data[0].get("embedding")
            if isinstance(vector, list):
                return [float(x) for x in vector]
        output = body.get("output")
        if isinstance(output, dict):
            embeddings = output.get("embeddings")
            if isinstance(embeddings, list) and embeddings:
                first = embeddings[0]
                if isinstance(first, dict):
                    first = first.get("embedding", first.get("vector"))
                if isinstance(first, list):
                    return [float(x) for x in first]
        vector = body.get("embedding", body.get("vector"))
        if isinstance(vector, list):
            return [float(x) for x in vector]
        raise RuntimeError("remote embedding response does not contain a vector")

    def _request(self, value: str, kind: str) -> list[float]:
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        last_error: Exception | None = None
        for attempt in range(self.retries + 1):
            try:
                with httpx.Client(timeout=self.timeout) as client:
                    response = client.post(self.url, headers=headers, json=self._payload(value, kind))
                    response.raise_for_status()
                    vector = self._read_vector(response.json())
                if len(vector) != self.info.dimension:
                    raise RuntimeError(
                        f"remote dimension {len(vector)} does not match configured {self.info.dimension}"
                    )
                return vector
            except Exception as exc:
                last_error = exc
                if attempt < self.retries:
                    time.sleep(min(2 ** attempt, 8))
        raise RuntimeError(f"remote embedding failed after {self.retries + 1} attempts: {last_error}") from last_error

    def embed_text(self, text: str) -> list[float]:
        return self._request(text, "text")

    def embed_image(self, image_bytes: bytes) -> list[float]:
        encoded = base64.b64encode(image_bytes).decode("ascii")
        return self._request(f"data:image/jpeg;base64,{encoded}", "image")
