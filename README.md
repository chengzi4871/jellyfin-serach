# Jellyfin Visual Search

面向 Jellyfin 10.11.x 的视频级多模态语义搜索插件。V1 将视频标题/文件名文本向量与 Jellyfin Trickplay 画面向量分别检索，再按 `ItemId` 聚合为视频结果并融合排序；普通 Jellyfin 搜索保持不变。

## 当前 P0 基线

- `src/visual_search_core`：Trickplay tile 坐标、均匀采样、Frame→Video 聚合、缺失模态归一化融合、指纹和退避算法。
- `worker`：FastAPI Worker API，提供 `/health`、`/embed/text`、`/embed/image`、`/embed/images` 和 Qdrant 写入/检索接口；默认使用确定性的 Mock Provider，也支持通过 `EMBEDDING_PROVIDER=qwen` 启用官方 Qwen3-VL-Embedding-2B。
- `plugin`：Jellyfin 10.11 插件骨架、自定义 REST API、状态接口和 Web 命名空间。Jellyfin 特定代码集中在 Adapter 层，`JellyfinVersion` 在根目录 `Directory.Build.props` 统一控制。
- `docker-compose.yml`：可选的快速试运行方式，不是部署前提。生产环境建议直接运行 Qdrant 服务和 Python Worker，避免为了本项目额外引入 Docker 管理负担。
- `.github/workflows/ci.yml`：Python 核心/Worker 测试和 .NET 9 插件编译，同时只打包插件自身 DLL、PDB 与 `meta.json`，避免把 Jellyfin 框架程序集复制进插件目录。

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

插件实际连接 Worker；Worker 再连接 Qdrant：

```text
Jellyfin Plugin -> Worker URL: http://笔记本局域网地址:8099
Windows Worker  -> Qdrant URL: http://长期在线设备:6333
```

插件设置页中的 Qdrant URL 当前仅作为记录值，实际连接地址以 Worker 的 `QDRANT_URL` / `-QdrantUrl` 参数为准。Worker 关闭、休眠或断网时，Qdrant 不会停止，已建立的索引也不会消失；索引任务保留 Pending/Partial 状态，待 Worker 恢复后继续处理。

### Docker（可选）

仅在希望快速启动测试环境时使用：

```bash
pip install -r worker/requirements.txt pytest httpx
pytest -q
python worker_test.py
docker compose up -d qdrant
docker compose --profile worker up --build
```

当前仓库仍处于真实环境联调阶段。Jellyfin 版本升级时应复核 `Directory.Build.props` 中的 `JellyfinVersion`、Trickplay API、权限调用和 Web 注入行为。

## 实际部署测试教程

下面的部署分为三台逻辑组件：Jellyfin 插件运行在 Jellyfin 服务端；Qdrant 运行在长期在线的 Ubuntu 设备上；Embedding Worker 运行在 Windows RTX 2070 笔记本上。插件只保存地址和任务状态，不保存 Worker 的模型或图片。

### 1. 在 Ubuntu 上启动 Qdrant

在 Ubuntu 上新建目录并运行：

```bash
mkdir -p ~/jellyfin-visual-search
cd ~/jellyfin-visual-search
cp deploy/qdrant-docker-compose.yml .
mkdir -p qdrant_storage
docker compose -f qdrant-docker-compose.yml up -d
curl http://127.0.0.1:6333/healthz
```

如果 Qdrant 与 Jellyfin 在同一台 Ubuntu 主机上，Windows Worker 仍应填写 Ubuntu 主机对 Windows 可达的局域网地址，而不是 Worker 自己的 `127.0.0.1`。

### 2. 在 Windows 笔记本上安装 Qwen Worker

