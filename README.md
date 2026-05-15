<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=rect&color=3d8c40,5aac44,3d8c40&height=12&section=header" width="100%"/>
  <img src="https://capsule-render.vercel.app/api?type=rect&color=8B6914,6B4F12,8B6914&height=8&section=header" width="100%"/>
</p>

<p align="center">
  <img src="https://readme-typing-svg.demolab.com?font=Press+Start+2P&size=22&pause=1000&color=5aac44&center=true&vCenter=true&width=600&height=70&lines=JMComic+Desktop" alt="Title"/>
</p>

<p align="center">
  <img src="https://readme-typing-svg.demolab.com?font=Press+Start+2P&size=10&pause=2000&color=c8a96e&center=true&vCenter=true&width=600&lines=%E6%BC%AB%E7%94%BB%E4%B8%8B%E8%BD%BD+%26+%E6%A0%BC%E5%BC%8F%E8%BD%AC%E6%8D%A2%E5%B7%A5%E5%85%B7" alt="Subtitle"/>
</p>

<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=rect&color=8B6914,6B4F12,8B6914&height=8&section=header" width="100%"/>
  <img src="https://capsule-render.vercel.app/api?type=rect&color=3d8c40,5aac44,3d8c40&height=12&section=header" width="100%"/>
</p>

<div align="center">

<a href="https://github.com/your-repo/stargazers"><img src="https://img.shields.io/github/stars/your-repo?style=for-the-badge&color=5aac44&logo=github&logoColor=white" alt="Stars"/></a>
![Platform](https://img.shields.io/badge/Windows-x64%20%7C%20arm64-3d8c40?style=for-the-badge&logo=windows11&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-6B4F12?style=for-the-badge&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-8B6914?style=for-the-badge)
![Backend](https://img.shields.io/badge/Backend-C%23-5aac44?style=for-the-badge&logo=csharp&logoColor=white)

<img src="https://readme-typing-svg.demolab.com?font=Press+Start+2P&size=11&pause=1200&color=c8a96e&center=true&vCenter=true&width=560&lines=%E6%90%9C%E7%B4%A2+%2F+%E6%A6%9C%E5%8D%95+%2F+%E6%89%B9%E9%87%8F%E4%B8%8B%E8%BD%BD+%2F+%E6%A0%BC%E5%BC%8F%E8%BD%AC%E6%8D%A2;%E5%9B%BE%E7%89%87%E7%9B%AE%E5%BD%95+%3C%3D%3E+ZIP+%3C%3D%3E+PDF;C%23+%E5%8E%9F%E7%94%9F%E5%90%8E%E7%AB%AF%EF%BC%8C%E4%B8%8D%E4%BE%9D%E8%B5%96+Python" alt="Typing SVG"/>

**一个偏实用、少折腾的 JMComic Windows 桌面客户端。**  
能搜、能看榜、能批量下，也能把下载结果整理成你想要的格式。

</div>

---

## 界面预览

<table align="center">
  <tr>
    <td align="center" width="58%">
      <img width="1902" height="1281" alt="主界面" src="https://github.com/user-attachments/assets/5eeb9d1d-8f10-47ac-9db2-8af8b56ee8b9" />
      <sub>主界面</sub>
    </td>
  </tr>
</table>

---

## 下载格式

支持三种输出，按使用场景直接选：

| 格式 | 适合场景 |
|:---:|:---|
| 🖼️ **图片目录** | 保留原始分页，方便二次整理、压缩或导入其他工具 |
| 📦 **ZIP** | 得到一个干净的压缩包，方便归档、转移和备份 |
| 📄 **PDF** | 直接用阅读器打开，或者放到平板、电子设备上看 |

<details>
<summary>📂 格式复用说明（点击展开）</summary>
<br>

- 已有图片目录 → 直接打包成 ZIP 或 PDF
- 已有 ZIP → 转成图片目录或 PDF
- 已有 PDF → 提取为图片目录或转 ZIP
- 目标格式已存在时会跳过重复下载

</details>

默认下载到程序同目录下的 `JMDownLoad`。

---

## 功能特性

<details open>
<summary>🔍 搜索与榜单</summary>
<br>

- 关键词搜索
- 日榜、周榜、月榜
- 支持本子 ID 和章节 ID

</details>

<details open>
<summary>⬇️ 下载与队列</summary>
<br>

- 批量下载
- 图片目录 / ZIP / PDF 输出
- PDF 支持多章节合并或按章节分开
- 下载队列、任务状态、日志面板

</details>

<details open>
<summary>📁 记录与复用</summary>
<br>

- 下载记录
- 已下载资源索引
- 已有格式复用转换，减少重复下载

</details>

<details open>
<summary>⚙️ 运行方式</summary>
<br>

- C# 原生后端，无需 Python
- WPF + WebView2 界面
- 单文件 EXE，开箱即用

</details>

---

## 获取 Release

按系统架构下载对应版本：

| 架构 | 文件 |
|:---:|:---|
| ![x64](https://img.shields.io/badge/x64-0078d4?style=flat-square&logo=windows11&logoColor=white) | `JMDownload-win-x64.exe` |
| ![arm64](https://img.shields.io/badge/arm64-0078d4?style=flat-square&logo=windows11&logoColor=white) | `JMDownload-win-arm64.exe` |

直接运行，无需解压。

---

## 运行要求

| 环境 | 要求 |
|:---|:---|
| 开发运行 | Windows、.NET 9 SDK、WebView2 Runtime |
| 发布版运行 | Windows x64 / arm64、.NET 9 Desktop Runtime、WebView2 Runtime |

> WebView2 Runtime 通常已预装于 Windows 11。若缺失，可从 Microsoft 官方页面安装。

---

## 构建与发布

<details>
<summary>📋 展开命令</summary>
<br>

```powershell
# 构建
dotnet build DesktopShell\DesktopShell.csproj

# 运行
dotnet run --project DesktopShell\DesktopShell.csproj

# 发布单文件 EXE，非自包含，win-x64
dotnet publish DesktopShell\DesktopShell.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

把 `win-x64` 换成 `win-arm64` 可以生成对应架构。

</details>

---

## 数据文件

<details>
<summary>📄 展开说明</summary>
<br>

程序运行时可能生成：

| 文件 / 目录 | 说明 |
|:---|:---|
| `JMDownLoad/` | 默认下载目录 |
| `config.json` | 程序配置 |
| `.jmdownload_index.json` | 已下载资源索引 |
| 日志文件 | 运行和下载日志 |

这些文件已在 `.gitignore` 中排除。

</details>

---

## 项目结构

```text
JMComicDesktop/
├── DesktopShell/                 WPF + WebView2 桌面壳
│   └── NativeBackend/            C# 原生 JMComic 后端
└── frontend/                     WebView 前端页面
```

---

## Star History

<div align="center">
  <a href="https://star-history.com/#your-username/your-repo&Date">
    <img src="https://api.star-history.com/svg?repos=your-username/your-repo&type=Date&theme=dark" alt="Star History Chart" width="600"/>
  </a>
</div>

---

## 说明

本项目仅供学习交流使用。下载内容版权归原作者所有，请尊重版权并合理使用。

<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=rect&color=3d8c40,5aac44,3d8c40&height=12&section=footer" width="100%"/>
  <img src="https://capsule-render.vercel.app/api?type=rect&color=8B6914,6B4F12,8B6914&height=8&section=footer" width="100%"/>
</p>
