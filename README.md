# Jellyfin Visual Search

面向 Jellyfin 10.11.x 的视频级多模态语义搜索插件。插件直接调用云端 OpenAI-compatible 多模态 Embedding API，并直接访问 Qdrant；正式部署不需要 Worker、RTX 2070、Python 或环境变量。

## 运行结构

```text
Jellyfin 插件 ── HTTP/HTTPS ── 云端多模态 Embedding API
       │
       └──────────── HTTP ──── Qdrant（Ubuntu）
```

所有运行配置都在 Jellyfin 的 Visual Search 页面填写：Embedding Base URL、模型、API Key、协议、输入格式、向量维度、超时、图片最大尺寸、Embedding batch size、Qdrant URL、索引权重和调度参数。batch size 默认 32，表示一次 HTTP Embedding 请求最多发送多少个文本或图片输入。定时增量索引使用“星期 + 时段表”，例如 `周一 02:00-06:00;22:00-23:30`，每行一个星期；时段外发现的新视频或变更会等待下一个时段，手动索引不受限制。Docker 中两个地址都不能默认使用 `127.0.0.1`，除非目标服务与 Jellyfin 在同一容器。`auto` 输入格式优先发送 `[{"text":"..."}]` 或 `[{"image":"data:..."}]`，遇到 HTTP 400 会依次尝试纯字符串和 OpenAI `type/image_url` 形状；`EmbeddingDimension > 0` 时请求会带 `dimensions`。云端 API 必须让文本和图片处于同一个向量空间；只有纯文本 `/v1/embeddings` 的服务不能用于视觉检索。

API Key 不会出现在健康检查返回值或普通日志中，但会保存在 Jellyfin 插件配置中，请限制配置目录的访问权限。

## Qdrant（Ubuntu）

```bash
mkdir -p ~/jellyfin-visual-search/qdrant_storage
cd ~/jellyfin-visual-search
wget -O qdrant-docker-compose.yml https://raw.githubusercontent.com/chengzi4871/jellyfin-serach/main/deploy/qdrant-docker-compose.yml
docker compose -f qdrant-docker-compose.yml up -d
curl http://127.0.0.1:6333/healthz
```

如果 Qdrant 与 Jellyfin 不在同一台机器，配置页面填写 Ubuntu 的局域网地址。点击“初始化 Qdrant”后，插件会直接创建 `jellyfin_video_text` 和 `jellyfin_video_frames` 集合。

## Jellyfin 插件安装

GitHub Actions 的下载文件通常是外层 Artifact 压缩包。解开外层后，取出其中的 `visual-search-plugin.zip`。内层插件包只应包含以下 4 个文件：

```text
Jellyfin.Plugin.VisualSearch.dll
Jellyfin.Plugin.VisualSearch.pdb
SixLabors.ImageSharp.dll
meta.json
```

不要把 `MediaBrowser.*`、`Jellyfin.*`、`Microsoft.Extensions.*` 或 EntityFrameworkCore 等框架副本复制进插件目录，否则可能出现程序集加载后插件实例无法创建、后台不显示且日志不明显报错的问题。

```bash
rm -rf ~/docker/jellyfin/config/data/plugins/VisualSearch_0.1.0.5
mkdir -p ~/docker/jellyfin/config/data/plugins/VisualSearch_0.1.0.5
unzip visual-search-plugin.zip -d ~/docker/jellyfin/config/data/plugins/VisualSearch_0.1.0.5
docker restart jellyfin
```

## 第一次测试

1. 填写 Embedding Base URL、模型、API Key、维度和 Qdrant URL；Docker 中不要填写 `127.0.0.1`，除非 Qdrant 与 Jellyfin 在同一容器。
2. 点击“保存并检查连接”。失败时页面会显示失败阶段（cloud 或 qdrant）、HTTP 状态和服务端错误文案。
3. 点击“初始化 Qdrant”；该操作是幂等的，已存在的集合会直接复用。
4. 在“语义能力验收”中随机选择一个已有 Trickplay 的视频，先检查即将发送的标题与裁切帧；确认后才调用模型，需要更换时重新随机选择。
5. 确认模型返回向量后手动输入查询词，查看标题和每帧的余弦相似度，再开始增量索引。
6. 使用“刷新状态”检查队列进度、等待重试次数、永久失败数、取帧成功/失败数和失败原因分布。

