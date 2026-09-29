param(
  [string]$ModelPath = "$PSScriptRoot\models\Qwen3-VL-Embedding-2B",
  [string]$QwenRepo = "$PSScriptRoot\Qwen3-VL-Embedding"
)

$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot
if (-not (Test-Path .venv)) { py -3.12 -m venv .venv }
& .\.venv\Scripts\python.exe -m pip install --upgrade pip
& .\.venv\Scripts\python.exe -m pip install -r requirements.txt -r requirements-qwen.txt
if (-not (Test-Path $QwenRepo)) { git clone https://github.com/QwenLM/Qwen3-VL-Embedding.git $QwenRepo }
& .\.venv\Scripts\python.exe -m pip install -r "$QwenRepo\requirements.txt" 2>$null
& .\.venv\Scripts\huggingface-cli.exe download Qwen/Qwen3-VL-Embedding-2B --local-dir $ModelPath
Write-Host "完成。接下来运行 .\run_windows.ps1 -QdrantUrl http://你的Ubuntu地址:6333"
