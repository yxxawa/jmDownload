# JM Download · 手机端（Android）

独立的安卓应用：**装到手机上就能用，不需要电脑、不需要任何外部服务**。

搜索、作品详情、下载队列、图片还原、ZIP/PDF 导出、书架、书签、阅读进度、在线阅读 —— 全部在手机本地完成，数据存在手机的公共存储里。

---

## 与电脑端的关系

手机端**不是**重写的一份实现。它直接编译电脑端仓库里 `NativeBackend/` 的同一批 C# 源码：

| 模块 | 来源 | 说明 |
| --- | --- | --- |
| `JmClient` | 直接复用 | 站点请求、节点切换、封面与图片抓取 |
| `JmCrypto` | 直接复用 | 响应解密与签名 |
| `NativeDownloadManager` | 直接复用 | 下载队列、并发控制、断点续传 |
| `ReaderService` / `ReaderStore` | 直接复用 | 阅读会话、书架、进度、书签 |
| `ImageRepository` | 直接复用 | 图片缓存与按需取图 |
| `ArtifactTools` | 生成式复用 | 文件名清洗、ZIP、PDF 写入（见下） |
| `JmImageDecoder` | Android 重写 | 电脑端用 WPF `BitmapSource`，手机端用 `Android.Graphics.Bitmap`，算法逐行对应 |
| `NativeBackendServer` | 直接复用 | 本地 HTTP/WebSocket 服务，接口契约与电脑端完全一致 |

所以电脑端修了 bug 或加了功能，手机端重新构建一次就跟上了。

### 为什么会有两份 `ArtifactTools` / `JmImageDecoder`

整个 `ArtifactTools`（约 570 行）都是平台无关的，只有繁简转换用的 Win32 `LCMapStringEx` 在 Android 上不存在。

`tools/sync-backend.mjs` 会在每次构建前，从电脑端源码**自动生成** `Backend/ArtifactTools.cs`，只替换那一处（改用 Android 自带的 ICU 音译器），其余逐字保留。生成文件不手改，改动一律落在电脑端源码。

`JmImageDecoder` 依赖 WPF 的 `BitmapSource`，无法复用，因此在 `Backend/JmImageDecoder.cs` 里用 `Android.Graphics.Bitmap` 实现同一套分段还原算法。这一份比电脑端更省内存：按行分块搬运，只分配一个分块大小的缓冲区，而不是整图两份。

---

## 界面

`frontend/` 是与电脑端同源的手机界面，同一套 HTML/CSS/JS：

- 底部标签栏：发现 / 书架 / 任务 / 记录 / 设置
- 作品网格与列表两种排布，下拉刷新，卡片加入清单有触感反馈
- 作品详情的章节目录展开动画、封面大图
- 下载清单：输出格式（图片 / ZIP / PDF）、保存位置、质量与并发
- 阅读器：竖向滚动 / 单页 / 双页，章节目录抽屉，书签，双指捏合缩放，双击快速缩放，左右轻点或滑动翻页，中间轻点隐藏工具栏
- 自适应刘海与手势条（系统栏内边距直接算进页面，不会被遮挡）
- 所有控件图标都是内联 SVG，与电脑端用的是同一批 `symbol` 定义

界面资源以**嵌入资源**打进 APK，由应用内的本地服务对外提供，因此离线也能打开界面。

---

## 构建

### 一次性准备工具链

```powershell
powershell -ExecutionPolicy Bypass -File mobile\android\tools\install-toolchain.ps1
```

会在 `C:\jm-mobile-toolchain` 下装好 JDK 21 与 Android SDK（不动系统环境变量，整个目录删掉即可回退），然后安装 .NET Android 工作负载：

```powershell
dotnet workload install android
```

### 出包

```powershell
powershell -ExecutionPolicy Bypass -File mobile\android\tools\build-apk.ps1
```

脚本会依次：生成图标 → 同步 `ArtifactTools` → 构建 → 签名 → 输出 `mobile/android/dist/JMDownload-mobile.apk`。

首次运行会在工具链目录生成一个自签名密钥（**不会**写进仓库）。

- 默认**每次先清空 `bin`/`obj` 再构建**。.NET 10 for Android 的增量构建会产出启动即崩的包
  （症状是 `n_onCreate` 找不到实现，看起来像 Mono 运行时没加载）。要省时间可以加 `-Incremental`，但出包前请用默认方式构建一次。
- `-DebugBuild`：出调试包。脚本会自动补上 `-p:EmbedAssembliesIntoApk=true`；Release **不能**加这个属性，否则同样会产出启动即崩的包。

### 安装到手机

```powershell
adb install -r mobile\android\dist\JMDownload-mobile.apk
```

或者把 APK 传到手机上直接点击安装（需在系统里允许「安装未知来源应用」）。

首次打开会请求两个权限：

1. **通知** —— 用于显示后台下载进度，可选。
2. **所有文件访问** —— 用于把漫画保存到你自己选的目录。不授予也能用，但只能存到应用私有目录。界面上会给出提示入口。

### 两个必须知道的坑

1. **不要给这个项目加 `[Application]` 子类。** .NET 10 for Android 下自定义 `Application` 会触发
   `Unable to activate instance ... from native handle`，表现为 `UnsatisfiedLinkError: No implementation found for ... n_onCreate()`，
   看起来像 Mono 运行时没加载，实际是自定义 Application 激活失败，整个进程启动即崩。
   所以 `AppLog` 改成了首次写日志时自己取 `Context`，而不是在 Application 里初始化。
