# codexU Windows 设计方案

> [!NOTE]
> 本文是 0.4 版 WPF/WebView2 正式发行路径的历史设计和 Electron 功能对等基线。其中“不再评估 Electron”是已经被取代的旧决策；当前迁移目标为 Electron + Vue renderer + .NET Sidecar，进程边界、协议和打包约束以仓库根 README 与 `src/CodexU.Electron/README.md` 为准。WPF 在迁移完成前仍是正式发行版。

版本：0.4
日期：2026-07-16
状态：已实施视觉回归、无障碍与费率版本化基线

## 1. 项目目标

本项目参考开源项目 [shanggqm/codexU](https://github.com/shanggqm/codexU) 的产品设计，并参考 [liu1198767931-bit/codexU-windows](https://github.com/liu1198767931-bit/codexU-windows) 的 Windows 功能边界，在 Windows 上构建本地优先的 Codex / Claude Code 用量与任务看板。

目标是复刻原项目的主要信息架构、视觉层级、交互与数据能力，同时使用 Windows 等价能力替换 macOS 独有的菜单栏、Dock、Liquid Glass 和快捷键行为。

固定技术栈：

- WPF + .NET 10：桌面宿主和 Windows 原生集成。
- Microsoft WebView2：承载 Web UI。
- Vue 3 + TypeScript + Vite：主窗口、设置和 Web 交互界面。
- 自定义 SVG、CSS 和 Canvas：额度圆环、热力图、折线图、排行条和粒子动画。
- C# 本地数据服务：Codex app-server、SQLite、session JSONL、automations 和 Claude Code transcript。
- 类型化 JSON IPC：WebView2 与 C# 之间的唯一业务通信边界。

不再评估 Electron、Tauri、纯 WPF 或纯 PWA 作为主方案。

## 2. 复刻边界

### 2.1 必须一致

- Codex / Claude Code Runtime 切换。
- 5 小时和 7 天窗口的已用/剩余比例、重置时间和不可用状态。
- 今日、近 7 天、当月和累计 token；输入、缓存输入和输出拆分。
- 按模型、未缓存输入、缓存输入和输出分别核算点数，再按用户设置换算为等效金额。
- 今日任务看板：进行中、待处理、定时、完成。
- 最近半年热力图和最近 7 日趋势摘要。
- 项目排行、工具排行和 Skill 使用排行。
- 中文/英文、自动/浅色/深色主题。
- 手动刷新、后台自动刷新、置顶、关闭隐藏和更新检查。
- 本地解析、无遥测，不保存、不展示、不上传正文。

上述列表是最终复刻目标。当前 0.4 版已交付 Codex 与 Claude Code 本地数据链路、真实更新检测、安装包、可选签名、导出备份、诊断修复、版本化费率目录，以及可在 CI 重复执行的视觉回归和无障碍测试；英文界面仍属于后续增强，不以占位返回值伪装为已实现能力。

### 2.2 Windows 等价实现

| macOS 行为 | Windows 行为 |
| --- | --- |
| 菜单栏图标 | Windows 通知区域托盘图标 |
| 菜单栏 Runtime 卡片 | 点击托盘图标打开无边框 Runtime 弹窗 |
| 菜单栏可变宽丰富指标 | 可选的任务栏旁无边框丰富状态条 |
| Dock 显示/隐藏 | Windows 任务栏显示/隐藏 |
| `Command + U` | 默认 `Ctrl + U`，再次触发时回归桌面底层 |
| Liquid Glass | Windows 11 Mica/Acrylic，Windows 10 半透明降级 |

Windows 通知区域不允许普通应用常驻可变宽文字。简约双环可以绘制进托盘图标；经典和丰富模式由任务栏旁状态条提供，不伪装为标准托盘能力。

### 2.3 第二参考项目纳入范围

`liu1198767931-bit/codexU-windows` 中与本项目定位一致的能力全部纳入，使用现有 WPF/WebView2/C# 分层重新实现：

| 能力 | 本项目实现 |
| --- | --- |
| 紧凑/展开双模式 | WPF 动态窗口约束 + Vue 整体布局缩放 |
| Codex 顶部状态条 | 独立 WPF 无边框窗口，跟随 Codex 顶层窗口；支持悬停、点击/触摸和键盘展开 |
| 额度阈值通知 | 托盘气泡通知，按额度重置周期去重 |
| 增量 rollout 索引 | `%LOCALAPPDATA%\codexU\session-index-v1.json` 保存文件元数据、解析游标和字节偏移；未变化文件复用，增长文件续读 |
| 模型、任务生命周期统计 | session token/model 与 task started/completed/aborted 聚合 |
| 本地待办 | 独立 `todos.json`，支持优先级、日期、筛选、编辑、完成和清理 |
| 最近线程转待办 | 线程卡片通过白名单 IPC 创建关联待办 |
| `CODEX_HOME` 与工作范围 | 设置优先，其次环境变量，最后回退 `%USERPROFILE%\.codex`；最近任务支持工作区和子代理过滤 |
| Automations、Goals、日志健康 | 只读元数据和 WARN/ERROR 数量，不显示日志正文 |
| 设置与诊断 | 显示每个本地数据源状态、路径和错误原因 |
| 单实例、托盘、开机启动 | 命名 Mutex/事件、NotifyIcon、HKCU Run |
| 浅色玻璃主题 | Vue 设计 token 切换，支持深色、浅色和跟随系统 |

不复制参考项目的 Electron 实现，也不读取其代码作为运行时依赖。

## 3. 设计原则

- 像桌面工具，不像网页后台管理系统。
- 高信息密度，但每个区域有稳定层级和对齐基线。
- 蓝紫色表示品牌和额度，绿色仅表示完成/健康，橙色表示活动/输出，红色仅表示错误。
- 估算、回退和缺失数据必须明确标记。
- UI 不展示 prompt、回复正文、tool arguments、auth、环境变量或 raw logs。
- 前端无文件系统和进程权限；所有本地能力通过最小化 IPC 暴露。
- 刷新不得阻塞 UI；初次扫描显示可用的加载状态。

## 4. 总体架构

```text
WPF / .NET 10 Desktop Host
├─ Window & Native Integration
│  ├─ MainWindow
│  ├─ StatusStripWindow
│  ├─ TrayIcon
│  ├─ GlobalHotKey
│  └─ Mica / Acrylic
├─ WebView2 Host
│  └─ Vue 3 + TypeScript application
├─ Typed IPC Dispatcher
└─ Local Services
   ├─ CodexAppServerClient
   ├─ CodexSqliteReader
   ├─ CodexSessionReader
   ├─ AutomationReader
   ├─ ClaudeCodeUsageReader
   ├─ UsageAggregator
   ├─ SettingsStore
   └─ UpdateChecker
```

## 5. 解决方案结构

```text
src/
├─ CodexU.App/                 WPF 启动、窗口、WebView2、托盘
├─ CodexU.Core/                领域模型、聚合和业务口径
├─ CodexU.Infrastructure/      Codex/Claude/SQLite/JSONL 实现
├─ CodexU.Contracts/           IPC 请求、响应、事件 DTO
└─ CodexU.Web/                 Vue 3 Web UI
tests/
├─ CodexU.Core.Tests/
└─ CodexU.Infrastructure.Tests/
```

依赖方向：

```text
CodexU.App ──> CodexU.Infrastructure ──> CodexU.Core
     │                    │
     └────────> CodexU.Contracts <─────┘

CodexU.Web 只通过生成或手工同步的 Contracts 与宿主通信。
```

## 6. 数据源

### 6.1 Codex

- 账户和额度：启动 `codex app-server`，使用 stdio JSONL/JSON-RPC。
- 账户方法：`account/read`。
- 额度方法：`account/rateLimits/read`。
- Token 主账本仅由本机历史 session JSONL 重建，不调用 `account/usage/read`。
- 本机线程：`%USERPROFILE%\.codex\state_5.sqlite`。
- 精细 token：`%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`。
- 已归档 session：`%USERPROFILE%\.codex\archived_sessions\*.jsonl`。
- 定时任务：`%USERPROFILE%\.codex\automations\**\automation.toml`，数据库数据可作为增强来源。

应用只读取上述数据；不得读取或显示 `auth.json` 的凭据内容。

Microsoft Store 中的桌面应用已显示为 ChatGPT，但 app-server CLI 仍使用 `codex.exe`。包内 `WindowsApps` 副本可能禁止普通桌面进程直接启动，因此应用依次检查显式设置、`CODEXU_CODEX_PATH`、`CODEX_INSTALL_DIR`、官方独立安装目录、ChatGPT/Codex 的本地版本化运行时目录和 `PATH`，并只在 initialize 握手成功后接受候选。额度读取采用双路径：优先调用 `codex app-server`；若所有候选均失败，则从最新 `token_count.info.rate_limits` 恢复 5 小时/7 天窗口、重置时间和套餐类型。回退来源必须显示在诊断页。

### 6.2 Claude Code

- 历史 token 和工具：`%USERPROFILE%\.claude\projects\**\*.jsonl`。
- 任务：`%USERPROFILE%\.claude\tasks\**\*.json`。
- active quota：可选的本地 snapshot；缺失时显示 `--`，不伪造零值。

### 6.3 缓存与刷新

- 额度、完整用量、趋势和排行：启动时加载，之后每 5 分钟刷新。
- 今日任务：每 10 秒轻量刷新。
- JSONL 缓存键：规范化路径、文件大小、最后修改时间。
- SQLite 使用只读连接和短超时，处理 WAL 并避免锁住 Codex。
- 所有读取支持取消、超时和部分失败；一个数据源失败不能清空其他已加载数据。

## 7. 领域模型

核心快照：

```text
DashboardSnapshot
├─ Runtime
├─ Account
├─ Quotas[primary, secondary]
├─ TokenSummary[today, sevenDay, month, lifetime]
├─ TokenBreakdown[input, cachedInput, output, reasoning]
├─ CreditUsage[total, unratedTokens, byModel]
├─ TaskBoard[active, pending, scheduled, done]
├─ UsageTrend[dayBuckets, heatmap, summary]
├─ ProjectRanking
├─ ToolRanking
├─ SkillRanking
├─ RefreshedAt
└─ Diagnostics
```

所有估算项携带 `quality: detailed | approximate | unavailable`，而不是只依赖展示文案推断。

## 8. IPC 设计

统一消息包：

```json
{
  "version": 1,
  "id": "request-id",
  "type": "request",
  "method": "usage.getSnapshot",
  "payload": {}
}
```

响应：

```json
{
  "version": 1,
  "id": "request-id",
  "type": "response",
  "ok": true,
  "payload": {}
}
```

宿主事件：

```json
{
  "version": 1,
  "type": "event",
  "method": "usage.snapshotChanged",
  "payload": {}
}
```

当前方法白名单：

- `app.initialize`
- `usage.getSnapshot`
- `usage.refresh`
- `runtime.select`
- `settings.get`
- `settings.update`
- `rates.getCatalog`
- `rates.export`
- `rates.import`
- `rates.reset`
- `todos.list/add/update/toggle/delete/clearCompleted`
- `update.check`
- `update.openRelease`
- `data.exportAggregates`
- `data.backup`
- `data.restore`
- `diagnostics.export`
- `diagnostics.rebuildIndex`
- `window.toggleCompact`
- `window.show`
- `window.hide`
- `window.setAlwaysOnTop`

IPC 分发采用明确白名单。不得把任意 PowerShell、SQL、路径读取或进程启动能力暴露给 Web UI。

## 9. Web UI

### 9.1 页面路由

- `/#/dashboard`：主窗口。
- `/#/settings`：设置。
- `/#/runtime-popup`：托盘弹窗。

顶部丰富状态条不使用 Web 路由，由独立的 `StatusStripWindow` 原生 WPF 窗口承载。它复用 C# 数据服务但不依赖 WebView 是否成功导航；启用时回放最近快照并立即刷新，读取失败时保留旧数据并提供诊断与手动重试。窗口按目标显示器的工作区与 DPI 计算尺寸和位置，小工作区使用可滚动详情；缺失、部分和估算数据分别显示，不把未知值伪装为零。详情可悬停临时展开，也可通过点击、触摸、Enter/Space 固定展开并用 Escape 收起。

### 9.2 主窗口布局

1. 顶部 Header：Logo、产品名、Runtime 切换、账户状态、设置和刷新。
2. 首屏：5h/7d 双环、Token 指标、等效金额与金额档位进度。
3. Tab：今日任务、用量趋势、项目排行、Skill 使用。
4. 页脚：本地读取状态、数据口径和刷新时间。

### 9.3 视觉实现

- 全局设计 token 使用 CSS Custom Properties。
- 不采用 Element Plus、Ant Design Vue、Vuetify 等完整视觉框架。
- 双环、折线、热力图使用 SVG；粒子和大量动态点使用 Canvas。
- 关键数字使用等宽数字特性，避免刷新抖动。
- 常规正文和辅助标签不低于 11px 设计字号，交互控件正文以 12px 为基线；主要数字使用 15–29px 层级。
- 使用 CSS 媒体查询和宿主主题事件支持浅色、深色和高对比。
- 以 1000×750 Web 设计坐标为基准，依据 WebView 可用宽高等比缩放整个界面；缩放参与布局，内容超高时保留滚动，不允许裁切。
- WPF 使用设备无关像素；窗口跨屏或 DPI 改变时重新读取当前显示器工作区并更新最小/最大尺寸约束。
- 820px 逻辑窗口宽度下不出现水平滚动；125%、150%、200% DPI 均需截图验证。
- 主内容提供跳转链接；仪表盘 Tab 使用标准 `tablist` / `tab` / `tabpanel` 语义，并支持左右方向键、Home、End 和清晰的焦点指示。
- 动画遵循 `prefers-reduced-motion`，Windows 强制颜色模式下保留边框和控件辨识度。

### 9.4 点数核算口径

点数是内部核算中间量。每条精细 token 增量先归因到当时的模型，再按以下四类独立计算：

```text
未缓存输入点数 = (input_tokens - cached_input_tokens - cache_write_tokens) / 1,000,000 × 模型输入费率
缓存读取点数   = cached_input_tokens / 1,000,000 × 模型缓存输入费率
缓存写入点数   = 5m 写入 / 1,000,000 × 模型输入费率 × 1.25
               + 1h 写入 / 1,000,000 × 模型输入费率 × 2.0
输出点数       = output_tokens / 1,000,000 × 模型输出费率
总点数         = 四类点数之和
```

输入侧被切成三片——普通输入、缓存读取、缓存写入——三者相加恒等于 `input_tokens`，不重复计数。缓存写入按 TTL 分档，倍率是基础输入价的固定倍数，因此随目录版本管理而不逐模型标注。未报告该拆分的数据源（Codex）两档均为 0，整个余量仍按普通输入计价，与拆分引入前完全一致。

界面默认不直接展示点数，而是按设置中的换算关系显示金额：

```text
等效金额 = 总点数 / 1,000 × 每 1,000 点对应金额
```

默认值为 `1,000 点 = US$40`，即 500 点 = US$20、2,000 点 = US$80。用户可在设置页修改每 1,000 点对应的美元金额；所有等效金额、订阅月费、净收益和回本倍数统一使用美元，不提供其它货币入口。修改只影响展示换算，不改变模型点数费率和历史 token 聚合。

API 等价收益卡在上述金额之上继续计算：

```text
缓存节省点数 = cached_input_tokens / 1,000,000 ×（模型输入费率 - 模型缓存输入费率）
净等价值     = 本月 API 等价值 - 订阅月费
回本倍数     = 本月 API 等价值 / 订阅月费
预计本月     = 当前月累计 API 等价值 / 当月已流逝天数 × 当月总天数
```

订阅月费默认根据本机账户返回的套餐标识推算：`free = US$0`、`plus = US$20`、`prolite = US$100`、`pro = US$200`。推算值不等同于真实账单；Business、Enterprise 或未知套餐不会猜测价格，而是使用设置页中的手动备用值（默认 `US$200`）。用户可关闭自动推算或直接编辑手动值；该金额仅用于收益对比，不参与 token 点数计算。

费率基准：

| 模型 | 未缓存输入 / 1M | 缓存输入 / 1M | 输出 / 1M |
| --- | ---: | ---: | ---: |
| GPT-5.6 Sol | 125 | 12.5 | 750 |
| GPT-5.6 Terra | 62.5 | 6.25 | 375 |
| GPT-5.6 Luna | 25 | 2.5 | 150 |
| GPT-5.5 | 125 | 12.5 | 750 |
| GPT-5.5 Cyber | 500 | 50 | 3,000 |
| GPT-5.4 | 62.5 | 6.25 | 375 |
| GPT-5.4-Mini | 18.75 | 1.875 | 113 |
| GPT-5.3-Codex | 43.75 | 4.375 | 350 |
| GPT-5.2 | 43.75 | 4.375 | 350 |
| GPT-Image-2.0（image） | 200 | 50 | 750 |
| GPT-Image-2.0（text） | 125 | 31.25 | 250 |

GPT-5.3-Codex-Spark 仍为 research preview，不计入已核算点数；无法识别模型的 token 计入 `unratedTokens` 并在界面明确提示。购点参考仅用于刻度：500 点 / US$20、1,000 点 / US$40、2,000 点 / US$80。

费率目录采用 schema 版本与业务版本双层版本化。内置目录当前为 schema `1`、目录版本 `2026.07.1`，来源为用户提供的 OpenAI Credits 参考表。每条费率可记录模型、三类点数费率、生效日期、来源、目录版本和匹配方式；生效日期留空表示适用于全部历史。内置目录只允许精确模型/显式别名匹配，避免未来未知后缀被猜价；自定义项默认精确匹配，也可由用户明确选择“前缀家族”。历史用量按发生日期选择当日已经生效的最新费率，自定义目录优先于内置目录，未来生效的版本不得回写历史金额。

设置页支持导出、导入和恢复内置费率目录。导入使用最大 1 MB 的 JSON 文件，校验 schema、目录元数据、1–1,000 条目录行（其中自定义覆盖最多 200 条）、重复模型/日期、匹配方式、字段长度和数值范围，经用户确认后原子替换当前自定义费率；未知 schema 必须拒绝，不能静默降级。目录导出会把自定义项与其依赖的内置基线合并为完整解析目录，并包含 schema、目录版本、基线版本、来源、导出时间和全部费率，确保未来内置表变化后仍可重放，便于审计及回滚。

## 10. Windows 原生宿主

- 主窗口首选 1080×810，设计最小 820×620；当显示器工作区更小时，最小尺寸自动下调并保持 16 个设备无关像素的工作区余量。
- 关闭行为可配置为隐藏到托盘或退出。
- 默认 `Ctrl + U` 唤起主窗口；支持从固定安全组合中选择快捷键，再次触发时把窗口放回桌面底层。
- 托盘左键打开 Runtime 弹窗，右键打开命令菜单。
- WPF 窗口通过 DWM 请求 Mica；失败时使用设计 token 的不透明/半透明背景。
- WebView2 使用打包的静态资源和虚拟 HTTPS 主机映射，不启动 localhost 服务。
- WebView2 Runtime 使用 Evergreen；安装阶段检测并在缺失时引导安装。

## 11. 隐私与安全

- 为生成聚合统计，宿主会在本机解析 session、SQLite 和日志元数据中的必要字段；不保存、不展示、不上传对话正文、reasoning、工具参数、工具输出、附件正文或 auth token。
- 默认 Content Security Policy：`default-src 'self'`。
- WebView2 禁用不需要的浏览器能力、默认上下文菜单、状态栏和外部拖放。
- 外部 URL 交给系统浏览器，禁止在应用 WebView 内任意导航。
- 前端资源固定映射到 `https://app.codexu.local/`。
- 日志只记录聚合、错误分类和性能，不记录 prompt、回复、参数、凭据或完整 session 行。
- 设置文件写入 `%LOCALAPPDATA%\codexU\settings.json`。

## 12. 更新与发布

- 每天最多检查一次 GitHub Release，也可手动强制检查；发现更新后由用户打开下载页，不静默安装。私有仓库令牌只从进程环境变量读取，不写入设置。
- GitHub Actions 对 pull request、`main` 和手动触发执行构建、格式、单元测试、Playwright 视觉回归、axe 无障碍扫描、键盘操作测试、自包含发布、许可清单及真实 EXE 冒烟验证，并保留短期验证产物；视觉失败时额外上传截图差异与 trace。
- `main` 上的版本号首次出现时自动创建 `vX.Y.Z` 标签和 GitHub Release；已完整发布的版本不会被覆盖。草稿、资源上传和正式发布分阶段执行，失败重跑可修复缺失资源。
- Release 发布 `win-x64` 完整 ZIP、每用户 Setup EXE 和各自 SHA-256 校验文件，后续可增加 `win-arm64`。
- .NET 使用 self-contained 发布，WebView2 使用系统 Evergreen Runtime。
- 安装包支持开始菜单、卸载项和可选开机启动。
- 仓库配置签名证书密钥时，发布流程对应用 EXE 与 Setup EXE 执行 Authenticode 签名和验证；缺少密钥时明确发布未签名产物。
- 商业发布按 Inno Setup 官方许可说明配置 `INNO_SETUP_LICENSE_KEY` 仓库 Secret；非商业构建不强制提供。
- 发布产物必须携带原项目 MIT License 和本项目第三方许可清单。

## 13. 测试与验收

### 13.1 自动测试

- Core：token delta、模型点数费率、缓存/未缓存/输出拆分、按日期选择历史费率、跨版本聚合、日期桶、回退质量、任务分类。
- Infrastructure：app-server JSONL、SQLite schema 兼容、session fixtures、异常文件、费率目录导入导出和 schema 拒绝。
- Web：生产构建、确定性浏览器演示快照和 IPC mock。
- 视觉：Playwright 像素基线覆盖深色/浅色主视图、主要 Tab、设置页和紧凑布局；关闭动画并使用固定时钟，避免时间与数据漂移造成误报。
- 无障碍：axe 扫描关键页面的 serious/critical 违规，验证 Tab 键顺序、Tab 列表方向键/Home/End、主内容跳转、表单标签、可见焦点与 reduced-motion。
- 宿主：IPC 白名单、导航限制、关闭行为和单实例。

### 13.2 验收条件

- 主窗口可在没有 Codex 数据时正常启动并展示空状态。
- 有本地 Codex 数据时可显示额度、Token 和任务。
- Web UI 不能直接读取任意本地文件或启动进程。
- 刷新过程中窗口可交互，不发生 UI 线程阻塞。
- 关闭隐藏、托盘恢复和快捷键恢复均可靠。
- 所有估算和回退数据有明确标记。
- 不上传本机数据，不产生遥测请求。
- 所有已提交视觉基线在 Windows Chromium 上无非预期差异；差异必须经人工确认后显式更新。
- 关键页面没有 axe serious/critical 违规，并可只使用键盘完成 Runtime 切换、Tab 导航、待办和设置保存。
- 同一模型跨费率版本的历史核算保持可重放，导出的目录可以无损导入。

## 14. 实施阶段

### 阶段 1：可运行骨架（已完成）

- 解决方案和分层项目。
- Vue/Vite 工程。
- WebView2 加载打包资源。
- `app.initialize`、`usage.getSnapshot`、`usage.refresh`。
- 主窗口首版界面和模拟/本机快照。

### 阶段 2：Codex 完整数据（已完成）

- app-server 额度。
- SQLite 本机线程。
- session JSONL 精细 token。
- 任务、趋势、项目、工具和 Skill 聚合。

### 阶段 3：桌面集成（已完成）

- 托盘、顶部状态条、全局快捷键、关闭隐藏。
- Mica/Acrylic、主题、设置持久化。
- 丰富状态条。

### 阶段 4：发布质量（已完成）

- Claude Code 本地数据适配。
- GitHub 自动验证与自动 Release（已完成）。
- 应用内更新检测、安装包和可选签名。
- 聚合导出、设置与待办备份恢复、脱敏诊断包、索引安全重建。

### 阶段 5：可回归与可审计（已完成）

- Playwright 视觉基线、axe 无障碍扫描与键盘操作测试。
- 深色、浅色、主要 Tab 和紧凑布局的确定性截图矩阵。
- 费率目录 schema/业务版本、按日期生效、导入导出、恢复内置与自动测试。
- CI 和 Release 在发布前执行相同的前端质量门禁。

### 后续增强

- 英文界面。
- Windows ARM64 发布产物。
- 在真实多显示器设备上扩充 Narrator 与 125%/150%/200% DPI 人工验收矩阵。

## 15. 已确认决策

- Windows 为首要且当前唯一交付平台。
- 技术栈固定为 WPF/.NET 10 + WebView2 + Vue 3/TypeScript。
- Web UI 与本地能力严格分层。
- 数据读取保持本地、只读和无遥测。
- 主窗口功能与信息架构按 codexU 复刻。
- macOS 独有系统行为使用 Windows 等价能力，不声称操作系统级像素一致。
