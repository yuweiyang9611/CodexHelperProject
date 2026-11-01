# Claude 实时额度与重置时间：开源实现核对

核对日期：2026-11-01（Asia/Tokyo）。本次只读取公开项目的代码与文档，没有安装或运行这些工具，没有读取本机 Cookie、Token、凭据或进程内存，也没有请求用户账户接口。下面的“已确认”表示源码存在相应实现，不表示已在本机账号验证成功。

## 结论

社区工具可以取得服务端返回的额度百分比和 `resets_at`，主要有 OAuth 请求、登录后的 claude.ai Web 请求、Claude CLI `/usage` 三条路径。它们的“实时”通常指按需或定时请求服务端，不是从本地 Token 总数计算，也不是秒级推送。Desktop 的本地历史缓存不含重置时间，并不意味着服务端没有该字段。

对本项目，优先验证**未修改的官方 Claude Code CLI 自持认证、仅向本应用输出非秘密的用量数据**。补充核对已确认 CLI 的 `get_usage` 控制请求可以不发送模型提示词，见下文。Desktop 登录与独立 CLI 不一定互通，需要分别验证账号环境。

独立 Electron／WebView2 登录和 Cookie 路线保留为社区技术证据，**不作为本公开产品的默认推荐**。当前官方说明不允许第三方开发者在自己的应用提供 Claude.ai 登录，或收集、保存、中转 Claude.ai 凭据／会话 Token；同时明确不阻止最终用户以自己的订阅登录未修改的 Claude Code binary。产品方案应遵循这一区分，不能把“代码能请求成功”视为产品集成许可。本报告不对所有个人工具作进一步法律判断。[官方认证与凭据使用说明](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use)

## 核对的固定源码版本

| 项目 | 已读取提交 | 提交时间 | 作用 |
| --- | --- | --- | --- |
| steipete/CodexBar | `897bff31731bb4b21b105f6e8bf4c32a8a8bf095` | 2026-10-31 | OAuth、Web、CLI 三种来源及限流处理 |
| thivanao-jp/claude-usage-hud | `50e3daeea07833262a9559000c8e08790003d67c` | 2026-10-28 | Windows Electron 中保留登录会话并执行同源请求 |
| HanChangHun/claude-usage | `d4aa5728ebbbdde2affa8fd82b78dae7f48d0b85` | 2026-10-27 | Windows WebView2 登录、Rust 请求与一分钟轮询 |
| turkbil/claude-usage-widget-windows | `a792396c6b8499365dcff032716008de4558b2d9` | 本次未单独核对提交时间 | Windows .NET 浏览器 Cookie 读取与额度接口 |

所有链接固定到上述提交，避免后续主分支变化影响本次结论。

## 实现比较

| 路线 | 认证由谁提供 | 百分比／重置时间 | Windows 可行性与限制 |
| --- | --- | --- | --- |
| CodexBar OAuth | Claude Code 登录产生的 OAuth access token；源码还维护不同凭据来源和刷新策略 | `five_hour`、`seven_day` 等窗口中的 `utilization`、`resets_at` | HTTP 请求本身可移植；不能假定 Desktop 登录会留下独立 CLI 可用的凭据，CodexBar 原生 GUI 也不是 Windows 实现 |
| CodexBar CLI | 由 `claude` 自行处理登录 | 在 PTY 中执行 `/usage`，解析文字百分比与重置描述；`/status` 仅补账号信息 | Unix PTY 实现不能原样移植为 Windows 管道；需先验证当前 Windows CLI／SDK 的官方结构化输出能力 |
| Electron 同源 Web | 用户在独立 `persist:claude-ai` 会话内登录 | claude.ai 组织用量响应，含百分比及可空的 `resets_at` | 项目提供 Windows x64 NSIS／portable 构建；认证 Cookie 随浏览器请求发送，代码无需把 Cookie 导出给 HTTP 客户端 |
| WebView2 → Rust HTTP | 用户在应用内登录，程序随后读取该 WebView2 的 Cookie | 同一 claude.ai 组织用量响应 | Windows 已有实现，但 Cookie 会进入 Rust 内存；与“仅浏览器内部使用认证”的方案有区别 |
| 直接读取既有浏览器 Cookie | 用户浏览器存储，程序解密后发送 Cookie | 同一 claude.ai 组织用量响应 | 受浏览器加密、登录账号和网络挑战影响；现有源码不能证明本机所有 Chromium 版本均可读取 |

