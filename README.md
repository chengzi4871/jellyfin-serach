# Jellyfin Visual Search

面向 Jellyfin 10.11.x 的视频级多模态语义搜索插件。V1 将视频标题/文件名文本向量与 Jellyfin Trickplay 画面向量分别检索，再按 `ItemId` 聚合为视频结果并融合排序；普通 Jellyfin 搜索保持不变。

## 当前 P0 基线

- `src/visual_search_core`：Trickplay tile 坐标、均匀采样、Frame→Video 聚合、缺失模态归一化融合、指纹和退避算法。
- `worker`：FastAPI Worker API，提供 `/health`、`/embed/text`、`/embed/image`、`/embed/images` 和 Qdrant 写入/检索接口；支持 `mock`、本地 `qwen`，以及不需要 GPU 的 `remote` OpenAI-compatible 多模态 Provider。
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

### 2A. 改用云端 Embedding（推荐先用小批量验证）

云端模式仍运行一个很轻量的 Worker，但 Worker 不加载本地模型；它只负责把文本/Trickplay 图片转发到你指定的云端多模态 Embedding API，并把向量写入 Qdrant。RTX 2070 可以完全关闭。云端接口必须同时接受文本和图片，并且返回同一个向量空间；只有文本的普通 /v1/embeddings 不能用于本项目的视觉检索。

在 Windows Worker 目录先安装基础依赖（不安装 Torch/Qwen）：

```powershell
py -3.12 -m venv .venv
& .\\.venv\\Scripts\\python.exe -m pip install -r requirements.txt
```

然后用自定义 Base URL、模型名、API Key 启动（API Key 只存在当前 Worker 进程环境变量，不写入 Jellyfin，也不返回到 /health）：

```powershell
.\\run_remote.ps1 `
  -BaseUrl "https://你的供应商.example/v1" `
  -EmbeddingModel "你的多模态嵌入模型" `
  -ApiKey "你的API_KEY" `
  -QdrantUrl "http://192.168.1.20:6333" `
  -Dimension 1024
```

默认请求为 OpenAI-compatible multimodal 格式：文本使用 input: [{type: "text", text: "..."}]，图片使用 input: [{type: "image_url", image_url: {url: "data:image/jpeg;base64,..."}}]；响应支持标准 data[0].embedding，并兼容常见的 output.embeddings/vector 返回。若供应商只接受纯字符串 input，可加 -Protocol plain，但该模式只有在供应商明确支持图片且仍保证文本/图片同空间时才可用。

先访问 Invoke-RestMethod http://127.0.0.1:8099/health，确认返回 provider=remote、模型名和正确 dimension。然后在 Jellyfin 插件设置中点击“小批量安全测试”：它随机抽取 1～10 个视频，只调用文本和少量 Trickplay 图片嵌入，不写入正式 Qdrant 集合；返回每个视频是否成功、测试帧数和错误信息。这样可以先验证供应商是否接受你的家庭/成人媒体图片、是否返回正确维度，再进行增量或完整索引。

云端供应商拒绝成人图片、返回内容安全错误、超时或维度不一致时，小批量测试会在对应视频的 error 字段显示原因；不要直接启动整库索引。确认测试通过后，再点击“初始化 Qdrant”并执行增量索引。

如果暂时只想测试链路而不下载模型，可以在 PowerShell 中运行 Mock 模式：

```powershell
$env:EMBEDDING_PROVIDER = "mock"
$env:QDRANT_URL = "http://192.168.1.20:6333"
& .\.venv\Scripts\python.exe -m uvicorn app:app --host 0.0.0.0 --port 8099
```

### 3. 编译和安装 Jellyfin 插件

GitHub Actions 成功后，在仓库的 Actions → CI → Artifacts 下载 `visual-search-plugin`。浏览器下载得到的 ZIP **就是最终 Artifact**，不再包含第二层插件 ZIP；解压一次后应看到：

```text
VisualSearch_0.1.0.2/
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
rm -rf ~/docker/jellyfin/config/data/plugins/VisualSearch_0.1.0.*
unzip visual-search-plugin.zip -d ~/docker/jellyfin/config/data/plugins/
docker start jellyfin
```

重启后日志应出现类似：

```text
Loaded plugin: "Visual Search" "0.1.0.2"
```

**不要**把 `MediaBrowser.*`、`Jellyfin.Data`、`Microsoft.Extensions.*`、`EntityFrameworkCore.*` 等 Jellyfin/ASP.NET 框架 DLL 一起放进插件目录。它们会进入插件自己的 AssemblyLoadContext，可能导致插件实现的 `IPlugin` 与 Jellyfin 主程序中的 `IPlugin` 类型身份不一致，表现为“Loaded assembly”但插件实例没有创建。官方 Jellyfin 插件模板也要求对 Jellyfin 包设置 `ExcludeAssets=runtime`。

### 4. 配置插件

0.1.0.2 起，Visual Search 与 JS Injector 一样通过 Jellyfin 原生 `EnableInMainMenu` 注册到管理后台左侧栏，日常配置无需先进入较慢的 Plugins 列表页。重启 Jellyfin 并刷新浏览器后，在左侧“插件”区域直接点击 **Visual Search** 即可。配置页仍可通过 Dashboard → Plugins → Visual Search 进入；如果页面异常，先确认日志显示加载的是 `0.1.0.2`，再强制刷新浏览器缓存。

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
