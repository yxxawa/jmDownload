# 架构说明

## 组件与启动流程

```text
App.xaml / App.xaml.cs
        │ 创建并显示窗口
        ▼
MainWindow.xaml / MainWindow.xaml.cs
        ├── NativeBackendServer：127.0.0.1 随机端口 + 会话令牌
        └── WebViewBridge：注入令牌，初始化 WebView2
                │ 加载本地工作区
                ▼
frontend/index.html + styles.css + app.js + reader.js + reader.css
                │ HTTP API / WebSocket 事件
                ▼
NativeBackend/
        ├── JmClient：上游请求与作品元数据
        ├── ReaderService：阅读会话、章节/图片加载、进度与预读
        ├── ImageRepository：图片读取、缓存、并发优先级
        ├── NativeDownloadManager：下载任务与队列
        └── ArtifactTools：图片、ZIP、PDF 等文件处理
```

WPF 负责 Windows 窗口和桌面生命周期。WebView2 承载 HTML/CSS/JavaScript 工作区。C# 后端使用 loopback HTTP 和 WebSocket 与前端通信；前端文件同时作为程序集嵌入资源，以支持不携带源码目录的发布包。

## 主要模块

| 模块 | 责任 |
| --- | --- |
| `App.xaml.cs` | WPF 启动与发布包 `--selfcheck` 分支 |
| `MainWindow.xaml.cs` | 启停本地后端、创建 WebView2、加载工作区 |
| `WebViewBridge.cs` | WebView2 初始化、令牌注入和请求头授权 |
| `DesktopBridge.cs` | 文件夹选择、打开本地目录等桌面能力 |
| `NativeBackendServer.cs` | HTTP/WebSocket 路由、静态资源服务、会话鉴权 |
| `JmClient.cs` | 上游搜索、排行、详情、封面及章节数据请求 |
| `ReaderService.cs` | 阅读器会话、章节清单、图片读取、预读与进度 |
| `ReaderStore.cs` | 书架、收藏、阅读进度、书签和阅读设置持久化 |
| `ImageRepository.cs` | 图片缓存、读取及容量管理 |
| `NativeDownloadManager.cs` | 下载任务、并发执行、进度和取消 |
| `ArtifactTools.cs` | 图片整理及 ZIP/PDF 等输出格式 |
| `frontend/app.js` | 搜索/排行榜、详情、下载队列和书架 UI |
| `frontend/reader.js` | 在线阅读器、章节/页码导航、设置和进度保存 |

## 本地 HTTP API

所有 `/api/*` 请求均通过本地会话令牌保护。以下是源码中实现的主要路径（`{id}`、`{index}` 为路径参数）：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health` | 本地服务状态 |
| `GET` / `PUT` | `/api/config` | 下载和界面配置 |
| `GET` | `/api/search` | 搜索、分页、排序与时间筛选 |
| `GET` | `/api/ranking` | 排行榜 |
| `GET` | `/api/album/{id}` | 作品详情 |
| `GET` | `/api/cover/{id}` | 封面图片 |
| `POST` | `/api/download` | 创建下载任务 |
| `GET` | `/api/tasks` | 获取任务快照 |
| `POST` | `/api/download/stop` | 停止下载队列 |
| `POST` | `/api/download/cancel/{id}` | 取消单个任务 |
| `GET` / `POST` / `PATCH` / `PUT` / `DELETE` | `/api/reader/...` | 阅读会话、章节、图片、书架、书签、设置、缓存及阅读进度 |
| `GET` | `/ws/events` | 下载状态事件 |

阅读器的具体路由以 `NativeBackendServer.RouteReaderAsync` 为准；新增或变更接口时，请同步更新前端调用和本文档。

## 阅读请求生命周期

1. 前端打开作品，后端创建短期阅读会话并记住作品元数据。
2. 前端请求章节清单；后端按需读取章节页索引和图片信息。
3. 当前可视页优先进入图片请求队列；邻近页面以有限并发预读。
4. 前端上报当前阅读窗口，后端使用 revision/cancellation 避免过期窗口继续预读。
5. 阅读进度、书签、书架和设置由 `ReaderStore` 保存到本地 JSON；关闭阅读会话时取消其剩余请求。

## 用户数据与安全

默认数据路径为 `%LOCALAPPDATA%\JMComicDesktop`。本地 HTTP 服务只应监听 IPv4 回环地址 `127.0.0.1`，令牌按进程启动随机生成。封面和阅读图片由于浏览器图片标签无法附加 Authorization 请求头，允许使用短期令牌查询参数；不要将服务暴露到局域网或公网。

具体数据文件布局可能随版本扩展；不要将用户数据、下载内容、WebView2 数据目录或令牌纳入源码仓库。