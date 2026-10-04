# Anthropic 模型与费率核对：2026-10-05

核对日期：2026-10-05（Asia/Tokyo）。此记录依据 Anthropic 官方模型页、价目及发布记录；用于本轮内置目录更新，不代表第三方云平台、订阅账单或特殊处理档位价格。

## 新模型

美元价格单位为每百万 Token，点数按项目现有的 1 美元 = 25 点换算。每行链接同时提供官方模型 ID、价格和发布日期。

| 模型 | 官方发布日期／建议生效日期 | 输入美元 | 缓存读取美元 | 输出美元 | 输入／缓存读取／输出点数 | 官方依据 |
| --- | --- | ---: | ---: | ---: | --- | --- |
| claude-fable-5-1 | 2026-09-01 | 10 | 0.25 | 50 | 250／6.25／1250 | [Fable 5.1](https://platform.claude.com/docs/en/models/fable-5-1/overview) |
| claude-mythos-5-1 | 2026-09-01 | 10 | 0.25 | 50 | 250／6.25／1250 | [Mythos 5.1](https://platform.claude.com/docs/en/models/mythos-5-1/overview) |
| claude-opus-5-5 | 2026-09-22 | 4 | 0.20 | 20 | 100／5／500 | [Opus 5.5](https://platform.claude.com/docs/en/models/opus-5-5/overview) |
| claude-sonnet-5-5 | 2026-09-28 | 2 | 0.20 | 10 | 50／5／250 | [Sonnet 5.5](https://platform.claude.com/docs/en/models/sonnet-5-5/overview) |

Mythos 5.1 仅对 Project Glasswing 参与者开放；这是已公布价格的独立模型 ID，历史用量核算可支持它，无须推测用户是否具备调用权限。[Mythos 5.1 模型页](https://platform.claude.com/docs/en/models/mythos-5-1/overview)

新 ID 都是 4.6 代之后的无日期固定版本。应精确注册，不能将 `claude-fable-5`、`claude-opus-5` 或 `claude-sonnet-5` 归一化到新版本；没有新的永久映射别名需要补充。[Model IDs and versioning](https://platform.claude.com/docs/en/about-claude/models/model-ids-and-versions)

## 缓存计价

5 分钟缓存写入仍为基础输入价的 1.25 倍，1 小时写入仍为 2 倍，现有项目常量无需改变。缓存读取不能再统一假设为基础输入价的 0.1 倍：Fable 5.1／Mythos 5.1 为 0.025 倍，Opus 5.5 为 0.05 倍，Sonnet 5.5 为 0.1 倍；目录必须存储上表的准确缓存读取价格。[Anthropic Pricing](https://platform.claude.com/docs/en/about-claude/pricing#prompt-caching)

新模型官方美元写入价格分别为 Fable 5.1／Mythos 5.1 的 12.50／20、Opus 5.5 的 5／8、Sonnet 5.5 的 2.50／4（5 分钟／1 小时）；与上述乘数一致。[Fable 5.1](https://platform.claude.com/docs/en/models/fable-5-1/overview)、[Mythos 5.1](https://platform.claude.com/docs/en/models/mythos-5-1/overview)、[Opus 5.5](https://platform.claude.com/docs/en/models/opus-5-5/overview)、[Sonnet 5.5](https://platform.claude.com/docs/en/models/sonnet-5-5/overview)

## 现有 Sonnet 5 行需要纠正

官方 2026-08-10 发布记录已取消原计划于 2026-09-01 生效的 Sonnet 5 涨价。首发输入／输出 2／10 美元成为标准价，3／15 美元从未实际生效。当前模型页也列出输入／缓存读取／输出 2／0.20／10 美元。[2026-08-10 发布记录](https://platform.claude.com/docs/en/release-notes/overview#august-10-2026)、[Sonnet 5 模型页](https://platform.claude.com/docs/en/models/sonnet-5/overview)

`UsageMath.cs` 核对前的 `anthropic-2026.09.1` 行从 2026-09-01 起按 75／7.5／375 点计算，是预告被取消后遗留的错误。纠正必须覆盖 2026-09-01 起的历史计算；只追加 2026-10-05 生效行会保留此前一个月的误计费。建议删除该从未真实生效的错误行，保留原 `anthropic-2026.07.1` 无日期首发行的 50／5／250 点及其他真实历史费率，无须另造同日或后日更正价格行。该删除是目录错误修正，不应描述成实际发生过的涨价再降价。

用户自定义费率和已固定的导入目录延续现有优先级；更正当前内置目录无需静默改写用户保存的历史目录。旧目录仍能按其自身数值复现旧计算，但不再作为官方现行价目的证据。

## 其他已有模型及目录建议

现行官方表中 Fable 5／Mythos 5 的输入／缓存／输出仍为 10／1／50 美元；Opus 5／4.8／4.7／4.6 为 5／0.50／25；Sonnet 4.6 为 3／0.30／15；Haiku 4.5 为 1／0.10／5，与项目现有行一致，无需新增价格变动历史。[Anthropic Pricing](https://platform.claude.com/docs/en/about-claude/pricing)

建议新增四个精确模型行并删除 Sonnet 5 错误涨价行。新增 Anthropic 行目录版本统一为 `anthropic-2026.10.1`，来源为 `Anthropic 官方 Claude API Standard 价目与发布记录`；这是本项目更新批次标签，不是厂商版本号。发布日期边界按上表配置。整体目录版本与 OpenAI 本轮更新一并决定。

最低验证范围：新模型生效日前无费率、生效日可计价；Fable／Mythos 和 Opus 的较低缓存读取价；5 分钟／1 小时写入；Sonnet 5 在 8 月末与 9 月起均为 50／5／250 点；旧模型 ID 不被映射到新模型；自定义与固定目录行为、目录导入导出及前端演示／编辑器费率同步。
