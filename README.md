# JMComic Desktop

一个偏实用、少折腾的 JMComic Windows 桌面客户端。  
能搜、能看榜、能批量下，重点是下载结果可以直接变成你想要的格式。

## 下载格式

支持三种输出：

| 格式 | 适合场景 |
| --- | --- |
| 图片目录 | 想保留原始分页，方便二次整理、压缩或导入其他工具 |
| ZIP | 想要一个干净的压缩包，方便归档、转移和备份 |
| PDF | 想直接用阅读器打开，或者放到平板/电子设备上看 |

格式复用也做了：

- 已经有图片目录，可以直接打包成 ZIP 或 PDF。
- 已经有 ZIP，可以转成图片目录或 PDF。
- 已经有 PDF，可以提取为图片目录或转 ZIP。
- 目标格式已存在时会跳过重复下载。

默认下载到程序同目录下的 `JMDownLoad`。

## 功能

- 关键词搜索
- 日榜、周榜、月榜
- 批量下载本子 ID 或章节 ID
- 图片目录 / ZIP / PDF 输出
- PDF 支持多章节合并或按章节分开
- 下载队列、任务状态、日志面板
- 下载记录和已下载资源索引
- C# 原生后端，不带 Python

## 获取 Release

Release 包按架构区分：

- `JMComicDesktop-win-x64.zip`
- `JMComicDesktop-win-x86.zip`
- `JMComicDesktop-win-arm64.zip`

解压后运行 `DesktopShell.exe`。

目标电脑需要安装：

- .NET Desktop Runtime
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

发布单文件 EXE，非自包含：

```powershell
dotnet publish DesktopShell\DesktopShell.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

把 `win-x64` 换成 `win-x86` 或 `win-arm64` 可以生成对应架构。

## 项目结构

```text
DesktopShell/                 WPF + WebView2 桌面壳
DesktopShell/NativeBackend/   C# 原生 JMComic 后端
frontend/                     WebView 前端页面
```

## 运行时文件

程序运行时可能生成：

- `JMDownLoad/`
- `config.json`
- `.jmdownload_index.json`
- 日志和构建输出

这些文件已在 `.gitignore` 中排除。

## 说明

本项目仅供学习交流使用。下载内容版权归原作者所有，请尊重版权并合理使用。
