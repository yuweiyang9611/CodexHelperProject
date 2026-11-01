# Claude Desktop 实时额度与重置时间：官方通道调研

核对日期：2026-11-01（Asia/Tokyo）。资料研究浏览官方文档及其链接的维护者仓库；随后使用未修改的本机 CLI 进行了下文记录的只读控制协议验证。未提取原始凭据、发送模型提示词或修改产品代码。下列页面均已实际打开。

## 结论

本轮找到值得优先验证的新通道：新版 Claude Code 的 `/usage` 可以通过 Agent SDK 消息返回结构化 `usage_report`，包含服务端额度百分比和重置时间。它比只含百分比的 Desktop 历史缓存更适合本项目，但还需要验证本机版本、输出模式和账户一致性。当前 Desktop 界面的 Settings → Usage 则是已明确支持的直接查看通道。[SDKUsageReport](https://code.claude.com/docs/en/agent-sdk/typescript#sdkusagereport)、[官方用量限制说明](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)

本轮没有找到 `https://api.anthropic.com/api/oauth/usage` 面向第三方的官方公开接口合同：精确路径检索官方站点无匹配，官方 API 总览也没有列出它。不能将社区调用方式或本机源码中的路径当作官方支持承诺；本轮没有请求该端点。[官方 API 总览](https://platform.claude.com/docs/en/api/overview)

## 1. 新候选：`/usage` 的结构化结果

官方 TypeScript reference 列出 `SDKUsageReport`，Agent SDK v0.3.273 起导出该实验性类型。`/usage` 的结果位于 `SDKAssistantMessage.usage_report`；文档特别说明它不是 `SDKLocalCommandOutputMessage`。[SDK 类型与本地命令输出](https://code.claude.com/docs/en/agent-sdk/typescript#sdkusagereport)

相关字段：

| 路径 | 含义 |
| --- | --- |
| `usage_report.rate_limits.limits[].kind` | 服务端窗口种类，例如 `session`、`weekly_all`、`weekly_scoped` |
| `group` | 服务端分组，例如 `session`、`weekly` |
| `percent` | 已用百分比，0–100 |
| `resets_at` | ISO 8601 时间或 `null` |
| `scope` | 可选模型／产品表面范围 |
| `severity`、`is_active` | 服务端展示级别及单值指示器选择；旧版本可能缺少 |

`rate_limits=null` 表示无法取得套餐用量，例如会话 OAuth 缺少 `user:profile`；`limits=[]` 表示服务端返回零个窗口，两者不能都当成 0% 使用。按 `kind` 分类，不按显示文字或数组位置猜测。`extra_usage` 是另购用量额度的消费，不是订阅月费。[SDK 字段说明](https://code.claude.com/docs/en/agent-sdk/typescript#sdkusagereport)

建议下一步在未修改的官方 CLI 上验证下列候选命令，检查输出中的 assistant 消息；这是根据官方 print/stream-json 参数与 SDK 本地命令说明组合的验证方案，本轮没有执行，也未证明所有版本都支持该组合：

```powershell
claude -p "/usage" --output-format stream-json --verbose --no-session-persistence
```

CLI reference 支持这些输出参数，`--no-session-persistence` 禁止保存会话。但启动 CLI 仍可能加载本机设置、hooks、MCP；不能把“查询命令不修改订阅”理解为整个进程绝无启动副作用。不要用普通自然语言提示作为失败后的自动替代，以免变成模型请求。[CLI reference](https://code.claude.com/docs/en/cli-reference)

## 2. 事件、statusLine 和遥测的区别

| 通道 | 可得到什么 | 实时性与适用边界 |
| --- | --- | --- |
| SDK `rate_limit_event` | `status`，可选 `resetsAt`、`utilization` | 会话遇到限流时发出的事件，不是主动查询所有窗口的接口；已列出的类型没有完整 5h/7d 行列表。数值字段单位应另外核实，不能套用其他通道的单位 |
| statusLine stdin | `rate_limits.five_hour/seven_day.used_percentage`、`resets_at`（Unix 秒） | 合格订阅首个 API 响应后出现；窗口可以独立缺失。刷新脚本不等于强制刷新服务端用量 |
| OpenTelemetry | 会话／请求 Token、成本、耗时、错误和工具事件 | 官方指标列表没有个人订阅余量百分比或窗口重置指标；不能由累计 Token 反推出额度 |

SDK 事件依据：[SDKRateLimitEvent](https://code.claude.com/docs/en/agent-sdk/typescript#sdkratelimitevent)。statusLine 依据：[字段与触发条件](https://code.claude.com/docs/en/statusline)。遥测依据：[Monitoring](https://code.claude.com/docs/en/monitoring-usage)。

OTel 的 `api_error.status_code` 可以反映请求失败，但错误不提供完整套餐窗口。原始请求／响应 body 日志涉及会话内容，也不是已文档化的订阅额度查询方法；本项目无需为了获取额度启用它。[遥测事件说明](https://code.claude.com/docs/en/monitoring-usage#available-metrics-and-events)

## 3. Desktop 账户、界面和扩展

同一账户在 claude.ai、Claude Code 和 Claude Desktop 的使用共享套餐限制。Settings → Usage 提供当前使用量及下一次重置时间；五小时窗口与固定每周窗口规则不同，已验证 Ultra 账户可没有五小时限制。读取该界面是查看操作，不需要调用聊天模型。UI 自动化是可评估的回退方式，但布局、语言、登录态和窗口可见性会影响稳定性。[用量与重置规则](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)

Desktop Code 模型选择器旁的 usage ring 也显示套餐用量。Desktop 使用独立更新的内嵌 CLI；终端 CLI 与它并非同一个版本。仅发现两者安装在同一台电脑，或共享 `.claude` 配置，不能证明所查询的账户／组织一致。结构化 `/usage` 只有在已确认同一登录账户和组织时，才可当作 Desktop 对应额度。[Desktop reference](https://code.claude.com/docs/en/desktop#check-usage)

Desktop 扩展是打包的本地 MCP server，提供扩展自身的工具、资源与用户配置；远程 MCP 由 Claude 连接外部服务。本轮查阅的官方说明及 MCPB 文档未提供“读取宿主当前订阅额度／重置时间”的特权接口。因此扩展可以包装已有采集方法，却不会凭安装自动获得 Desktop 的账户额度或认证令牌。[Desktop 扩展说明](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop)、[MCPB 维护者文档](https://github.com/modelcontextprotocol/mcpb)、[远程连接器说明](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp)

## 4. OAuth 与组织 API 不能混淆

`user:profile` 在 SDK 用量报告说明中是能否得到套餐用量的条件之一，不是向第三方授予任意调用 `/api/oauth/usage` 的公开许可。官方说明要求 Claude 账户登录走 Anthropic 自身流程，并区分原版 Claude Code 的终端用户登录与第三方产品收集、保存或中转用户订阅凭据。本项目应优先读取原版 CLI 的非秘密输出，不导出 token。[认证与凭据使用说明](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use)

Claude Platform 的 Usage and Cost Analytics API 返回 Console／Enterprise 组织历史 Token 和费用，需要相应管理凭据；普通 API 的 `anthropic-ratelimit-*-reset` 描述 API 请求／Token 速率限制。这两者都不能替代个人订阅的五小时／每周额度。[组织 Analytics API](https://platform.claude.com/docs/en/manage-claude/usage-cost-analytics-api)、[API 速率限制](https://platform.claude.com/docs/en/api/rate-limits)

## 建议验证顺序

1. 验证官方 CLI `/usage` 是否输出 `assistant.usage_report.rate_limits`，核对版本、登录账户和组织；不访问原始凭据。
2. 与当前 Desktop Settings → Usage 的百分比和重置时间对照。只映射无 scope 的通用窗口；模型专属、表面专属及额外消费单独处理。
3. 结果为空、失效或账户无法对应时，保留现有 Desktop 历史百分比回退，仍不生成重置时间。记录每个来源的真实获取时间，不能把旧缓存标成实时。
4. 不把 SDK 限流事件、OTel Token 或管理员统计冒充个人套餐实时查询。若只能经 UI 获取，明确它是界面采集而非稳定公开 API。

## 本机控制协议验证

已重新确认 Desktop 内嵌 Claude Code 2.1.309 支持 JSON 流、严格 MCP 配置、安全模式及关闭会话保存。官方 `auth status` 返回未登录、`authMethod=none`；这不表示 Desktop 自身没有登录，只表示独立启动的 CLI 没有同一认证上下文。

在隔离工作目录中，以未修改的 CLI、`--safe-mode`、`--strict-mcp-config` 和 `--no-session-persistence` 启动 print/stream-json 模式，只发送一条控制帧，没有发送 `type=user` 消息：

```json
{"type":"control_request","request_id":"quota-research-read","request":{"subtype":"get_usage","skip_behaviors":true}}
```

本机进程正常退出，收到匹配 request_id 的成功 `control_response`；规范化结果为 `rate_limits_available=false`、没有 `rate_limits`、没有订阅类型。原始响应、账户标识和凭据未输出或存档。

这确认了该原版二进制识别控制查询；**没有证明已取得当前 Desktop 账号的实时额度**。要完成这条路线，还需用户通过 Claude 原版登录流程登录同一账号，并对照账号／组织及窗口范围。实验控制协议的源码依据见 [开源实现核对](research-claude-realtime-implementations-2026-11-01.md)，它与公开文档的 `/usage → usage_report` 形状不同，不能混用。

对当前 Desktop-only 使用方式，还有无需额外 CLI 登录的候选：让已有 Code 会话调用它自带的 `mcp__ccd_session_mgmt__get_usage`；已从本机分发代码核实该工具会使用 Desktop 自身的认证并返回服务端重置时间，但当前会话可用性尚待实际调用确认。见 [本地静态核查](research-claude-desktop-realtime-local-2026-11-01.md)。

这些结果没有改变已发布应用的采集逻辑，也不能被描述为实时数据已经接通。
