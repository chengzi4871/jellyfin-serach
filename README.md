# Jellyfin Visual Search

面向 Jellyfin 10.11.x 的视频级多模态语义搜索插件。V1 将视频标题/文件名文本向量与 Jellyfin Trickplay 画面向量分别检索，再按 `ItemId` 聚合为视频结果并融合排序；普通 Jellyfin 搜索保持不变。

## 当前 P0 基线

- `src/visual_search_core`：Trickplay tile 坐标、均匀采样、Frame→Video 聚合、缺失模态归一化融合、指纹和退避算法。
- `worker`：FastAPI Worker API，提供 `/health`、`/embed/text`、`/embed/image`、`/embed/images` 和 Qdrant 写入/检索接口；支持 `mock`、本地 `qwen`，以及不需要 GPU 的 `remote` OpenAI-compatible 多模态 Provider。
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

## 实际部署测试教程

下面的部署分为三台逻辑组件：Jellyfin 插件运行在你的 Jellyfin Docker 容器中；Qdrant 运行在长期在线的 Ubuntu 设备上；Embedding Worker 运行在 Windows RTX 2070 笔记本上。插件只保存地址和任务状态，不保存 Worker 的模型或图片。

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

如果 Qdrant 与 Jellyfin 在同一台 Ubuntu 主机上，插件中的地址填写 `http://127.0.0.1:6333`；如果不在同一台机器，填写 Ubuntu 局域网地址，例如 `http://192.168.1.20:6333`。Qdrant 只需要长期在线，不依赖 Windows Worker。

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

云端模式仍运行一个很轻量的 Worker，但 Worker 不加载本地模型；它只负责把文本/Trickplay 图片转发到你指定的云端多模态 Embedding API，并把向量写入 Qdrant。RTX 2070 可以完全关闭。云端接口必须同时接受文本和图片，并且返回同一个向量空间；只有文本的普通 `/v1/embeddings` 不能用于本项目的视觉检索。

在 Windows Worker 目录先安装基础依赖（不安装 Torch/Qwen）：

```powershell
py -3.12 -m venv .venv
& .\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

然后用自定义 Base URL、模型名、API Key 启动（API Key 只存在当前 Worker 进程环境变量，不写入 Jellyfin，也不返回到 `/health`）：

```powershell
.\run_remote.ps1 `
  -BaseUrl "https://你的供应商.example/v1" `
  -EmbeddingModel "你的多模态嵌入模型" `
  -ApiKey "你的API_KEY" `
  -QdrantUrl "http://192.168.1.20:6333" `
  -Dimension 1024
```

为避免 API Key 出现在 PowerShell 历史记录，也可以省略 `-ApiKey`；脚本会以隐藏输入方式提示密钥。

默认请求为 OpenAI-compatible multimodal 格式：文本使用 `input: [{type: "text", text: "..."}]`，图片使用 `input: [{type: "image_url", image_url: {url: "data:image/jpeg;base64,..."}}]`；响应支持标准 `data[0].embedding`，并兼容常见的 `output.embeddings`/`vector` 返回。若供应商只接受纯字符串 `input`，可加 `-Protocol plain`，但该模式只有在供应商明确支持图片且仍保证文本/图片同空间时才可用。

先访问：

```powershell
Invoke-RestMethod http://127.0.0.1:8099/health
```

确认返回 `provider=remote`、模型名和正确 `dimension` 后，在 Jellyfin 插件设置中点击“**小批量安全测试**”。它随机抽取 1～10 个视频，只调用文本和少量 Trickplay 图片嵌入，不写入正式 Qdrant 集合；返回每个视频是否成功、测试帧数和错误信息。这样可以先验证供应商是否接受你的家庭/成人媒体图片、是否返回正确维度，再进行增量或完整索引。

云端供应商拒绝成人图片、返回内容安全错误、超时或维度不一致时，小批量测试会在对应视频的 `error` 字段显示原因；不要直接启动整库索引。确认测试通过后，再点击“初始化 Qdrant”并执行增量索引。

如果暂时只想测试链路而不下载模型，可以在 PowerShell 中运行 Mock 模式：

```powershell
$env:EMBEDDING_PROVIDER = "mock"
$env:QDRANT_URL = "http://192.168.1.20:6333"
& .\.venv\Scripts\python.exe -m uvicorn app:app --host 0.0.0.0 --port 8099
```

### 3. 编译和安装 Jellyfin 插件

GitHub Actions 成功后，在仓库的 Actions → CI → Artifacts 下载 `visual-search-plugin.zip`。解压到 Jellyfin 的插件目录。Docker 安装通常类似：

```bash
mkdir -p /你的Jellyfin配置目录/plugins/VisualSearch_0.1.0.0
unzip visual-search-plugin.zip -d /你的Jellyfin配置目录/plugins/VisualSearch_0.1.0.0
docker restart jellyfin
```

如果你的 Docker Compose 使用了命名卷，先通过 `docker volume inspect` 找到实际配置卷，或把插件目录复制到容器内的 `/config/plugins/VisualSearch_0.1.0.0`。安装后在 Jellyfin 管理后台的插件页面确认 **Visual Search** 已加载。

### 4. 配置插件

在插件设置中填写 Worker 地址；Qdrant 地址由 Windows Worker 的启动参数配置：

```text
Worker URL:  http://Windows笔记本局域网地址:8099
Qdrant URL:  http://Ubuntu局域网地址:6333  # 在 run_windows.ps1 中配置
Frames per video: 12
Visual weight: 0.75
Title weight: 0.25
```

插件设置页中的“测试 Worker”只检查连接和模型元数据；“小批量安全测试”才会真正发送文本和 Trickplay 图片。普通搜索仍保留，语义搜索入口独立于 Jellyfin 原搜索。

先点击测试连接，再只选择 20～50 个视频验证完整链路。管理员索引接口为 `POST /VisualSearch/Index/Item`，请求体示例：

```json
{"itemId":"Jellyfin视频ID","libraryId":"媒体库ID"}
```

插件从 Jellyfin 的 Trickplay manifest 读取 `Interval`、`ThumbnailCount`、`TileWidth`、`TileHeight` 和 `Width`，按实际 tile 布局采样，不重新解码原视频。

### 5. JS 注入兼容性

插件使用独立的 `window.JellyfinVisualSearch` 命名空间，并通过 `/VisualSearch/*` REST 路由工作，不覆盖 Jellyfin 原有搜索 API。你现有的 JS 注入可以继续保留；后续只需要在现有搜索界面增加一个按钮并调用：

```javascript
window.JellyfinVisualSearch.search('卧室，两个人', [], 30)
```

如果你的注入脚本修改了搜索页 DOM，插件不会强行替换它；语义搜索入口和结果页会单独实现。每次注入脚本初始化都应保持幂等，避免 Jellyfin SPA 路由切换时重复添加按钮。

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
