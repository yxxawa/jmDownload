# 更新记录

## 1.1.0 · 2026-10-08

- 电脑端与手机端的作品详情增加收藏／取消收藏按钮。
- 下载清单单选时显示“收藏／取消收藏”，自动读取作品现有收藏状态；多选时显示“批量收藏”。
- 多选收藏后按钮变为“撤销操作”，只取消本次新增的收藏，操作前已有收藏会保留；部分失败时可继续处理剩余项。
- 收藏不依赖下载任务是否进行，操作会显示成功、失败数量和失败作品 ID。
- 最近阅读、收藏、书签、本地作品、下载记录、活动记录、搜索记录和下载清单增加管理、选择、全选及批量移除。
- 删除最近阅读记录时保留阅读进度、收藏、书签和下载文件；再次阅读后重新显示该作品。
- 删除书签只处理选中的具体页码；从本地书架移除作品会保留磁盘文件。
- 任务队列增加单项删除按钮，也允许批量删除已完成、失败或取消的任务；仍在下载或等待的任务受后端保护。
- 修复手机端任务筛选后的多选绑定错位，异常筛选中可正确删除失败／取消的任务；章节任务 ID 也可删除。
- 多选界面使用圆形选择标记、柔和选中背景和紧凑操作栏；切换筛选会清除上个筛选的选择。
- 下载记录删除后不会被同一轮任务重新添加；新的下载任务仍会正常生成记录。
- 清空与批量移除增加确认窗口，切换筛选时清除上一个分类的选择。
- 修复图片写入失败被并发流水线的取消异常覆盖，任务保留实际失败原因。
- 元数据缓存无法写入时继续返回已获取的内容；图片缓存目录不可写时改用本机临时缓存目录。
- 桌面端内置 WebP 解码和编码，支持 WebP 分段还原、PNG／JPEG 转换和 PDF 导出，不依赖用户安装系统 WebP 编解码器。
- 图片处理错误保留具体原因，损坏或不完整的 WebP 输入会被拒绝；旧的解码缓存与新版本隔离。
- 下载前检查目录是否可写，权限被拒绝时明确提示更换目录或授予安卓文件访问权限。
- 安卓版本号提升至 1.1.0（versionCode 2），网页界面缓存升级。

## 开发验证

后端离线自检使用临时数据目录：

```powershell
dotnet run --project ./DesktopShell.csproj -c Release -p:SmokeTest=true -- --collection-selfcheck
dotnet run --project ./DesktopShell.csproj -c Release -p:SmokeTest=true -- --webp-selfcheck
```

两端界面测试使用隔离浏览器和模拟数据，不连接上游内容服务：

```powershell
npm install --prefix .ui-test --no-audit --no-fund playwright
node ./scripts/check-collections.mjs
```

已完成电脑端和手机端的界面回归、后端离线自检，以及 Windows 真实禁写权限的原版／修复版对照测试。Android 安装包使用原签名密钥生成；未连接安卓实机或 ARM64 设备进行测试。

默认使用本机 Edge；可通过环境变量 `JM_TEST_BROWSER` 选择 Playwright 的其他浏览器通道。批量操作的共用界面位于 `frontend/bulk.js` 和 `frontend/bulk.css`，手机端保存同一实现的副本，修改时需同步两端。

Windows 权限回归使用临时目录的真实 NTFS 禁写 ACL，执行后恢复 ACL：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/check-storage.ps1
```
