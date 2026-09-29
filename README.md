# Jellyfin Visual Search

面向 Jellyfin 10.11.x 的视频级多模态语义搜索插件。V1 将视频标题/文件名文本向量与 Jellyfin Trickplay 画面向量分别检索，再按 `ItemId` 聚合为视频结果并融合排序；普通 Jellyfin 搜索保持不变。

## 当前 P0 基线

- `src/visual_search_core`：Trickplay tile 坐标、均匀采样、Frame→Video 聚合、缺失模态归一化融合、指纹和退避算法。
- `worker`：FastAPI Worker API，提供 `/health`、`/embed/text`、`/embed/image`、`/embed/images`；当前默认使用确定性的 Mock Provider，真实 Qwen3-VL Provider 后续替换，不影响 API 和索引流程。
- `plugin`：Jellyfin 10.11 插件骨架、自定义 REST API、状态接口和 Web 命名空间。Jellyfin 特定代码集中在 Adapter 层，`JellyfinVersion` 在根目录 `Directory.Build.props` 统一控制。
- `docker-compose.yml`：可选的快速试运行方式，不是部署前提。生产环境建议直接运行 Qdrant 服务和 Python Worker，避免为了本项目额外引入 Docker 管理负担。
- `.github/workflows/ci.yml`：Python 核心/Worker 测试和 .NET 9 插件编译。

## 本地验证

### 原生运行（推荐）

Qdrant 和 Worker 是两个相互独立的进程，不要求 Docker：

```bash
# 1. 在长期在线设备上安装并运行 Qdrant 独立服务，监听 6333 端口
#    Qdrant 的数据目录应放在持久化磁盘上

# 2. 在 Worker 笔记本上创建 Python 环境
python -m venv .venv
. .venv/bin/activate                 # Windows: .venv\\Scripts\\activate
pip install -r worker/requirements.txt
uvicorn worker.app:app --host 0.0.0.0 --port 8099
```

插件只需要配置：

```text
Qdrant URL:  http://长期在线设备:6333
Worker URL:  http://笔记本局域网地址:8099
```

Worker 关闭、休眠或断网时，Qdrant 不会停止，已建立的索引也不会消失；索引任务保留 Pending/Partial 状态，待 Worker 恢复后继续处理。

### Docker（可选）

仅在希望快速启动测试环境时使用：

```bash
pip install -r worker/requirements.txt pytest httpx
pytest -q
python worker_test.py
docker compose up -d qdrant
docker compose --profile worker up --build
```

当前仓库只承诺完成可测试的架构基线，尚未声称已在真实 Jellyfin、真实 Trickplay、RTX 2070 或 Qwen 模型上完成集成验证。首次部署前应将 `Directory.Build.props` 的 `JellyfinVersion` 改为实际服务器的小版本，并依据该版本源码复核 Trickplay API、权限调用和 Web 注入行为。
