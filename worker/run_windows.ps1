param(
  [string]$QdrantUrl = "http://127.0.0.1:6333",
  [string]$ModelPath = "$PSScriptRoot\models\Qwen3-VL-Embedding-2B",
  [string]$QwenRepo = "$PSScriptRoot\Qwen3-VL-Embedding"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:EMBEDDING_PROVIDER = "qwen"
$env:QDRANT_URL = $QdrantUrl
$env:QWEN_MODEL_PATH = $ModelPath
$env:QWEN_REPO = $QwenRepo
$env:EMBEDDING_DIMENSION = "1024"
& .\.venv\Scripts\python.exe -m uvicorn app:app --host 0.0.0.0 --port 8099