2. **不要用增量构建出包。** 见上面的 `-Incremental` 说明，症状同样是 `n_onCreate`。

---

## 交互约定

- **全屏**：界面一直铺到屏幕物理边缘，系统栏高度由原生侧按屏幕密度换算成 CSS 像素后交给页面
  （`window.__jmInsets`），由 CSS 自己避开，所以背景是连贯的，不会在状态栏位置留一条色带。
- **沉浸阅读**：进入阅读器时收起系统栏、保持屏幕常亮；退出时恢复。
- **返回键**：依次是 收浮层 → 退出搜索 → 回到「发现」→ 退出应用。由 Activity 调用页面的
  `window.__jmBack()`，拿不到返回值（页面还没加载完）时也会正常退出，不会出现按了没反应。

---

## 已验证

在 Android 15（API 35，x86_64 模拟器）上实机跑通并逐项确认：

- APK 安装、启动，应用内的 C# 后端正常监听 `127.0.0.1`（`HttpListener` 在 .NET for Android 上可用）。
- 榜单界面拉到**真实上游数据**，封面图片正常加载。
- 作品详情：作者、标签、章节数、章节目录展开。
- 阅读器：打开真实章节、按页加载、进度保存、工具栏与进度条。
- 下载：把作品下到 `/storage/emulated/0/JMDownload/<作品ID>/<标题>/<章节>/`，
  写出的是**分段还原正确**的 PNG（拉回本地逐页核对过，分镜和对白框连续，没有错位）。
- 系统栏内边距由原生侧按屏幕密度换算后交给页面，界面铺满整屏且状态栏、手势条都没有压住内容。
- 阅读器为沉浸式全屏：进入时状态栏与手势条一起收起，页面从屏幕最顶端画到最底端。
- 返回键层级：收浮层 → 退出搜索 → 回到「发现」→ 退出应用，逐级验证通过。

未在真机验证的部分：搜索提交、ZIP/PDF 导出、书签、书架、滑动手势与双指缩放 ——
这些走的是与电脑端共享的后端代码路径，或已在浏览器预览中确认过渲染，但没有在手机上逐项点过。


---

## 目录结构

```text
mobile/
├── frontend/                  手机端界面（与电脑端同源，也被 APK 打包进去）
│   ├── index.html             页面结构与内联 SVG 图标表
│   ├── styles.css             设计系统、布局、动效
│   ├── reader.css             阅读器样式
│   ├── app.js                 全部业务逻辑与移动交互
│   ├── reader.js              阅读器状态机与手势
│   ├── sw.js                  Service Worker（仅网页版注册）
│   ├── manifest.webmanifest   PWA 清单
│   └── icons/                 图标 + 生成脚本 build-icons.mjs
└── android/                   安卓应用
    ├── JmDownload.Mobile.csproj
    ├── AndroidManifest.xml
    ├── App.cs                 本地服务宿主、日志、首次运行目录初始化
    ├── MainActivity.cs        WebView 外壳、系统栏、权限、原生动作
    ├── BackendService.cs      前台服务（后台下载 + 进度通知）
    ├── Backend/               Android 侧的平台相关实现
    │   ├── JmImageDecoder.cs  基于 Android Bitmap 的分段还原
    │   ├── ChineseScript.cs   ICU 繁简转换
    │   └── ArtifactTools.cs   由 sync-backend.mjs 生成，勿手改
    ├── Resources/             图标、主题、字符串
    ├── dist/                  构建产物（JMDownload-mobile.apk，已 gitignore）
    └── tools/
        ├── install-toolchain.ps1
        ├── build-apk.ps1
        └── sync-backend.mjs
```

---

## 和电脑端不同的地方

- **长文件名**按 UTF-8 字节数限制到 120 字节，超过时保留标题前缀和稳定摘要；支持中文与表情字符，为文件扩展名、重名后缀和临时文件留出空间。
- **保存位置**由电脑路径变成手机路径，默认 `/storage/emulated/0/JMDownload`。首次运行自动改掉电脑端的默认值，之后你自己改过的路径不会被覆盖。
- **打开目录**会弹出 Android 的应用选择器，让用户选择支持打开文件夹的 App；已设置默认文件管理器时也会显示选择器。
- **目录选择**在手机上改为手输路径 + 常用位置快捷选择，没有电脑端那种目录浏览服务。
- **连接设置**在应用里被隐藏 —— 服务就在应用进程内，不需要配置地址和令牌。
- 阅读时自动**保持屏幕常亮**，退出阅读器恢复。
- 主题切换会同步系统状态栏/导航栏颜色。

---

## 已知取舍

- APK 约 60 MB：包含三个 ABI 的 .NET 运行时。项目关闭了裁剪（`AndroidLinkMode=None`），因为后端大量依赖 `System.Text.Json` 的反射序列化，裁剪会静默丢字段。
- 下载在**前台服务**里跑，通知栏会常驻一条进度。划掉应用不会中断下载，但彻底「强行停止」应用会。
- 界面里的网页版仍可单独打开（`mobile/frontend/index.html`）；但浏览器有跨域限制，拿不到站点数据，只适合看界面，不能作为手机端的主要形态。

---

## 许可

与仓库其余部分一致：**当前源码快照没有附带 LICENSE 文件**。发布前请确认你对源码、图标等拥有相应权利，并复核上游服务条款与内容版权要求。
