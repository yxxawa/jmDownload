# JMComic Desktop

一个偏实用、少折腾的 JMComic Windows 桌面客户端。  
支持搜索、榜单、批量下载，并能把下载结果保存为 **图片目录 / ZIP / PDF**。

<p align="center">
  <img src="https://github.com/user-attachments/assets/63e366ec-980b-4977-b197-65e8164d2b16" alt="JMComic Desktop 主界面" width="720">
</p>

<p align="center">
  <img src="https://github.com/user-attachments/assets/74e3650a-2bce-4afd-b1c4-0d144d8e9c46" alt="JMComic Desktop 下载设置" width="520">
</p>

## 亮点

- 搜索漫画，查看日榜、周榜、月榜
- 支持本子 ID 和章节 ID 批量下载
- 下载格式支持 **图片目录 / ZIP / PDF**
- PDF 支持多章节合并或按章节分开
- 已有图片、ZIP、PDF 时尽量复用转换，减少重复下载
- 下载队列、任务状态、日志面板、下载记录
- C# 原生后端，不带 Python

## 下载格式

| 格式 | 适合场景 |
| --- | --- |
| 图片目录 | 保留原始分页，方便二次整理、压缩或导入其他工具 |
| ZIP | 得到一个干净的压缩包，方便归档、转移和备份 |
| PDF | 直接用阅读器打开，或者放到平板、电子设备上看 |

格式复用：

- 已经有图片目录，可以直接打包成 ZIP 或 PDF。
- 已经有 ZIP，可以转成图片目录或 PDF。
- 已经有 PDF，可以提取为图片目录或转 ZIP。
- 目标格式已存在时会跳过重复下载。

默认下载到程序同目录下的 `JMDownLoad`。

## 获取 Release

按系统架构下载对应版本：

- `JMComicDesktop-win-x64.exe`
- `JMComicDesktop-win-x86.exe`
- `JMComicDesktop-win-arm64.exe`

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
