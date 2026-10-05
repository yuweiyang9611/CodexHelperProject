# CodexU 0.6.2

修复安装版检测到更新后，下载 GitHub 附件时出现 `Redirect was cancelled` 的问题。

- 更新下载继续使用 Chromium 网络栈，通过手动重定向适配器逐跳验证 GitHub 发布源及附件 CDN。
- 保留流式下载、取消、大小与 SHA-256 校验，以及 API 令牌只发送给固定 GitHub API 的约束。
- 新增真实 Electron 网络回归，覆盖本地 302、目标白名单、流式下载、取消及失败，接入 CI 和 Release。

**0.6.0／0.6.1 用户需要手动升级一次**：旧版自动下载链路本身有此问题，无法靠该链路获取修复。请从 [0.6.2 发布页](https://github.com/yuweiyang9611/CodexHelperProject/releases/tag/v0.6.2) 下载安装包，退出原应用后覆盖安装；用量数据和设置保留。升级完成后，可继续使用自动更新。

便携版仍需解压完整 ZIP 后运行。详细原因与验证记录见 [重定向修复说明](updater-redirect-fix.md)。
