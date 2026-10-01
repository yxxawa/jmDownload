# GitHub 发布前检查清单

- [ ] 确认仓库名称、简介、截图及程序名称不会暗示与第三方平台官方关联。
- [ ] 确认所有源码、`icon.ico`、`assets/app-preview.png` 及 NuGet 依赖均可公开发布，并记录来源/许可证。
- [ ] 由代码权利人选择并添加 `LICENSE`；公开仓库但缺少许可证时，不要声称他人可自由复制或再发布。
- [ ] 检查 Git 历史、提交、分支和 Actions 日志，确认没有令牌、Cookie、私人下载、个人路径或本机数据。
- [ ] 确认 `.gitignore` 排除了 `bin/`、`obj/`、`release/` 和 `artifacts/`。
- [ ] 使用 `SmokeTest=false` 构建 GUI；明确区分 `NativeBackendSmoke` 与桌面应用入口。
- [ ] 在 Windows x64 上完成构建、发布包自检和普通 GUI 启动检查。
- [ ] 若宣称支持 ARM64，另行验证 ARM64 目标发布包；交叉发布成功不等于在 ARM64 设备上完成实机验证。
- [ ] 检查 WebView2 Evergreen Runtime 的安装说明和启动错误提示。
- [ ] 重新检查上游服务条款、所在地法规、内容版权与 API 使用许可；不要将程序描述为平台官方客户端。
- [ ] 目标框架迁移到仍受支持的 .NET 版本后，再制作长期维护承诺或正式 Release。
- [ ] 在 README 中写明实际验证过的 Windows 版本、架构、程序版本和已知限制。