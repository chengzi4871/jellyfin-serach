from __future__ import annotations

import base64
import os
from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from provider import MockEmbeddingProvider


class TextRequest(BaseModel):
    text: str = Field(min_length=1)


class ImageRequest(BaseModel):
    image_base64: str = Field(min_length=1)


class ImagesRequest(BaseModel):
    images_base64: list[str] = Field(min_length=1)


provider = MockEmbeddingProvider(int(os.getenv("EMBEDDING_DIMENSION", "1024")))
app = FastAPI(title="Jellyfin Visual Search Embedding Worker", version="0.1.0")


@app.get("/health")
def health() -> dict[str, Any]:
    return {"status": "ready", **provider.info.__dict__}


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
