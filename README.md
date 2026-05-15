<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=waving&color=gradient&customColorList=18&height=180&section=header&text=JMComic%20Desktop&fontSize=48&fontColor=fff&animation=fadeIn&fontAlignY=38&desc=%E6%BC%AB%E7%94%BB%E4%B8%8B%E8%BD%BD%E4%B8%8E%E6%A0%BC%E5%BC%8F%E8%BD%AC%E6%8D%A2%E5%B7%A5%E5%85%B7&descAlignY=58&descSize=16" width="100%"/>
</p>

<div align="center">

![Platform](https://img.shields.io/badge/Windows-x64%20%7C%20x86%20%7C%20arm64-0078d4?style=flat-square&logo=windows11&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-512bd4?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-68217a?style=flat-square)
![WebView2](https://img.shields.io/badge/WebView2-Required-0078d4?style=flat-square&logo=microsoftedge&logoColor=white)
![Backend](https://img.shields.io/badge/Backend-C%23-239120?style=flat-square&logo=csharp&logoColor=white)

**一个偏实用、少折腾的 JMComic Windows 桌面客户端。**  
能搜、能看榜、能批量下，也能把下载结果整理成你想要的格式。

</div>

---

## 界面预览

<table align="center">
  <tr>
    <td align="center" width="58%">
      <img width="1902" height="1281" alt="4560c64c-33c6-4304-86a5-edb25491f355" src="https://github.com/user-attachments/assets/5eeb9d1d-8f10-47ac-9db2-8af8b56ee8b9" />
      <sub>主界面</sub>
    </td>
  </tr>
</table>

## 下载格式

支持三种输出，按使用场景直接选：

| 格式 | 适合场景 |
|:---|:---|
| **图片目录** | 保留原始分页，方便二次整理、压缩或导入其他工具 |
| **ZIP** | 得到一个干净的压缩包，方便归档、转移和备份 |
| **PDF** | 直接用阅读器打开，或者放到平板、电子设备上看 |

格式复用也做了：

- 已有图片目录，可以直接打包成 ZIP 或 PDF。
- 已有 ZIP，可以转成图片目录或 PDF。
- 已有 PDF，可以提取为图片目录或转 ZIP。
- 目标格式已存在时会跳过重复下载。

默认下载到程序同目录下的 `JMDownLoad`。

---

## 功能特性

**搜索与榜单**

- 关键词搜索
- 日榜、周榜、月榜
- 支持本子 ID 和章节 ID

**下载与队列**

- 批量下载
- 图片目录 / ZIP / PDF 输出
- PDF 支持多章节合并或按章节分开
- 下载队列、任务状态、日志面板

**记录与复用**

- 下载记录
- 已下载资源索引
- 已有格式复用转换，减少重复下载

**运行方式**

- C# 原生后端
- WPF + WebView2 界面
- 不带 Python 后端

---

## 获取 Release

按系统架构下载对应版本：

| 架构 | 文件 |
|:---|:---|
| x64 | `JMComicDesktop-win-x64.zip` |
| x86 | `JMComicDesktop-win-x86.zip` |
| arm64 | `JMComicDesktop-win-arm64.zip` |

解压后运行 `DesktopShell.exe`。

---

## 运行要求

| 环境 | 要求 |
|:---|:---|
| 开发运行 | Windows、.NET 9 SDK、WebView2 Runtime |
| 发布版运行 | Windows x64 / x86 / arm64、.NET 9 Desktop Runtime、WebView2 Runtime |

> WebView2 Runtime 通常已预装于 Windows 11。若缺失，可从 Microsoft 官方页面安装。

---

## 构建与发布

```powershell
# 构建
dotnet build DesktopShell\DesktopShell.csproj

# 运行
dotnet run --project DesktopShell\DesktopShell.csproj

# 发布单文件 EXE，非自包含，win-x64
dotnet publish DesktopShell\DesktopShell.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

把 `win-x64` 换成 `win-x86` 或 `win-arm64` 可以生成对应架构。

---

## 数据文件

程序运行时可能生成：

| 文件 / 目录 | 说明 |
|:---|:---|
| `JMDownLoad/` | 默认下载目录 |
| `config.json` | 程序配置 |
| `.jmdownload_index.json` | 已下载资源索引 |
| 日志文件 | 运行和下载日志 |

这些文件已在 `.gitignore` 中排除。

---

## 项目结构

```text
JMComicDesktop/
├── DesktopShell/                 WPF + WebView2 桌面壳
│   └── NativeBackend/            C# 原生 JMComic 后端
└── frontend/                     WebView 前端页面
```

---

## 说明

本项目仅供学习交流使用。下载内容版权归原作者所有，请尊重版权并合理使用。

<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=waving&color=gradient&customColorList=18&height=100&section=footer&text=Have%20fun%20downloading&fontSize=18&fontColor=fff&animation=fadeIn" width="100%"/>
</p>
