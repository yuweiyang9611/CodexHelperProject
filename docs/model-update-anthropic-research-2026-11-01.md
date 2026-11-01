# Anthropic 模型与费率核对：2026-11-01

核对日期：2026-11-01（Asia/Tokyo）。本记录用于本地用量的 API 等效金额估算。表内价格均为美元／百万 Token，点数仍按 1 美元 = 25 点换算。下列官方页面在核对日已实际打开；不依据搜索摘要更新费率。

## 本轮变化

| 模型 | 生效日期 | 输入 | 缓存读取 | 输出 | 5 分钟／1 小时缓存写入 | 输入／缓存读取／输出点数 |
| --- | --- | ---: | ---: | ---: | --- | --- |
| `claude-sonnet-5-5` | 2026-10-07 | 2 | 0.10 | 10 | 2.50／4 | 50／2.5／250 |
| `claude-haiku-5-5`，单次提示不超过 100,000 Token | 2026-10-07 | 0.10 | 0.01 | 0.50 | 0.125／0.20 | 2.5／0.25／12.5 |
| `claude-haiku-5-5`，单次提示超过 100,000 Token | 2026-10-07 | 0.50 | 0.05 | 2.50 | 0.625／1 | 12.5／1.25／62.5 |

Sonnet 5.5 缓存读取从 0.20 降至 0.10，输入、输出及缓存写入价格不变。必须保留 2026-09-28 起的旧价，并追加 2026-10-07 生效的新价，避免改写降价前用量。发布记录明确记载降价日及 Haiku 5.5 的发布日。[2026-10-07 发布记录](https://platform.claude.com/docs/en/release-notes/overview#october-7-2026)

Haiku 5.5 的官方 ID 是 `claude-haiku-5-5`，模型页同时给出发布日期和上表两档完整价格。[Haiku 5.5 模型页](https://platform.claude.com/docs/en/models/haiku-5-5/overview)

Haiku 的门槛按每次请求的全部输入计算，包括缓存读取和缓存写入。越过门槛后该请求的各 Token 类别使用较高价格；之前请求的价格不回溯改变。不能用每日、会话或月度累计输入判断是否越过门槛。本项目若仅保留按日期和模型聚合的 Token，应明确以短上下文标准价格估算，不能声称已完整重放分档账单。若未来实现精确分档，需在聚合前保留单次请求的档位，或保存各档位的独立 Token 分项。[官方长上下文计价规则](https://platform.claude.com/docs/en/about-claude/pricing#long-context-pricing)

## 其他现有费率

现行价表的输入／缓存读取／输出：Fable 5.1 和 Mythos 5.1 为 10／0.25／50；Fable 5 和 Mythos 5 为 10／1／50；Opus 5.5 为 4／0.20／20；Opus 5、4.8、4.7、4.6 为 5／0.50／25；Sonnet 5 为 2／0.20／10；Sonnet 4.6 为 3／0.30／15；Haiku 4.5 为 1／0.10／5。以上与项目上一批目录一致，不追加无实际价格变化的历史版本。5 分钟／1 小时写入仍为对应基础输入价的 1.25／2 倍。[官方价表](https://platform.claude.com/docs/en/about-claude/pricing)

建议本轮 Anthropic 新行使用 `anthropic-2026.11.1` 作为项目目录批次标签。保持精确 ID 匹配，不将 Haiku 4.5 映射为 Haiku 5.5；固定的旧目录和用户自定义行继续按用户内容核算。退役模型仍可能出现在历史用量中，不能仅因退役删除历史费率。

## Desktop、Code 和额度来源边界

2026-10-06 起，Pro、Max 和 Ultra 的新 Cowork 任务运行在云端；之前已开始的本机任务仍留在本机。因此，读取本机 Claude Code transcript 不能被描述为覆盖整个 Claude Desktop、统一聊天或云端 Cowork 的账户用量。文档中的云端变化也不能证明本机 transcript 的 JSON schema 已发生变化，解析器仍需本机结构证据和回归用例。[Cowork 在各端的运行方式](https://support.claude.com/en/articles/15520349-use-claude-cowork-on-web-desktop-and-mobile)

Claude Code status line 当前继续提供 `rate_limits.five_hour` 和 `rate_limits.seven_day`，其中 `used_percentage` 为已用百分比，`resets_at` 为 Unix 秒。字段在首个 API 响应前可缺失。新增的网关 `spend_limit` 属于消费金额限额，不能冒充 5 小时或 7 天订阅额度；其美元字段要求 Code 与网关 v2.1.284 或以上。已有桥接脚本应继续接受可缺失窗口并保留数据新鲜度。[Status line 字段说明](https://code.claude.com/docs/en/statusline)

10 月 29 日新增的 Usage and Cost Analytics API 面向 Claude Console 和 Enterprise 组织。官方已将先前 Usage and Cost API 标为 legacy，但仍支持。当前项目不调用这些组织管理接口，此变化不要求替换本地日志读取，更不能借个人桌面登录推断组织管理权限。[平台发布记录](https://platform.claude.com/docs/en/release-notes/overview#october-29-2026)

此处没有给 Claude Desktop 安装包填写“最新版本号”：公共产品发布说明和 Code CLI changelog 是不同版本线，不能互相替代。安装兼容性以本轮实际发现的本机版本与文件结构为准。

## 最小验证范围

- Sonnet 5.5 在 10 月 6 日按 0.20 缓存读取价核算，10 月 7 日起按 0.10；两侧输入、输出和缓存写入不变。
- Haiku 5.5 发布前无内置费率，发布日起短上下文费率正确；长上下文限制在界面和文档可见。
- 同名模型的多个价格版本按用量日期选择；用户自定义行、旧固定目录、导入导出出处不被新目录覆盖。
- 解析器改动使用脱敏构造样本，不把真实对话内容、凭据或个人目录写入测试及文档。
