from __future__ import annotations

import base64
import os
from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

try:
    from .provider import create_provider
    from .qdrant import QdrantConfig, QdrantRepository, TEXT_COLLECTION, FRAME_COLLECTION
except ImportError:  # uvicorn app:app from the worker directory
    from provider import create_provider
    from qdrant import QdrantConfig, QdrantRepository, TEXT_COLLECTION, FRAME_COLLECTION


class TextRequest(BaseModel):
    text: str = Field(min_length=1)


class ImageRequest(BaseModel):
    image_base64: str = Field(min_length=1)


class ImagesRequest(BaseModel):
    images_base64: list[str] = Field(min_length=1)


provider = create_provider()
qdrant = QdrantRepository(QdrantConfig(
    url=os.getenv("QDRANT_URL", "http://127.0.0.1:6333"),
    dimension=provider.info.dimension,
))
app = FastAPI(title="Jellyfin Visual Search Embedding Worker", version="0.1.0")


@app.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ready",
        "provider": os.getenv("EMBEDDING_PROVIDER", "mock"),
        "qdrantConfigured": bool(os.getenv("QDRANT_URL")),
        **provider.info.__dict__,
    }


@app.post("/qdrant/ensure")
async def ensure_qdrant() -> dict[str, Any]:
    await qdrant.ensure_collections()
    return {"status": "ready", "collections": [TEXT_COLLECTION, FRAME_COLLECTION]}


@app.post("/qdrant/upsert")
async def qdrant_upsert(request: dict[str, Any]) -> dict[str, Any]:
    collection = request.get("collection")
    points = request.get("points")
    if collection not in (TEXT_COLLECTION, FRAME_COLLECTION) or not isinstance(points, list):
        raise HTTPException(status_code=400, detail="collection and points are required")
    return await qdrant.upsert(collection, points)


@app.post("/qdrant/search")
async def qdrant_search(request: dict[str, Any]) -> dict[str, Any]:
    collection = request.get("collection")
    if collection not in (TEXT_COLLECTION, FRAME_COLLECTION):
        raise HTTPException(status_code=400, detail="invalid collection")
    vector = request.get("vector")
    if not isinstance(vector, list):
        raise HTTPException(status_code=400, detail="vector is required")
    result = await qdrant.search(collection, vector, int(request.get("limit", 100)), request.get("filter"))
    return {"result": result}


@app.post("/embed/text")
def embed_text(request: TextRequest) -> dict[str, Any]:
    vector = provider.embed_text(request.text)
    return {"vector": vector, **provider.info.__dict__}


@app.post("/embed/image")
def embed_image(request: ImageRequest) -> dict[str, Any]:
    try:
        payload = base64.b64decode(request.image_base64, validate=True)
    except Exception as exc:
        raise HTTPException(status_code=400, detail="image_base64 is invalid") from exc
    return {"vector": provider.embed_image(payload), **provider.info.__dict__}


@app.post("/embed/images")
def embed_images(request: ImagesRequest) -> dict[str, Any]:
    vectors = []
    for encoded in request.images_base64:
        try:
            payload = base64.b64decode(encoded, validate=True)
        except Exception as exc:
            raise HTTPException(status_code=400, detail="image_base64 is invalid") from exc
        vectors.append(provider.embed_image(payload))
    return {"vectors": vectors, **provider.info.__dict__}
