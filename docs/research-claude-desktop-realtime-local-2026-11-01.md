# Claude Desktop 实时额度与重置时间：本地静态核查

核查日期：2026-11-01。对象为 Claude Desktop `4.4212.0.0` 分发的 `app.asar`，SHA-256：`f0058d0b13c4787a2f2c1684bbb48b7352efffc68a1e35d8ca574698afa90a63`。

本次只读取分发代码，以及精确匹配指定工具名的本地调用记录；没有调用额度 API、内部 IPC 或探针，没有读取凭据、Cookie 存储或进程内存，没有注入、修改或重启 Desktop。未把真实账号标识、额度或会话内容复制进仓库。

## 结论与证据等级

| 路线 | 已确认的能力 | 尚未确认／边界 |
| --- | --- | --- |
| Desktop Code 内建 `mcp__ccd_session_mgmt__get_usage` | 源码确认返回账号额度百分比、真实重置时间及会话上下文；使用 Desktop 自身的 OAuth 能力 | 受功能开关、会话类型和工具策略约束；本次没有执行调用，不能保证当前会话一定暴露该工具 |
| Desktop 托盘用量 | 主进程直接获取完整用量，内存中保留 `resets_at` | 持久化历史只保存百分比；未发现可供 codexU 直接调用的公开读取接口 |
| 用户让 Desktop Code 读取并保存白名单快照 | 可作为下一步验证方案，无需把登录凭据交给 codexU | `get_usage` 自身只读、不写文件；保存需另外由用户授权的会话文件操作完成，尚未验证导出格式与自动刷新机制 |
| `ClaudeCode.getPeriodUsage()` | 内部探针返回日／周行为统计 | **不是账号额度接口**；不能据此得到本任务所需的重置时间 |
| 直接连 Desktop 的内部 IPC／MCP／进程内函数 | 找到内部调用关系 | 没有据此确认公开稳定的外部入口；不能把“代码里存在函数”当作 codexU 可直接调用的 API |

## 内建 get_usage：最值得先验证的路线

来源 `[S2]` 注册的服务器名称为 `ccd_session_mgmt`，工具为 `get_usage`，完整名称是 `mcp__ccd_session_mgmt__get_usage`。输入只有可选字符串 `session_id`，没有必填字段，省略时使用 `self`，因此 `{}` 合法。账号额度不随这个参数变化，该参数选择的是要查询上下文用量的会话。

注册有三层限制：服务器仅对 `sessionType === "ccd"` 启用；动态工具受功能开关控制，代码默认值为 true；内部服务器管理器还会应用工具禁用策略及不可信 CLI 限制。它通过 `createSdkMcpServer` 注册在会话的 SDK 工具集合内，而不是在这个注册点创建独立 HTTP 或 named-pipe 服务。源码描述其为只读工具，未核实存在对应的专用 slash 命令。`[S2]`

调用结果是 `{ plan, context }`。`plan` 可包含状态、套餐显示名、5 小时／全部模型周额度／模型专属周额度及额外用量；窗口中有 `percentUsed`、`resetsAt` 和相对重置时间。实际 `resetsAt` 来自服务端响应字段：字符串时间经校验，数字时间按 Unix 秒转换，不是用当前时间加窗口长度推算。没有该字段时保持缺失。`[S2][S3]`

内部 `readPlanUsage` 的生产默认请求为：

```text
https://api.anthropic.com/api/oauth/usage
```

认证类别是 Desktop 持有的 Claude Code OAuth Bearer，并带 `anthropic-beta: oauth-2025-04-20`；没有读取或记录任何实际凭据。本模块识别由 API key、Bedrock、Vertex 或网关自行路由的部署，将其标记为不适用，而不是返回伪造的订阅额度。成功结果最多缓存 60 秒，失败缓存 20 秒，请求总超时 10 秒，因此这是分钟内的近实时能力，不能承诺零延迟。`[S1][S3]`

下一步可由用户在已登录的 Desktop **Code 标签**中提出以下验证请求，本次研究没有发送它：

> 请调用内置工具 mcp__ccd_session_mgmt__get_usage，参数为 {}。只展示 plan 中实际返回的额度百分比和 resetsAt；缺失值保持未知，不推算。如果工具不可用，请明确说明。

如果成功，再评估由该会话按用户要求写入最小白名单快照，或者仅读取该工具对应的 transcript 结果。后者必须精确匹配 tool-use identity、校验 schema 和新鲜度，不能把无关会话文本当额度。此次检查的近期 16 个 transcript 文件没有该工具的调用或结果，当前没有现成结果可直接复用。

## 托盘用量、刷新与内部 IPC

托盘模块 `[S1]` 通过 Electron `net.fetch` 请求默认主窗口域名下的组织用量：

```text
https://claude.ai/api/organizations/{active-organization}/usage
```

