# OpenAI 模型费率核对：2026-11-01

核对日期：2026-11-01（Asia/Tokyo）。对照 `2026.10.1` 目录，本轮目录版本为 `2026.11.1`。以下依据均为本轮实际打开的官方文档；新增行按发布日生效，原模型的历史价格和出处保持不变。

## 新增模型

价格单位为美元／百万 Token，采用 Standard 短上下文价格。

| 模型 | 发布／生效日期 | 输入 | 缓存输入 | 输出 | 应用点数：输入／缓存／输出 |
| --- | --- | ---: | ---: | ---: | ---: |
| gpt-6.1-terra | 2026-10-26 | 1 | 0.05 | 5 | 25／1.25／125 |
| gpt-6-rosalind | 2026-10-30 | 15 | 1.5 | 75 | 375／37.5／1875 |

两个发布日期由 [API Changelog](https://developers.openai.com/api/docs/changelog) 确认。Terra 价格在[模型页](https://developers.openai.com/api/docs/models/gpt-6.1-terra)与 [API Pricing](https://developers.openai.com/api/docs/pricing) 一致；Rosalind 价格由后者的生命科学模型表确认。`gpt-6-rosalind` 与 `gpt-rosalind-research` 是两个独立名称，后者保留 2026-10-05 开始的原计费行。

Terra 在 ChatGPT Work 与 Codex 的 Standard 点数表也列为 25／1.25／125 点，和应用固定换算 1 美元 = 25 点一致。账号可用性仍受套餐、工作区及地区限制。[ChatGPT Pricing](https://learn.chatgpt.com/docs/pricing)

## 现有价格与核算边界

- GPT-6 Astra、GPT-6.1 Sol、GPT-6 Luna 的 Standard 基准未变。GPT-5.6 Sol 的促销仍至少持续至 2026-11-21；没有明确终止日，不能预设次日涨价。[API Pricing](https://developers.openai.com/api/docs/pricing)
- API 于 2026-10-20 增加 12 小时缓存：写入价是基础输入价的 2.5 倍；30 分钟写入仍是 1.25 倍。这两个 TTL 不能套用 Claude 的 5 分钟／1 小时字段。本机新版 Codex 日志已有缓存写入总量，但缺少可靠的时长拆分，因此本次不伪造分档写入明细。[API Changelog](https://developers.openai.com/api/docs/changelog)、[GPT-6.1 Terra 模型页](https://developers.openai.com/api/docs/models/gpt-6.1-terra)
- Codex 的点数计费没有单独缓存写入费，API Key 用量才遵循 API 计价。因此这里显示的 Standard API 等效金额既不等于订阅额度，也不等于实际账单。[ChatGPT Pricing](https://learn.chatgpt.com/docs/pricing)
- Terra 的长上下文阈值是单次请求输入超过 272K Token；输入及缓存为 2 倍，输出为 1.5 倍。Fast、Ultrafast、地区处理及 FedRAMP 也有各自费率。当前每日模型聚合不保留充分的逐请求计费维度，本轮继续沿用 Standard 短上下文等效口径，不能按每日 Token 总量选长上下文档。[GPT-6.1 Terra 模型页](https://developers.openai.com/api/docs/models/gpt-6.1-terra)、[API Pricing](https://developers.openai.com/api/docs/pricing)
- 当前价目列出 `gpt-6-astra-law` 与 `gpt-rosalind-discovery`，但此次未找到可核实的费率生效日期。它们继续保持未核算，不以今日价格追溯未确认的历史，也不把它们映射成其他模型。[API Pricing](https://developers.openai.com/api/docs/pricing)

## 订阅月费

官方 Pro 现有 100／200／500 美元月费档。应用新增明确的 `pro500` 识别；`pro100`、`pro200` 继续独立，只有 `pro` 时不再猜测为 200 美元，而是交给设置中的手动月费。Plus 仍为 20 美元；席位、合同及实际税费不由模型费率推算。[ChatGPT Pricing](https://learn.chatgpt.com/docs/pricing)

## 回归范围

新增测试覆盖发布前未核算、发布日起计价、缓存输入价格、日期快照归一化、每行出处恢复、固定旧目录、自定义价格优先与未知后缀。Sonnet 5.5 的真实降价追加第二行，旧测试按模型与生效日期选取，继续验证历史行。目录快照升级为 `2026.11.1`／2026-11-01，共 41 行；实际测试执行结果见本轮汇总记录。