语义能力验收展示实际送入模型的标题和单独视频帧；查询词不预填写，由用户自行输入。测试不会写入正式 Qdrant 集合，也不再提供重复的小批量安全测试入口。

## 采样、去重和排序

插件通过 Jellyfin 10.11 的 `ITrickplayManager` 读取 Trickplay 元数据，从拼图中裁剪单独帧，不重新解码原视频。读取时按照库的 `SaveTrickplayWithMedia` 设置优先查找媒体旁目录，并回退查找 Jellyfin 本地目录，避免迁移过程中漏掉数据。语义验收通过 `GetTrickplayItemsAsync` 只从已有 Trickplay 的视频中随机选择。短视频按约 30 秒覆盖一个时间点，基础采样至少 6 帧；约 5 分钟以上的长视频可启用场景变化采样，最多读取 24 个稀疏探针，用 8×8 颜色指纹和 dHash 估计镜头变化，再以场景变化优先、时间覆盖补充的方式选择最终帧。相同 tile 在一次读取中只解码一次，探针不会全部发送到云端；`单视频最大截图量` 是最终硬上限，不表示固定采样数。发送云端前可用配置开关启用轻量 dHash 和颜色差异近重复过滤，并保留首帧和末帧。

索引状态页每 3 秒刷新一次，显示运行类型、阶段、视频总进度、当前视频、当前帧、Embedding 批次、已完成输入、请求数、处理速度、预计剩余时间、自动重试和永久失败数量；取帧失败原因也会按类别汇总。

```text
VisualScore = 0.50 × BestFrame + 0.30 × Top3Average + 0.20 × Top5Average
FinalScore = VisualScore × VisualWeight + TitleScore × TitleWeight

标题和画面结果先分别做 P10～P90 归一化，再参与融合；返回给前端的综合、画面和标题分数均为 0～1 的归一化分数。
```

缺少某一模态时会自动重新归一化。改变权重不需要重新生成向量。取帧失败会记录 `tile_missing`、`tile_read_error` 等原因，并在索引状态中单独统计。

## API 和兼容性

- `GET /VisualSearch/Health`：测试云端 Embedding 和 Qdrant；
- `POST /VisualSearch/Test/Preview`：随机选择视频并只提取即将发送的标题和裁切帧，不调用模型；
- `POST /VisualSearch/Test/Inspect`：在用户确认后调用模型，展示返回向量摘要，并在有查询词时计算相似度；
- `POST /VisualSearch/Index/Ensure`：初始化集合；
- `POST /VisualSearch/Index/Incremental`：增量索引；
- `POST /VisualSearch/Index/Rebuild`：完整重建；
- `POST /VisualSearch/Index/Pause`、`Resume`、`Cancel`：控制索引队列；
- `POST /VisualSearch/Search`：语义检索。

Embedding 网络错误、超时、429 和 5xx 会进入等待队列并指数退避重试；401、403、404、400 和 413 等配置或请求错误会立即记录为永久失败。定时增量索引默认关闭，启用后只在配置的星期及时段内执行；插件会记录上次完成的定时扫描时间，后续只处理新增或变更的视频。手动增量索引和完整重建会立即执行，并优先于等待中的定时任务。

插件使用独立的 `window.JellyfinVisualSearch` 命名空间和 `/VisualSearch/*` 路由，不修改普通 `/Items` 搜索请求，可以和现有 JS 注入共存。配置页遵循 Jellyfin `data-role="page"` 插件页面结构，兼容 Jellyfin 10.11 Web 客户端和移动端布局。

## 开发验证

```bash
pytest -q
python worker_test.py
```

Worker 目录仍保留作为开发和 Mock 测试工具，但不属于云端直连部署的必要组件。GitHub Actions 会编译插件、检查 ImageSharp 依赖，并生成只包含 4 个运行文件的内层插件包。
