# JMComic Desktop

纯 C# / WebView2 的 PC 端 JMComic 下载工具。

## 功能

- 搜索漫画和查看日榜、周榜、月榜
- 批量下载本子 ID 或章节 ID
- 输出为图片目录、ZIP 或 PDF
- 已有图片、ZIP、PDF 时尽量复用转换，减少重复下载
- 下载记录、任务状态、日志面板
- 默认下载目录为程序目录下的 `JMDownLoad`

## 目录

```text
DesktopShell/                 WPF + WebView2 桌面壳
DesktopShell/NativeBackend/   C# 原生 JMComic 后端移植
frontend/                     WebView 前端页面
```

## 环境要求

- Windows
- .NET Desktop SDK/Runtime，版本需匹配 `DesktopShell/DesktopShell.csproj`
- Microsoft Edge WebView2 Runtime

## 开发运行

```powershell
dotnet run --project DesktopShell\DesktopShell.csproj
```

## 构建

普通构建：

```powershell
dotnet build DesktopShell\DesktopShell.csproj
```

发布为单文件框架依赖 EXE：

```powershell
dotnet publish DesktopShell\DesktopShell.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

输出目录通常为：

```text
DesktopShell\bin\Release\net9.0-windows\win-x64\publish\
```

目标机器仍需要安装 .NET Desktop Runtime 和 WebView2 Runtime。

## 运行时文件

程序运行时可能生成：

- `JMDownLoad/`
- `config.json`
- `.jmdownload_index.json`
- 日志和构建输出

这些文件已在 `.gitignore` 中排除。

## 说明

本项目仅供学习交流使用，下载内容版权归原作者所有，请尊重版权并合理使用。
