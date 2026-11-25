# 使用 Claude Code CLI 获取额度

Claude 额度后台读取方案需要使用 Anthropic 官方 **Claude Code CLI**。CLI 使用自己的 Claude 登录，codexU 读取它返回的额度百分比和重置时间。首次使用时，应在 CLI 登录与 Claude Desktop 相同的账号；有多个组织时也应核对当前组织。

本说明对应 2026-11-12 的验证工作。当前发布版 0.6.7 使用 Desktop 会话查询结果或本地历史缓存；CLI 后台读取正在验证，尚未接入发布版的自动刷新。

## 在 codexU 检查登录

当前源码在「设置与诊断 → 数据源」提供 Claude CLI 登录状态。打开设置时自动检查，完成外部登录后点击「重新检查」更新结果。检测在后台调用官方 CLI 的 `auth status`，界面显示检查时间和所选 CLI 来源。

- 已登录表示该 CLI 返回有效登录状态。使用 Claude 订阅账号、API key 或第三方平台时，认证方式会分别标出。
- 未登录时，使用官方 CLI 执行下文的登录命令。
- 未找到 CLI 时，按官方说明安装，或确认 Desktop 内嵌 CLI 可用。
- 检查失败时，可重新检查；超时或格式异常会保留为未知状态。

检测优先使用本机独立安装的原生 CLI，然后查找 PATH 中的原生可执行文件，最后检查 Desktop 内嵌版本。当前检测入口支持原生可执行文件，npm 的 `.cmd` 或 `.bat` 启动脚本不在自动查找范围。CLI 和 Desktop 的登录状态需要分别确认。此处的登录指示用于验证认证状态，额度读取仍须单独验证。状态检测不会打开浏览器、发起模型请求或修改设置。

## 准备官方 CLI

在 PowerShell 中检查：

```powershell
claude --version
```

若系统找不到 `claude`，可按 [Claude Code 官方安装说明](https://code.claude.com/docs/en/setup)安装。Claude Desktop 的内嵌 CLI 也可用于本机验证，但其安装路径会随 Desktop 更新而改变，应先确认实际可执行文件。

若使用已确认路径的 `claude.exe`，可在 PowerShell 中将以下命令里的 `claude` 替换为 `& '已确认的完整路径\claude.exe'`。

## 登录与确认

通过官方 CLI 登录 Claude 订阅账号：

```powershell
claude auth login --claudeai
```

按照官方授权页完成登录，并选择与 Desktop 相同的账号。登录由 Claude CLI 处理，codexU 接收查询输出。随后检查登录状态：

```powershell
claude auth status
```

该命令只读取登录状态，正常登录时退出码为 0。Desktop 已登录时，独立启动的 CLI 仍可能显示未登录。[官方 CLI 命令说明](https://code.claude.com/docs/en/cli-reference)

## 验证后台读取

登录完成后，先进行一次后台只读查询，确认：

- 返回的是所选账号／组织的订阅额度。
- 通用额度窗口包含实际百分比和服务端重置时间；缺失窗口保持未知。
- 与 Desktop 用量页面的同范围读数一致。
- 查询过程没有发送模型提示词或产生模型调用。

本机原版 CLI 2.1.321 已确认识别 stdin 的 `get_usage` 控制查询；登录前返回 `rate_limits_available=false`，额度和订阅类型均为空。该控制协议未在公开 CLI 文档中形成稳定合同，应按实验性能力验证版本兼容性。

官方 SDK 另提供实验性 `/usage` 结构化报告，满足认证条件时包含 `percent` 和 `resets_at`。查询返回成功、空结果或没有权限是不同状态，应分别处理。[SDK 用量报告](https://code.claude.com/docs/en/agent-sdk/typescript#sdkusagereport)

登录后的验证结果记录在 [后台读取方案核查](research-claude-background-usage-2026-11-12.md)。验证通过后，才可将查询接入 codexU 的定时刷新，并加入缓存、超时和查询限流时的退避。

## 数据范围

CLI 查询用于额度百分比和重置时间。实际订阅月费仍由用户在 codexU 设置中填写，套餐标签仅作为参考信息。采样时间和原始来源需要随结果保留，以便界面标注新鲜度。

既有 Desktop 会话查询方式见 [Claude Desktop 用量来源与限制](claude-desktop-usage.md)。