依据：[CodexBar OAuth 请求](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift#L58)、[CLI 探针](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/Sources/CodexBarCore/Providers/Claude/ClaudeStatusProbe.swift#L75)、[Electron 登录窗口](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/claudeWebFetcher.ts#L61)、[Windows 打包目标](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/package.json#L93)、[WebView2 Cookie 到 Rust 请求](https://github.com/HanChangHun/claude-usage/blob/d4aa5728ebbbdde2affa8fd82b78dae7f48d0b85/app/src-tauri/src/lib.rs#L529)、[.NET 浏览器读取器](https://github.com/turkbil/claude-usage-widget-windows/blob/a792396c6b8499365dcff032716008de4558b2d9/src/BrowserCookieReader.cs#L46)。

## 端点、字段与认证细节

**OAuth。** CodexBar 向 `https://api.anthropic.com/api/oauth/usage` 发 GET，使用 Bearer access token 与 `anthropic-beta: oauth-2025-04-20`，请求超时为 30 秒。它识别 401、429，并针对缺少 `user:profile` 的 403 给出独立错误；不能把普通 API Key、MCP OAuth token 或仅可推理的 token 当成相同凭据。源码中的可选 `cedar_ember=1` 是额外重置权益信息；读取窗口的下一次 `resets_at` 不要求本项目接入消耗重置权益的操作。[请求及响应类型](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift#L58)、[scope 错误处理](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/Sources/CodexBarCore/Providers/Claude/ClaudeUsageFetcher.swift#L263)。

**Web。** 典型路径是 `GET https://claude.ai/api/organizations` 选择组织，再 `GET https://claude.ai/api/organizations/{org}/usage`。CodexBar 使用 Cookie HTTP 请求；Electron HUD 则在已登录 claude.ai 的 WebContents 内使用 `fetch(..., {credentials: 'include'})`。后者的认证会话独立于现有 Claude Desktop：安装了 Desktop 不等于新窗口已经登录。[CodexBar Web 请求](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/Sources/CodexBarCore/Providers/Claude/ClaudeWeb/ClaudeWebAPIFetcher.swift#L105)、[Electron 同源请求](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/claudeWebFetcher.ts#L186)。

**响应。** 旧形状为 `five_hour`／`seven_day`／按模型的窗口，各含百分比与可空重置时间。较新 Web 响应还出现 `limits[]`，使用 `kind=session`、`kind=weekly_all`、`kind=weekly_scoped`、`percent`、`resets_at`。源码支持两种形状；不能把百分比再次乘以 100，也不能把按模型窗口、额外消费或某一产品表面窗口直接当成统一的 7 天额度。缺失或空的重置时间应继续显示未知。[HUD 类型](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/claudeApi.ts#L4)、[字段映射](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/claudeWebFetcher.ts#L370)。

这些是社区针对 Claude 自用端点的实现证据，不能据此把端点宣称为 Anthropic 对第三方承诺稳定的个人额度开放 API。API 管理员的 usage／cost report 又是另一类数据，不能替代消费者订阅的五小时／七天余量。[CodexBar 来源区分](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/docs/claude.md#L43)。当前官方 CLI／SDK 结构化用量能力应以本轮另一份官方资料核对为准；本次读取的 CodexBar 探针仍采用 PTY 文字解析。

## 刷新、限流与源码局限

- CodexBar OAuth 使用 `Retry-After`，没有可用值时默认冷却 5 分钟；这证明请求可能被限流，不能承诺每秒拉取。其通用刷新提供手动、1／2／5／15／30 分钟及自适应模式。[限流门控](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageRateLimitGate.swift#L3)、[刷新说明](https://github.com/steipete/CodexBar/blob/897bff31731bb4b21b105f6e8bf4c32a8a8bf095/docs/refresh-loop.md#L10)。
- Electron HUD 默认 10 分钟，按配置定时刷新；启动请求延迟 3 秒。Web 请求遇到 401／403 会使登录状态失效。其源码先选组织数组的首项，且存在原始用量响应日志：本项目若借鉴思路，应改为明确选择／核对账号组织，且只保留规范化字段，不照搬全量日志。[默认设置](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/settings.ts#L79)、[调度](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/main.ts#L678)、[组织选择及日志](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/claudeWebFetcher.ts#L209)。
- HanChangHun 工具轮询间隔为 60 秒；实际实现调用 `cookies_for_url` 后拼成 Cookie 请求头交给 `reqwest`，不能仅凭 README 的隐私概述认定 Cookie 从未离开 WebView。源码对 401／403 区分认证拒绝和挑战页，但通用 429 分支没有展示专用退避策略。[轮询](https://github.com/HanChangHun/claude-usage/blob/d4aa5728ebbbdde2affa8fd82b78dae7f48d0b85/app/src-tauri/src/lib.rs#L221)、[请求和错误分类](https://github.com/HanChangHun/claude-usage/blob/d4aa5728ebbbdde2affa8fd82b78dae7f48d0b85/app/src-tauri/src/lib.rs#L543)。
- Windows .NET 工具默认 60 秒；它读取浏览器 Cookie 数据库及 Local State，通过 DPAPI／AES 解密，而非 Claude Desktop 的公开接口。本次没有运行该代码，不能保证浏览器版本兼容，也不建议在未获授权时采用这种读取方式。[读取器](https://github.com/turkbil/claude-usage-widget-windows/blob/a792396c6b8499365dcff032716008de4558b2d9/src/BrowserCookieReader.cs#L65)、[默认间隔](https://github.com/turkbil/claude-usage-widget-windows/blob/a792396c6b8499365dcff032716008de4558b2d9/src/Preferences.cs#L33)。

## 已有本地 HTTP 桥接不等于 Desktop 的公开 API

HUD 提供 `127.0.0.1:49485/usage`，返回该工具最近成功拉取的数据及时间；源码没有让这个 GET 触发一次新的服务端请求。Windows .NET 工具也提供 `127.0.0.1:9123/usage`，明确只服务缓存。它们需要额外工具持续运行及登录，不能假定用户机器已经有这些服务。[HUD 服务实现](https://github.com/thivanao-jp/claude-usage-hud/blob/50e3daeea07833262a9559000c8e08790003d67c/src/main/main.ts#L905)、[.NET 本地服务](https://github.com/turkbil/claude-usage-widget-windows/blob/a792396c6b8499365dcff032716008de4558b2d9/src/LocalHTTPServer.cs#L12)。

## 补充：无模型提示词的 CLI `get_usage`

**官方来源与公开文档的区别。** Anthropic 的 SDK [0.3.169 发布记录](https://github.com/anthropics/claude-agent-sdk-typescript/blob/3a0b748adbb1d31fa66a24a15df7e7c973fe1368/CHANGELOG.md#L884) 明确新增 `Query.usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET()`，返回会话、套餐用量和本地行为数据。当前公开 TypeScript 参考没有列出这个方法名或原始 `get_usage` 控制协议；它公开的是另一路：发送 `/usage` 后，assistant 消息携带 `usage_report: SDKUsageReport`。后者自 SDK 0.3.273 提供，文档也明确标为实验类型，含 `rate_limits.limits[].percent` 和可空的 `resets_at`。两者都不能被描述为长期不变的稳定接口。[官方 SDKUsageReport 参考](https://code.claude.com/docs/en/agent-sdk/typescript#sdkusagereport)

**原版 CLI 的实际社区实现。** Windows 项目 `Sundogs-s/Claude-Codex-Usage-Monitor` 在提交 `b081d229ab8d3db7807cf0d19fa199ae1cddfd35` 的 `src/claude.rs` 启动原版 `claude -p --input-format stream-json --output-format stream-json --verbose --strict-mcp-config`，只向 stdin 写入带换行的控制帧，然后匹配 `control_response.response.request_id`。它不发送 `type=user` 的模型提示词；Windows 进程设置了隐藏控制台，90 秒超时，进程结束后释放。[启动、请求与响应解析](https://github.com/Sundogs-s/Claude-Codex-Usage-Monitor/blob/b081d229ab8d3db7807cf0d19fa199ae1cddfd35/src/claude.rs#L530)

源码证实的最小请求形状如下，仅供协议说明，本次未执行：

```json
{"type":"control_request","request_id":"quota-read","request":{"subtype":"get_usage"}}
```

`pingdotgg/t3code` 在提交 `da216062c959b9fbf1bd06fb098d76e14d5f9350` 则通过 SDK 建立从不产出用户消息的异步 prompt stream，等待 `initializationResult()` 后调用 `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET({skipBehaviors:true})`。`skipBehaviors` 跳过本地 transcript 行为扫描，适合只要额度的请求；初始化时还关闭 hooks、会话保存和非必要 MCP 来源。原始协议对应的 `skip_behaviors:true` 另有 Windows 11 用户在 Anthropic 项目 issue 中报告验证，但这是用户报告，不能与维护者文档承诺等同。[T3 probe 实现](https://github.com/pingdotgg/t3code/blob/da216062c959b9fbf1bd06fb098d76e14d5f9350/apps/server/src/provider/ClaudeProvider.ts#L321)、[探测启动选项](https://github.com/pingdotgg/t3code/blob/da216062c959b9fbf1bd06fb098d76e14d5f9350/apps/server/src/provider/ClaudeProvider.ts#L205)、[Anthropic 仓库用户验证报告](https://github.com/anthropics/claude-code/issues/93107)。

**免模型调用不等于没有网络或本地写入。** 控制帧本身不是一次模型生成；额度仍由 CLI 向服务端读取，登录过期时 CLI 可能自行刷新其凭据。Sundogs 程序的其他路径会主动读取凭据文件，API-key fallback 甚至包含模型探测请求，所以只能借鉴它的 CLI 控制协议部分，不能把整个程序都称为“无凭据接触、免模型调用”。[分支说明及模型探测](https://github.com/Sundogs-s/Claude-Codex-Usage-Monitor/blob/b081d229ab8d3db7807cf0d19fa199ae1cddfd35/src/claude.rs#L12)

**认证与失败语义。** 官方 `/usage` 结构化报告要求 Claude.ai 认证、已知套餐或 `user:profile` scope，且不属于 usage-based billing；只有 `user:inference` 的 setup-token 默认不满足条件。不能用 API Key 替代消费者订阅认证。对控制响应，应分别处理 `rate_limits_available=false`（本登录没有这种套餐窗口）、`true` 且 `rate_limits=null`（未取到数据）、窗口缺失和可空重置时间，不能全部转成零或“不支持”。T3 issue 有用户报告该空值问题，另有一次用量请求耗时约 10–11 秒而被四秒超时误判的报告；它们是他人实测证据，本机尚未验证。[官方报告认证条件](https://code.claude.com/docs/en/agent-sdk/typescript#sdkassistantmessage)、[空数据问题](https://github.com/pingdotgg/t3code/issues/15364)、[超时问题](https://github.com/pingdotgg/t3code/issues/15354)

## 可执行的下一步及授权边界

1. 先通过 Desktop 自己的用量界面或内置用量工具验证当前账号确实返回重置时间；再验证原版 CLI 的 `get_usage`／`usage_report` 能否在该用户明确选择的登录环境返回相同窗口。Desktop 内置工具的接入边界由本轮本地静态研究单独核对，不把它假装成任意外部程序可调用的公开 HTTP API。
2. 长期采集优先由未修改的 CLI 自行认证，本应用只读规范化输出，不接管 Token／Cookie。若需要新的 CLI 登录，由用户在 Anthropic 自己的登录流程完成；首次仅做一次有界手动探测，然后再决定低频刷新、超时、429 退避和来源标识。若原版 CLI 路线不可用，保留未知及官方用量页入口，不自动降级到第三方应用内 Claude.ai 登录。
3. 若只需现在查看数值，用户在 Claude 的用量界面查看比安装第三方监控器更直接；界面抓取仍需用户允许操作已登录页面，且应把页面显示的时间当作显示证据，不伪装成稳定 API。
4. 本次没有授权或实施凭据提取、Cookie 导入、私有账户 HTTP 请求、额外工具安装、应用注入或业务代码变更。是否接入某条路线，需要在完成具体方案后由用户确认其登录／会话使用范围。
