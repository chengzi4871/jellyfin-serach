from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent / "worker"))
from fastapi.testclient import TestClient
from app import app


client = TestClient(app)
assert client.get("/health").json()["status"] == "ready"
text = client.post("/embed/text", json={"text": "海边玩水"}).json()
assert len(text["vector"]) == 1024
print("worker_test: ok")