可追加 `skip_spend=1`，单次超时 15 秒。调用点没有手动传入 API key 或 Bearer；它使用 Desktop 的 Electron 默认 session 网络栈，并由应用解析当前组织。认证类别可判断为 Desktop 自身网页会话路径，未检查 Cookie 值或额外运行时网络处理器。[Electron 官方说明：net.fetch 使用默认 session](https://www.electronjs.org/docs/latest/api/net/#netfetchinput-init)

定时行为在本版本代码中为：通常每 15 分钟，托盘交互后的 30 分钟内改为每 5 分钟；启动托盘用量功能、账号变化、解锁恢复以及托盘右键交互可触发刷新。后台额外支出信息最多约每小时更新，其余刷新可跳过支出；系统锁定或空闲至少 10 分钟时可以暂停。另有远程配置可要求近期打开过托盘才继续后台轮询。因此不能把历史文件修改频率当成固定实时保证。`[S1]`

完整响应的各窗口含 `utilization`、`resets_at`，还有新 `limits` 结构及额外用量。它保存在 `PVe` 内存 provider；该 provider 的实现只有 value、get/set/reset 和 change 事件。持久化的 `plan-usage-history.json` 仍仅保留 `t/org/u`，没有重置时间或套餐类型。`[S1]`

preload 暴露 `claude.web.PlanUsage.nudgeRefresh()`，对应 main 的 `nudgeRefresh` 实现只发起刷新，不返回额度。该接口没有 getter/store 定义。IPC 使用绑定到具体 webContents 的 handler，并校验顶层 sender frame 及允许的应用来源；没有发现把这个方法开放为任意外部进程可调用的 URL、HTTP 或 pipe 的注册。`[S1][S5]`

## getPeriodUsage 不能替代账号额度

`ClaudeCode.getPeriodUsage()` 是应用内部 renderer/main IPC。它加载 `probePeriodUsage`，由 Desktop 获取自己的认证能力，在不持久化会话、工具为空、工具调用被拒绝的选项下启动受控 SDK 查询。探针调用名称明确标为实验性的 usage 方法，随后只取 `behaviors.day` 与 `behaviors.week`。这用于行为统计，不是上述 `plan.windows`；把它解释为外部 SDK 可直接读取 Desktop 的实时额度是不成立的。`[S1][S4]`

## 可集成性判断

在已追踪的额度模块内，未发现一个现成、公开稳定、无需认证的本地额度 HTTP／pipe 服务，也未发现包含完整 reset 的专用自动导出文件。内建 MCP 路线是由已经登录的 Desktop **内部会话**代为读取；是否进一步形成可维护的 codexU 文件桥接，需要一次明确授权的实际验证。它不要求、也不应以读取 Desktop Cookie、复制 OAuth token、注入进程或绕过 IPC 来源校验为前提。

这些符号、功能开关和接口来自本版本的分发实现，不构成 Anthropic 对第三方的稳定 API 承诺。若需求是独立后台自动查询，应另行比较官方 SDK 和独立登录方案；本研究不把内部 OAuth endpoint 的存在视作直接调用授权。

本轮另一条已完成的实机验证是：独立启动未修改的 CLI 2.1.309 可以成功响应无模型提示词的 `get_usage` 控制请求，但由于该 CLI 没有登录，只返回额度不可用。它证明协议入口存在，不能代替在已登录 Desktop 会话中的工具验证。详见 [官方通道与本机探针](research-claude-realtime-official-2026-11-01.md)；社区实现和认证边界见 [开源实现核对](research-claude-realtime-implementations-2026-11-01.md)。

## 一手来源索引

以下路径均是上述 `app.asar` 的内部成员路径，不是用户数据目录。没有复制第三方完整源文件。

| 编号 | 成员与定位点 | 支持的事实 |
| --- | --- | --- |
| S1 | `.vite/build/index.chunk-2SHPxESV.js`：`FKi`、`JKi`、`XKi`、`LKi`、`HKi`、`PVe`、`PlanUsage`、`Py`、`ov`、`SVn` | 托盘请求、时间常量、内存缓存、IPC 来源限制、默认域名及生产 OAuth API host |
| S2 | `.vite/build/index.chunk-DQTSEjY1.js`：`Kp`、`zp`、`lf`、`rm`、`Qu`、`InternalMcpServerManager.createProxyServers` | get_usage 注册、开关、输入、只读用途、结果字段、SDK 内部 transport |
| S3 | `.vite/build/index.chunk-CFw7lR-H.js`：`readPlanUsage`、`toRateLimits` | OAuth usage 请求、认证类别、缓存／超时、服务端 reset 转换 |
| S4 | `.vite/build/index.chunk-Cq5KH6Si.js`：`probePeriodUsage` | 实验 usage 探针及仅提取 behaviors.day/week 的边界 |
| S5 | `.vite/build/mainView.js`：`PlanUsage_$_nudgeRefresh`、`ClaudeCode_$_getPeriodUsage` | 内部 preload 方法及没有额度 getter 的 PlanUsage 定义 |

最小代码证据只保留接口与映射形状：

```text
serverName: "ccd_session_mgmt"
isEnabled: context => context.sessionType === "ccd"
PlanUsage: methods = ["nudgeRefresh"]
usage result = { plan, context }
history sample = { t: Date.now(), org, u }
period probe result = { day: behaviors.day, week: behaviors.week }
```
