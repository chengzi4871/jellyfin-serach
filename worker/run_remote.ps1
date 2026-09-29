param(
  [Parameter(Mandatory=$true)][string]$BaseUrl,
  [Parameter(Mandatory=$true)][string]$EmbeddingModel,
  [Parameter(Mandatory=$true)][string]$ApiKey,
  [string]$QdrantUrl = "http://127.0.0.1:6333",
  [int]$Dimension = 1024,
  [ValidateSet("openai_multimodal", "plain")][string]$Protocol = "openai_multimodal"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:EMBEDDING_PROVIDER = "remote"
$env:REMOTE_EMBEDDING_BASE_URL = $BaseUrl
$env:REMOTE_EMBEDDING_MODEL = $EmbeddingModel
$env:REMOTE_EMBEDDING_API_KEY = $ApiKey
$env:REMOTE_EMBEDDING_PROTOCOL = $Protocol
$env:REMOTE_EMBEDDING_DIMENSION = "$Dimension"
$env:QDRANT_URL = $QdrantUrl
& .\.venv\Scripts\python.exe -m uvicorn app:app --host 0.0.0.0 --port 8099