要求：Windows 10/11、NVIDIA 驱动、CUDA 可用、Python 3.12、Git。进入项目的 `worker` 目录，在 PowerShell 中执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd worker
.\setup_windows.ps1
```

脚本会创建 `.venv`、安装 Worker 和 Qwen 依赖、克隆官方 `Qwen3-VL-Embedding` 推理代码，并下载 `Qwen/Qwen3-VL-Embedding-2B` 模型。默认输出维度设置为 `1024`，与 Qdrant Collection 维度一致。

启动 Worker：

```powershell
.\run_windows.ps1 -QdrantUrl http://192.168.1.20:6333
```

验证：

```powershell
Invoke-RestMethod http://127.0.0.1:8099/health
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8099/embed/text -ContentType 'application/json' -Body '{"text":"海边玩水"}'
```

健康检查应返回 `status=ready`、模型名称、`dimension=1024` 和 CUDA 设备信息。第一次加载模型较慢，后续请求会复用模型。Worker 关闭时，插件应显示计算节点离线，Qdrant 中已有的向量不会丢失。

如果暂时只想测试链路而不下载模型，可以在 PowerShell 中运行 Mock 模式：

```powershell
$env:EMBEDDING_PROVIDER = "mock"
$env:QDRANT_URL = "http://192.168.1.20:6333"
& .\.venv\Scripts\python.exe -m uvicorn app:app --host 0.0.0.0 --port 8099
```

### 3. 编译和安装 Jellyfin 插件

GitHub Actions 成功后，在仓库的 Actions → CI → Artifacts 下载 `visual-search-plugin`。浏览器下载得到的 ZIP **就是最终 Artifact**，不再包含第二层插件 ZIP；解压一次后应看到：

```text
VisualSearch_0.1.0.1/
├── Jellyfin.Plugin.VisualSearch.dll
├── Jellyfin.Plugin.VisualSearch.pdb
└── meta.json
```

Jellyfin 的插件目录位于 **Jellyfin 数据目录下的 `plugins`**，不能把 Docker 宿主机挂载根目录机械地当成插件目录。可先在容器中定位实际目录：

```bash
docker exec jellyfin sh -lc 'find /config -maxdepth 4 -type d -name plugins -print'
```

当前已验证的这套部署，宿主机实际目录是 `~/docker/jellyfin/config/data/plugins/`。升级本插件时建议：

```bash
docker stop jellyfin
rm -rf ~/docker/jellyfin/config/data/plugins/VisualSearch_0.1.0.0
unzip visual-search-plugin.zip -d ~/docker/jellyfin/config/data/plugins/
docker start jellyfin
```

重启后日志应出现类似：

```text
Loaded plugin: "Visual Search" "0.1.0.1"
```

**不要**把 `MediaBrowser.*`、`Jellyfin.Data`、`Microsoft.Extensions.*`、`EntityFrameworkCore.*` 等 Jellyfin/ASP.NET 框架 DLL 一起放进插件目录。它们会进入插件自己的 AssemblyLoadContext，可能导致插件实现的 `IPlugin` 与 Jellyfin 主程序中的 `IPlugin` 类型身份不一致，表现为“Loaded assembly”但插件实例没有创建。官方 Jellyfin 插件模板也要求对 Jellyfin 包设置 `ExcludeAssets=runtime`。

### 4. 配置插件

打开 Dashboard → Plugins → Visual Search。0.1.0.1 起，配置页按 Jellyfin 标准 `pluginConfigurationPage` 结构加载；如果仍看到空白页，先确认日志显示加载的是 `0.1.0.1`，并强制刷新浏览器缓存。

填写：

```text
Worker URL:       http://Windows笔记本局域网地址:8099
Qdrant URL:       http://Ubuntu局域网地址:6333  # 当前仅记录；实际在 Worker 启动参数中配置
Frames per video: 12
Visual weight:    0.75
Title weight:     0.25
```

先点击“测试 Worker”，再初始化 Qdrant，然后只选择少量视频验证完整链路。管理员索引接口为 `POST /VisualSearch/Index/Item`，请求体示例：

```json
{"itemId":"Jellyfin视频ID","libraryId":"媒体库ID"}
```

插件从 Jellyfin 的 Trickplay manifest 读取 `Interval`、`ThumbnailCount`、`TileWidth`、`TileHeight` 和 `Width`，按实际 tile 布局采样，不重新解码原视频。

### 5. JS 注入兼容性

插件使用独立的 `window.JellyfinVisualSearch` 命名空间，并通过 `/VisualSearch/*` REST 路由工作，不覆盖 Jellyfin 原有搜索 API。现有 JS 注入可以继续保留；后续只需要在现有搜索界面增加一个按钮并调用：

```javascript
window.JellyfinVisualSearch.search('卧室，两个人', [], 30)
```

如果注入脚本修改了搜索页 DOM，插件不会强行替换它；语义搜索入口和结果页会单独实现。每次注入脚本初始化都应保持幂等，避免 Jellyfin SPA 路由切换时重复添加按钮。

### 6. 故障定位

```bash
# Ubuntu：Qdrant
curl http://Ubuntu地址:6333/healthz

# Windows：Worker
curl http://Windows地址:8099/health

# Jellyfin：插件 API，需带当前登录会话
curl http://Jellyfin地址/VisualSearch/Health
```

出现问题时按顺序判断：Jellyfin 是否能访问 Worker、Worker 是否能访问 Qdrant、Worker 模型是否成功加载、Qdrant Collection 维度是否为 `1024`、插件当前用户是否有对应媒体库权限。不要把 Jellyfin Token 放入 Worker 配置或日志。
