# 开发说明

## 构建入口

桌面程序入口由 `App.xaml` / `App.xaml.cs` 提供，正常构建应得到 `WinExe`。根目录项目文件将 `SmokeTest` 默认设为 `false`；`scripts/build.ps1` 与 `scripts/publish.ps1` 也会显式传入 `-p:SmokeTest=false`。

`NativeBackendSmoke.cs` 是独立的开发诊断入口。只有显式传入 `-p:SmokeTest=true` 时，项目才会将输出类型和启动对象切换到 `DesktopShell.NativeBackendSmoke`。它用于本地 API、下载相关流程和图片/ZIP/PDF 文件处理的烟雾验证，完成后会退出，不是桌面 GUI。

建议的日常命令：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/publish.ps1 -RuntimeIdentifier win-x64
./scripts/selfcheck.ps1 -ExecutablePath ./release/win-x64/DesktopShell.exe
```

## 资源与前端

`frontend/**` 通过 MSBuild `EmbeddedResource` 作为 `frontend/<相对路径>` 嵌入程序集。开发时服务端优先从项目目录提供前端文件；单文件发布找不到源文件时会读取嵌入资源。因此添加、重命名或删除前端资源时，需要同时检查脚本引用和发布包自检。

## 本地服务安全边界

`NativeBackendServer` 在随机可用端口上仅绑定 `127.0.0.1`，每次启动生成随机会话令牌。HTTP API 默认需要 Bearer 令牌；浏览器图片元素使用查询参数令牌的例外只用于本机封面和阅读图片。不要将监听地址改为 `0.0.0.0`，不要把本地令牌写入前端源码、日志、截图或 issue。

## 持久化数据

默认用户数据根目录为 `%LOCALAPPDATA%\JMComicDesktop`。阅读状态存放于 `reader\state.json`，图片缓存、配置及节点缓存由各自服务管理。自动化测试应传入临时隔离目录，不应读取或覆盖开发者的真实用户数据。

## 自检边界

发布自检入口：

```powershell
DesktopShell.exe --selfcheck <report.json>
```

自检应只验证内嵌静态文件、阅读器初始化和本地鉴权等；它不代表上游搜索/下载服务此刻可用，也不替代 Windows 桌面上的人工启动检查。在线阅读/下载功能验证可能产生上游请求，运行前应确认访问权限和站点条款。