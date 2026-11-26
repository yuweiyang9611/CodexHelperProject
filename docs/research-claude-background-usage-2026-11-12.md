# Claude 额度后台自动读取方案核查

核查日期为 2026-11-12（Asia/Tokyo）。本记录复查官方文档，并纳入本轮的后台只读控制协议测试。未发送模型提示词、读取凭据或操作桌面。此前 2026-11-01 的实验另见 [原有调研](research-claude-realtime-official-2026-11-01.md)。

## 可行方向

优先验证由原版 Claude Code CLI 使用其自身登录读取额度，再由 codexU 定时消费非秘密结果。官方文档确认 `/usage` 的 assistant 消息可带实验性 `usage_report`，其中服务端窗口有 `percent` 和 `resets_at`。`rate_limits=null` 表示没有取得套餐用量，空窗口数组表示服务端没有返回窗口。该结构不是订阅月费账单。[SDKUsageReport](https://code.claude.com/docs/en/agent-sdk/typescript#sdkusagereport)

这是一条需要本机实测的集成方向。文档列出了 CLI 的 print、stream-json 和关闭会话保存选项，也列出 `auth status` 的登录状态输出；没有因此证明 Desktop 的登录能供独立 CLI 复用。接入前必须核对同一账号与组织，并确认本机 CLI 实际返回两个通用窗口及真实重置时间。[CLI reference](https://code.claude.com/docs/en/cli-reference)

本次复查公开 CLI 和 SDK reference 未找到 `get_usage` 控制帧合同，因此该通道仍应标为实验性实现细节。[SDK reference](https://code.claude.com/docs/en/agent-sdk/typescript)

## 本机后台复测

本轮使用 Desktop 内嵌原版 Claude Code `2.1.321`。`auth status` 返回 `loggedIn=false`、`authMethod=none`。只发送 `subtype=get_usage`、`skip_behaviors=true` 的控制请求后，收到成功 `control_response`，但 `rate_limits_available=false`，没有 `rate_limits` 或订阅类型。输出消息只有 `control_response`，测试未发送 `type=user` 提示或模型请求。原始账号字段和凭据未写入此记录。

这确认新版二进制仍识别该控制查询，独立启动时依然缺少可用的同账号认证上下文。尚未证明登录后能读到 Desktop 对应账户的两个通用窗口。不得把“控制请求成功”展示为“实时额度接通”。

## 登录后复测（2026-11-26）

用户通过官方流程 `auth login --claudeai` 登录 Desktop 6.28899.6.0 内嵌的原版 Claude Code `2.1.330` 后复测。`auth status` 返回 `loggedIn=true`、`authMethod=claude.ai`，退出码为 0。CLI 登录的组织与 Desktop 本地用量历史中唯一的组织一致；比较只在内存中进行，组织标识未输出或存档。

在隔离的空工作目录中，以 `-p --input-format stream-json --output-format stream-json --verbose --strict-mcp-config --no-session-persistence --safe-mode` 启动，只写入一条 `subtype=get_usage`、`skip_behaviors=true` 的控制帧。两次查询均在约 0.7–1.1 秒内收到匹配 request_id 的成功 `control_response`，进程退出码为 0，stdout 没有其他消息，stderr 为空。响应中的会话统计为 `total_cost_usd=0`、`total_api_duration_ms=0`，确认没有模型调用。

响应 `rate_limits_available=true`，`subscription_type` 为订阅套餐。`rate_limits.limits[]` 按 `kind` 返回 `session`（五小时）、`weekly_all`（七天、全部模型）和 `weekly_scoped`（按模型的七天窗口，`scope.model` 有值）。每项含整数 `percent`、`severity`、`is_active`、`group`，以及 ISO 8601 带时区偏移的 `resets_at`（UTC，微秒精度）；通用窗口的 `scope` 为 null。响应同时保留旧形状 `five_hour`／`seven_day.utilization` 和大量内部代号字段（本次均为 null），不应依赖。响应不含账号邮箱或组织标识。

同一时刻对照 Desktop Code 会话内建的 `get_usage` 工具：三个窗口的百分比完全一致，重置时间精确到秒一致，只有亚秒部分不同。真实百分比和会话标识未写入本记录。

结论：本机后台 CLI 路线已接通，满足 [CLI 使用说明](claude-cli-usage.md) 列出的四项验证条件。该控制帧仍是没有公开合同的实验接口，接入时需按版本容错：只映射 `scope` 为 null 的 `session` 与 `weekly_all`；未知 `kind`、缺失窗口和空 `resets_at` 保持未知；以查询完成时间作为采样时间；超时、非零退出与 `rate_limits_available=false` 分别处理。

## 自动化方式与限制

本机分发代码的只读复查对象为 Desktop 5.1231.2.0，内嵌 CLI 为 2.1.321。沿上一轮已识别模块核对，Code 会话内的工具仍受 `sessionType=ccd` 限制，SDK 实验入口仍发送 `get_usage` 控制帧。Desktop 的 PlanUsage preload 只暴露触发刷新的 `nudgeRefresh`，所追踪实现没有返回额度的 getter。历史缓存仍为 version 2 的 `t/org/u` 百分比结构。这些是当前分发实现的观察，不是公开接口合同；核查未操作窗口或读取凭据。

| 路线 | 可以确认的能力 | 适合本项目的结论 |
| --- | --- | --- |
| 原版 CLI 的结构化 `/usage` | 官方声明 assistant 消息携带报告，类型实验性 | 首选验证方向；成功后可由普通后台计时器调用，需缓存、超时和退避 |
| CLI 的 stdin `get_usage` 控制帧 | 原有本机实验确认识别，当前公开文档无合同 | 可作版本受控的候选；需另测登录、窗口数据和无模型调用 |
| Desktop Scheduled tasks | 按时启动新 Code 会话，可访问本地文件 | 可以消除手动发起步骤；它执行任务提示，不能承诺查询全程无模型消耗 |
| `/loop` / CronCreate | 自动排队会话提示；内建命令会作为纯文本交给 Claude | 不应把 `/loop /usage` 当成直接执行本地查询的无模型定时器 |
| statusLine 快照 | 可输出五小时、七天百分比和重置 Unix 秒 | 可供消费；字段在订阅会话首个 API 响应后出现，窗口可独立缺失 |
| 管理员 Analytics 或 API rate-limit headers | 组织历史用量/费用，或 API 速率限制 | 不能替代个人订阅窗口的实时额度 |

调度能力依据 [Desktop scheduled tasks](https://code.claude.com/docs/en/desktop-scheduled-tasks)、[/loop 与调度限制](https://code.claude.com/docs/en/scheduled-tasks)。statusLine 字段与首次响应条件依据 [statusLine](https://code.claude.com/docs/en/statusline#available-data)。组织统计及 API 限制依据 [Analytics API](https://platform.claude.com/docs/en/manage-claude/usage-cost-analytics-api)、[API rate limits](https://platform.claude.com/docs/en/api/rate-limits)。

## 凭据与轮询边界

官方要求 Claude 账号登录通过 Anthropic 自身流程完成，并明确限制第三方收集、保存或中转 Claude 订阅凭据。实现应让未修改的官方 CLI 处理自身登录，codexU 只接收正常输出；不把 Desktop 的 Cookie 或 OAuth token 提取到 codexU。[认证与凭据说明](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use)

额度查询本身也可能限流。官方 `/usage` 可以显示过去 60 分钟内的最后已知读数，并提示其年龄和稍后重试时间。后台实现必须区分新结果与旧缓存，在查询受限时退避；不能把轮询时间当成服务端采样时间。[查询失败行为](https://code.claude.com/docs/en/costs#when-the-usage-request-fails)

本次没有找到供外部应用直接读取 Desktop 当前 quota provider 的官方稳定 getter，也没有找到 `/api/oauth/usage` 面向第三方的公开 API 合同。这是本轮文档检索的范围结论，不能据此断言未来或所有内部版本均无此能力。[Desktop 用量入口](https://code.claude.com/docs/en/desktop#check-usage)、[公开 API 总览](https://platform.claude.com/docs/en/api/overview)
