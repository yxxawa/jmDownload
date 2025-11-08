# JMComic漫画 下载器

JMComic 下载器是一个基于 Python 和 Tkinter 开发的图形界面工具，用于下载 JMComic 网站的漫画内容。
API:https://github.com/hect0x7/JMComic-Crawler-Python
## 界面
![image](https://github.com/yxxawa/jmDownload/blob/main/%E7%A4%BA%E4%BE%8B%E5%9B%BE%E7%89%87/%E7%A4%BA%E4%BE%8B.png)
## 功能特性

-  **图形界面**: 基于 ttkbootstrap 的现代化 Windows 11 风格界面
-  **搜索功能**: 支持关键词搜索漫画本子
-  **排行榜**: 支持查看日、周、月排行榜
-  **批量下载**: 支持下载多个本子或章节 ID
-  **封面预览**: 支持封面显示和放大查看
-  **多主题**: 支持多种界面主题风格
-  **高级设置**: 支持图片格式转换、并发控制等高级选项

## 安装说明

### 方法一：使用打包的 EXE 文件（推荐）

1. 从 [Releases](https://github.com/your-username/jmcomic-downloader/releases) 页面下载最新版本的 `JMComic下载器.exe`
2. 直接运行即可使用，无需安装 Python 环境

### 方法二：从源码运行

1. 确保已安装 Python 3.7+
2. 克隆或下载本项目源码
3. 安装依赖：
   ```bash
   pip install ttkbootstrap pillow jmcomic
   ```
4. 运行程序：
   ```bash
   python jmcomic_gui.py
   ```

## 使用方法

1. **输入 ID**: 在本子 ID 输入框中输入要下载的本子或章节 ID
2. **设置路径**: 确认下载保存路径（默认为程序目录下的 JMDownLoad 文件夹）
3. **高级选项**: 根据需要设置图片格式、并发数等选项
4. **开始下载**: 点击"开始下载"按钮开始下载

### 支持的 ID 格式

- 本子 ID: `422866`
- 章节 ID: `p456`
- 多个 ID: `422866,422867` 或 `422866 422867`

## 功能说明

### 搜索功能
点击搜索框输入关键词，点击"搜索"按钮查看搜索结果，可选择本子添加到下载列表。

### 排行榜
点击"查看排行榜"按钮查看日、周、月排行榜，可选择本子添加到下载列表。

### 封面预览
输入本子 ID 后点击"显示封面"按钮查看封面，点击封面可放大查看。

### 界面风格
点击"界面风格"按钮选择喜欢的主题风格。

## 配置文件

程序会在运行目录下生成以下配置文件：

- `style_cache.json`: 保存界面主题设置
- `JMDownLoad/`: 默认下载目录

## 开发

### 项目结构

```
JMComic/
├── jmcomic_gui.py      # 主程序文件

```

## 依赖库

- [ttkbootstrap](https://github.com/israel-dryer/ttkbootstrap) - 现代化 Tkinter 主题
- [Pillow](https://python-pillow.org/) - 图像处理库
- [jmcomic](https://github.com/hect0x7/JMComic-Crawler-Python) - JMComic 爬虫库

## 免责声明

本项目仅供学习交流使用，下载的漫画内容版权归原作者所有，请尊重版权，合理使用。
