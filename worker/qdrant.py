from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import httpx


TEXT_COLLECTION = "jellyfin_video_text"
FRAME_COLLECTION = "jellyfin_video_frames"


@dataclass(frozen=True)
class QdrantConfig:
    url: str = "http://127.0.0.1:6333"
    timeout_seconds: float = 3.0
    dimension: int = 1024


class QdrantRepository:
    def __init__(self, config: QdrantConfig):
        self.config = config

    async def _request(self, method: str, path: str, **kwargs: Any) -> Any:
        async with httpx.AsyncClient(base_url=self.config.url.rstrip("/"), timeout=self.config.timeout_seconds) as client:
            response = await client.request(method, path, **kwargs)
            response.raise_for_status()
            return response.json() if response.content else None

    async def health(self) -> bool:
        try:
            await self._request("GET", "/healthz")
            return True
        except (httpx.HTTPError, OSError):
            return False

    async def ensure_collections(self) -> None:
        body = {"vectors": {"size": self.config.dimension, "distance": "Cosine"}}
        for name in (TEXT_COLLECTION, FRAME_COLLECTION):
            async with httpx.AsyncClient(base_url=self.config.url.rstrip("/"), timeout=self.config.timeout_seconds) as client:
                response = await client.put(f"/collections/{name}", json=body)
                if response.status_code not in (200, 201):
                    response.raise_for_status()

    async def upsert(self, collection: str, points: list[dict[str, Any]]) -> Any:
        return await self._request("PUT", f"/collections/{collection}/points", json={"points": points, "wait": True})

    async def search(self, collection: str, vector: list[float], limit: int = 100,
                     filter_body: dict[str, Any] | None = None) -> list[dict[str, Any]]:
        body: dict[str, Any] = {"vector": vector, "limit": limit, "with_payload": True}
        if filter_body:
            body["filter"] = filter_body
        data = await self._request("POST", f"/collections/{collection}/points/search", json=body)
        return data.get("result", [])

    async def delete_item(self, collection: str, item_id: str) -> Any:
        return await self._request("POST", f"/collections/{collection}/points/delete", json={
            "filter": {"must": [{"key": "itemId", "match": {"value": item_id}}]}, "wait": True
        })
