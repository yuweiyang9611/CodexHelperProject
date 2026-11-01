# 2026-11-01 本地运行时兼容核对

核对日期：2026-11-01（Asia/Tokyo）。本次只读取安装目录、数据库字段和日志结构；没有启动模型请求、改写原始 transcript、读取凭据或将真实会话正文复制进仓库。下面的版本是本机样本，不代表官方最新稳定版本。

## 当前数据布局

| 来源 | 实际观察 | 处理 |
| --- | --- | --- |
| Codex CLI / 桌面内置运行时 | 近期 rollout 标记 `0.164.0-alpha.11.1`、`0.165.0-alpha.1.2`、`0.166.0-alpha.5.1` | 继续按字段识别，不以版本号拒绝日志 |
| Codex 状态数据库 | 仍使用 `state_5.sqlite`，包含当前 reader 读取的 `threads` 字段 | 保持只读查询，不猜测数据库升级版本 |
| Codex 可执行文件 | 独立安装的稳定 `bin` 目录及桌面应用的 hash `bin` 子目录仍存在 | 现有发现路径继续适用 |
| Claude Code 本地 transcript | 近期文件标记 `2.1.286`、`2.1.299`、`2.1.308`、`2.1.309`；仍在 `.claude/projects` 下 | 支持 assistant 消息用量及 5m / 1h 缓存写入字段 |

## Claude 每个响应只核算一次

当前 Claude transcript 会把同一个响应的 content blocks 分成多条 `assistant` 记录。这些记录共享 `message.id`，且每条都带完整响应的 `message.usage`。逐行累加会把同一次输入、缓存和输出重复核算。

reader 现在在同一来源内按 `message.id` 和模型合并用量，保留各条 content block 的工具/Skill 统计。无消息 ID 的旧格式仍按独立记录处理；不同 ID 即使数值完全相同，也各计一次。后续更完整的 usage 快照可更新同一响应的计数；总量相同时，字段更完整的缓存 TTL 拆分可以替换旧快照。响应归属第一次观察到它的时间。

索引版本由 4 升至 5，升级后重新读取仍存在的原始文件。历史账本继续保留逐行规范化观测，只增加消息 ID；重新解析的原始计数字段必须与账本旧行逐条一致，才能补入 ID 并重新投影。这允许纠正旧版的重复统计，同时保留改写/截断冲突检查。增量追加、索引复用和源日志删除后的历史重放使用同一去重规则。

如果原始日志已删除，旧账本没有消息 ID，无法可靠区分“重复 block”与“两个数值相同的真实响应”。这种历史保留原值并显示诊断；恢复原始 transcript 后刷新可重新核对。更早的整日日汇总仍是独立的历史估算，缺乏消息级证据时不能自动改写。

## Codex 新用量记录与计价边界

近期 rollout 同时出现 `token_usage_record` 和旧 `event_msg/token_count`。新记录包含每响应 `usage`、`turn_token_usage` 和 `thread_token_usage`；旧记录通常随后写入相同累计值。正在执行的会话可能先出现新记录，随后才出现旧通知。reader 已将新记录的 `usage` / `thread_token_usage` 接入单次 / 累计计数归一化，与旧通知共用状态，避免漏计和双计；模型仍由上下文确定，fork 重放继续排除父会话贡献。

Codex 索引版本由 14 升至 15。旧账本的解析结果如果无法直接按前缀升级，必须重新读取原日志对应字节前缀：先用旧规则重放并逐条匹配旧 Token 事件，再用新规则确认更新投影，才允许替换。原文件读不到、截断或出现不相关改写时继续保留旧账本并标记冲突。

新的用量字段包括 `cache_write_input_tokens`，但抽样记录没有缓存 TTL，不能直接映射到 Claude 的 5m / 1h 写入档位。本次保留普通输入等效估算，不伪造缓存写入计价明细；真实金额还可能受 Fast、长上下文、地区等日志未充分提供的条件影响。

## Claude Desktop 覆盖范围

此项目的 Claude 适配器读取本机 Claude Code transcript 和可选 statusLine 额度快照，不能据此代表整个 Claude Desktop 账户的所有用量。

Anthropic 说明自 2026-10-06 起，Pro、Max、Ultra 的新 Cowork 任务在云端执行，旧本机任务保留在原设备。因此 `.claude/projects` 没有数据，既不能推断 Claude Desktop 没有使用，也不能把云端 Cowork 当作已采集。[官方 Cowork 说明](https://support.claude.com/en/articles/15520349-use-claude-cowork-on-web-desktop-and-mobile)

额度仍需明确的数据来源；statusLine 提供的 `rate_limits` 可用于额度快照，HTTP 429 仅表示当时被限流，不能推算当前剩余额度。[Claude Code statusLine 文档](https://code.claude.com/docs/en/statusline)

## 回归验证

`ClaudeMessageUsageTests` 覆盖重复 content blocks、独立响应相同数值、缺少 ID 的旧日志、增量追加、重启索引复用、后续 usage 补全、缓存 TTL 富化、源删除后的保留，以及旧账本在原日志恢复后的自动校正。`CodexTokenUsageRecordTests` 覆盖新旧双写、只有新记录的尾部、增量读取、模型归属、fork 重放和旧账本迁移。统一构建与测试结果由本次更新验收记录给出。
