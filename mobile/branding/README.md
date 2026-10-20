# 橙奈品牌资源

Android 1.3.1 使用日系萌系角色「橙奈」，图标为 Q 版头像。统一使用栗棕发、琥珀眼和橙色书签发夹，浅色与深色界面共享透明角色资源。

- `sources/mascot-welcome.png`：开屏欢迎立绘。
- `sources/mascot-reading.png`：最近阅读的空书架插画。
- `sources/launcher-chibi.png`：Q 版图标原图。
- `sources/mascot-home.png`：首页继续阅读入口。
- `sources/mascot-search.png`：搜索引导和没有搜索结果。
- `sources/mascot-collection.png`：收藏、书签空状态。
- `sources/mascot-download.png`：下载入口、下载队列、下载清单和本地作品。
- `sources/mascot-history.png`：下载历史空状态。
- `sources/mascot-retry.png`：网络异常、书架失败和阅读失败。
- 同目录的 `.prompt.txt` 保存绘制提示词。

原图不嵌入 APK。`build-assets.py` 从原图提取透明背景、统一欢迎立绘的发夹朝向，并输出压缩 WebP 与 Android 原生位图。使用完整服装和轻量装饰，书籍内容区保持紧凑。

重新导出资源（需要 Pillow）：

```powershell
python mobile/branding/build-assets.py
node mobile/frontend/icons/build-icons.mjs
```

常规 APK 构建会自动根据 `mobile/frontend/icons/brand-avatar.png` 生成各密度启动图标；无需再次调用绘图 API。启动画面由 `BrandSplashView.cs` 绘制，尊重系统动画设置，在前端首个可用画面就绪后淡出；不使用倒计时。

首页使用 48px 的小角色图，大插画用于暂无内容或加载失败的状态。装饰图片使用空替代文字，操作标签保留文字语义。阅读正文与书籍封面不增加装饰。所有资源打包在应用内，可离线显示。
