# OpenAI 模型费率核对：2026-10-05

核对日期：2026-10-05（Asia/Tokyo）。对照此前 `2026.09.2` 内置目录；本轮目录版本为 `2026.10.1`，目录发布日期为 2026-10-05，模型各行仍使用各自真实生效日期。以下依据为实际打开的 OpenAI 官方文档。

## 已确认的更新

`gpt-6.1-sol` 于 **2026-09-29** 发布。官方 API Changelog 明确给出不超过 272K 输入 Token 的 Standard 费率；模型页与当前 API 价目交叉一致。每百万 Token 为输入 **2 美元**、缓存输入 **0.10 美元**、缓存写入 **2.50 美元**、输出 **10 美元**。[API Changelog](https://developers.openai.com/api/docs/changelog)、[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)、[API Pricing](https://developers.openai.com/api/docs/pricing)

应用按 1 美元 = 25 点转换，新增核算行应为输入 **50**、缓存输入 **2.5**、输出 **250** 点，生效日期使用 **2026-09-29**。ChatGPT 官方 Standard 点数表也直接列出同样的三个数值。[ChatGPT Pricing](https://learn.chatgpt.com/docs/pricing)

## 现有模型与别名

美元价格单位为每百万 Token；表内使用 Standard 短上下文价格。

| 模型 | 输入美元 | 缓存输入美元 | 输出美元 | 本次核对结果 |
| --- | ---: | ---: | ---: | --- |
| gpt-6.1-sol | 2 | 0.10 | 10 | 新增独立行；2026-09-29 生效。来源：[模型页](https://developers.openai.com/api/docs/models/gpt-6.1-sol) |
| gpt-rosalind-research | 5 | 0.50 | 25 | 新增独立行；2026-10-05 开始计费。来源：[Changelog](https://developers.openai.com/api/docs/changelog) |
| gpt-6-sol | 2 | 0.20 | 10 | 仍为独立模型，现有行正确。来源：[模型页](https://developers.openai.com/api/docs/models/gpt-6-sol) |
| gpt-6-astra | 10 | 1 | 50 | Standard 基准未变。来源：[模型页](https://developers.openai.com/api/docs/models/gpt-6-astra) |
| gpt-6-luna | 0.10 | 0.01 | 0.50 | 现有行正确。来源：[模型页](https://developers.openai.com/api/docs/models/gpt-6-luna) |
| gpt-5.6-sol | 4 | 0.40 | 20 | 促销价仍至少持续至 2026-11-21，未给出确定终止日期或届时价格。来源：[API Pricing](https://developers.openai.com/api/docs/pricing) |

- GPT-6.1 Sol 官方模型页只明确列出 `gpt-6.1-sol`；此次未建立 `gpt-6.1`、`gpt-6.1-codex` 或其他猜测名称的别名关系。旧 `gpt-6-sol` 的缓存价不同，应保留各自模型名称与历史价格。[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)、[GPT-6 Sol](https://developers.openai.com/api/docs/models/gpt-6-sol)
- Daybreak Blue 模型页仍显示 `gpt-daybreak-blue-latest` 对应 `gpt-5.6-sol`；Red 模型页仍显示 `gpt-daybreak-red-latest` 对应 `gpt-5.6-cyber`。没有依据把现有 Daybreak 费率切换到 GPT-6.1 Sol。[Daybreak Blue](https://developers.openai.com/api/docs/models/gpt-daybreak-blue-latest)、[Daybreak Red](https://developers.openai.com/api/docs/models/gpt-daybreak-red-latest)
- Codex 与 ChatGPT Work 已发布 GPT-6.1 Sol；实际账号可用性取决于套餐、客户端和工作区设置。此项可用性不会改变历史用量的费率核算。[ChatGPT & Codex Changelog](https://learn.chatgpt.com/docs/changelog)、[ChatGPT Models](https://learn.chatgpt.com/docs/models)

## 同期变化与未确定项

- GPT-6 Astra 在 2026-09-29 增加 Ultrafast。它通过 `service_tier: "ultrafast"` 选择，属于单独处理档位，不能用该价格覆盖 Astra Standard 行；应用现有 Standard 等效口径应继续明确保留。[API Changelog](https://developers.openai.com/api/docs/changelog)
- GPT-Rosalind-Research 的官方 ID 为 `gpt-rosalind-research`；2026-09-08 发布条目明确写明 **2026-10-05 开始计费**，对应 **125／12.5／625 点**。此前目录没有该模型，新增行应从计费起日生效；官方条目未量化此前用量的价格，此次核对不能将其猜测为零费率。[API Changelog](https://developers.openai.com/api/docs/changelog)、[ChatGPT Pricing](https://learn.chatgpt.com/docs/pricing)

## 实现建议与验证边界

- 按仓库既有追加历史行约定升级目录：加入 GPT-6.1 Sol 与 GPT-Rosalind-Research 的精确匹配和各自真实生效日期，保留原模型行、费率来源和版本；前端演示目录与设置页候选列表同步。
- 继续遵循自定义费率优先与固定目录不被静默补齐的规则。验证新模型在各自费率生效日前未评级、生效日起计价、缓存输入占比、导出／导入以及固定旧目录的行为。
- 新模型名称与既有 `-latest`、有效日期快照格式归一化沿用现有规则；未知型号及其他变体后缀保持未知，不通过家族前缀套用新模型价格。
- 本文件是官方依据与实现建议；它本身不证明代码已经完成或测试通过。实际实现及测试结果由本轮变更记录给出。
