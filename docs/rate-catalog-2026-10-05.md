# 模型与费率更新：2026-10-05

核对日期：2026-10-05（Asia/Tokyo）。内置目录升级为 `2026.10.1`，目录发布日期为 2026-10-05，共 37 行。每个模型按自己的官方发布或计费日期生效。

## 本轮新增

美元价格单位为每百万 Token；应用使用 1 美元 = 25 点。

| 模型 | 生效日期 | 输入美元 | 缓存读取美元 | 输出美元 | 输入／缓存读取／输出点数 | 官方依据 |
| --- | --- | ---: | ---: | ---: | --- | --- |
| gpt-6.1-sol | 2026-09-29 | 2 | 0.10 | 10 | 50／2.5／250 | [模型页](https://developers.openai.com/api/docs/models/gpt-6.1-sol)、[发布记录](https://developers.openai.com/api/docs/changelog) |
| gpt-rosalind-research | 2026-10-05 | 5 | 0.50 | 25 | 125／12.5／625 | [发布及计费记录](https://developers.openai.com/api/docs/changelog) |
| claude-fable-5-1 | 2026-09-01 | 10 | 0.25 | 50 | 250／6.25／1250 | [模型页](https://platform.claude.com/docs/en/models/fable-5-1/overview) |
| claude-mythos-5-1 | 2026-09-01 | 10 | 0.25 | 50 | 250／6.25／1250 | [模型页](https://platform.claude.com/docs/en/models/mythos-5-1/overview) |
| claude-opus-5-5 | 2026-09-22 | 4 | 0.20 | 20 | 100／5／500 | [模型页](https://platform.claude.com/docs/en/models/opus-5-5/overview) |
| claude-sonnet-5-5 | 2026-09-28 | 2 | 0.20 | 10 | 50／5／250 | [模型页](https://platform.claude.com/docs/en/models/sonnet-5-5/overview) |

OpenAI 新行版本为 `2026.10.1`，Claude 新行为 `anthropic-2026.10.1`。版本号是应用目录批次标签；模型的真实生效日见上表。

## Sonnet 5 目录纠正

Anthropic 于 2026-08-10 取消了原定 9 月 1 日的 Sonnet 5 涨价，2／0.20／10 美元成为标准价格。[官方取消记录](https://platform.claude.com/docs/en/release-notes/overview#august-10-2026)、[Sonnet 5 模型页](https://platform.claude.com/docs/en/models/sonnet-5/overview)

删除旧目录中从未实际生效的 2026-09-01、75／7.5／375 点行，保留原始 50／5／250 点行。这是错误目录修正，不描述为先涨价再降价；当前内置目录会正确重算 9 月以来的明细。其余真实历史行保留各自日期、来源和版本。

用户自定义费率及固定导入目录仍按自身内容核算，不被静默改写。若曾固定包含错误 Sonnet 5 行的旧目录，可在设置中恢复内置目录或手动修正该行。

## 核算约定

- GPT-6.1 Sol 与 GPT-6 Sol 保持独立。后者仍按原 0.20 美元缓存输入价计价；未知型号和猜测后缀不映射为新模型。实际旧模型名称也不会因退役公告改成替代模型。
- GPT-Rosalind 的官方计费起日是 2026-10-05，不把 2026-09-08 发布日当作计费起日，也不猜测此前的零费率。
- 沿用 OpenAI Standard 短上下文等效金额口径；Astra 的 Ultrafast 是处理档位，不覆盖 Standard 行。缺少档位、单次上下文长度或模态证据时不猜测倍数。
- Claude 新模型分别使用明确的缓存读取价格。Fable/Mythos 5.1 为输入价的 0.025 倍，Opus 5.5 为 0.05 倍；5 分钟／1 小时缓存写入继续使用基础输入价的 1.25／2 倍。[Anthropic 缓存定价](https://platform.claude.com/docs/en/about-claude/pricing)
- Codex 本机日志仍缺少 OpenAI 缓存写入拆分，继续随普通输入估算；不从 API 已公布的缓存写入单价伪造日志明细。
- Daybreak、GPT-5.6 Sol 促销及其他旧费率保持经核实的历史定义。促销只确认至少持续至 2026-11-21，不预设终止价格。[OpenAI Pricing](https://developers.openai.com/api/docs/pricing)

逐条调研依据见 [OpenAI 核对记录](model-update-openai-research-2026-10-05.md) 和 [Anthropic 核对记录](model-update-anthropic-research-2026-10-05.md)。

## 验证

- .NET SDK 10.0.400：Core 231、Infrastructure 279、Sidecar 94、Contracts 11，共 615 项通过。新增测试先在旧目录捕获 18 项失败，覆盖缺失模型、取消涨价及目录身份，再验证全部通过。
- 新增回归覆盖六个模型的真实生效日、缓存读取、5 分钟／1 小时写入、自定义覆盖、固定目录、保留历史来源版本、留存用量重算与目录导入导出。
- Node 22.23.2：Web 198 项单元测试通过，生产构建通过；设置页 7 个专门场景验证新增与旧模型的价格自动填入。
- 完整 Playwright 矩阵 43 个有效场景通过，95 个重复组合按既有规则跳过；包含视觉、键盘、无障碍、缩放及设置。界面布局与截图基线未修改。
- 完整解决方案严格构建通过，含旧 WPF 宿主，0 警告、0 错误；完整格式检查与 `git diff --check` 通过。
- 独立源码复核确认后端实际 37 行，新增六行、删除 Sonnet 5 错误行，其余真实历史行的价格、日期、来源及版本不变。应用版本仍为 0.6.0。
