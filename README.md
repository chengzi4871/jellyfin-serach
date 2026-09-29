# Jellyfin Visual Search

面向 Jellyfin 10.11.x 的视频级多模态语义搜索插件。V1 将视频标题/文件名文本向量与 Jellyfin Trickplay 画面向量分别检索，再按 `ItemId` 聚合为视频结果并融合排序；普通 Jellyfin 搜索保持不变。

## 当前 P0 基线

- `src/visual_search_core`：Trickplay tile 坐标、均匀采样、Frame→Video 聚合、缺失模态归一化融合、指纹和退避算法。
- `worker`：FastAPI Worker API，提供 `/health`、`/embed/text`、`/embed/image`、`/embed/images`；当前默认使用确定性的 Mock Provider，真实 Qwen3-VL Provider 后续替换，不影响 API 和索引流程。
- `plugin`：Jellyfin 10.11 插件骨架、自定义 REST API、状态接口和 Web 命名空间。Jellyfin 特定代码集中在 Adapter 层，`JellyfinVersion` 在根目录 `Directory.Build.props` 统一控制。
- `docker-compose.yml`：长期在线设备上的 Qdrant；Worker 使用 profile 启动，Worker 离线不影响 Qdrant。
- `.github/workflows/ci.yml`：Python 核心/Worker 测试和 .NET 9 插件编译。

## 本地验证

```bash
pip install -r worker/requirements.txt pytest httpx
pytest -q
python worker_test.py
docker compose up -d qdrant
docker compose --profile worker up --build
```

当前仓库只承诺完成可测试的架构基线，尚未声称已在真实 Jellyfin、真实 Trickplay、RTX 2070 或 Qwen 模型上完成集成验证。首次部署前应将 `Directory.Build.props` 的 `JellyfinVersion` 改为实际服务器的小版本，并依据该版本源码复核 Trickplay API、权限调用和 Web 注入行为。
