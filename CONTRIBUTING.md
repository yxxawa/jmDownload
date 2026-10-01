# 贡献指南

感谢你愿意改进 JM Download。提交问题或 Pull Request 前，请先阅读仓库 README 中的功能边界、开源许可状态和内容使用说明。

## 本地验证

1. 使用 Windows 与 .NET SDK 9。
2. 执行 `./scripts/build.ps1 -Configuration Release`。
3. 涉及 WPF/WebView2 或嵌入资源的改动，应运行 `./scripts/publish.ps1 -RuntimeIdentifier win-x64` 和 `./scripts/selfcheck.ps1`。
4. 涉及 GUI 生命周期的改动，应在真实 Windows 桌面手动启动验证；headless 自检不等同于窗口测试。
5. 涉及上游请求的改动，应使用获得授权的测试方式，不要提交真实账号、令牌、Cookie、私人下载或受版权保护的内容。

## Pull Request 建议

- 一个 PR 聚焦一个问题，说明现象、预期、实现与验证方式。
- 保留原有用户数据兼容；涉及配置或持久化格式时，说明迁移策略。
- 不提交 `bin/`、`obj/`、`release/`、`artifacts/`、个人配置、日志或下载内容。
- 不在日志、截图、提交信息或测试夹具中暴露令牌、Cookie、个人路径或真实用户数据。
- UI 修改请附上不含个人信息的截图；网络功能修改请说明是否触发了上游请求。

## 安全问题

不要在公开 issue 中发布可复用的凭据、访问令牌、个人数据或未修复安全问题的利用细节。联系维护者的安全报告渠道需要在仓库公开前补充到本文件。