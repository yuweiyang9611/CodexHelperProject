# codexU Windows

[![CI](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/ci.yml/badge.svg)](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/ci.yml)
[![Release](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/release.yml/badge.svg)](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/release.yml)

codexU Windows 参考 [shanggqm/codexU](https://github.com/shanggqm/codexU) 的产品设计，并吸收 [liu1198767931-bit/codexU-windows](https://github.com/liu1198767931-bit/codexU-windows) 的 Windows 功能边界。当前公开的 v0.5.0 仍是 WPF/WebView2 版本；`main` 的下一版本发布链已经切换为内置 Chromium 的 Electron 宿主，继续使用 Vue 3，并将 .NET 10 保留为独立 Sidecar 后端。已有 v0.5.0 不会被新链路覆盖。

## 仓库迁移与隐私说明

本仓库于 2026-08-15 从原私有开发仓库迁移而来。原仓库的提交元数据、Pull Request 及相关记录包含个人隐私信息，因此原私有仓库及全部历史记录已删除；当前公开仓库从经过检查的文件快照重新初始化，没有导入旧提交、分支、标签、PR 或 Release 历史。

本仓库要求所有提交使用 GitHub noreply 邮箱，并通过本地 pre-push hook 与 GitHub Actions 检查提交作者、提交者、提交信息、文件内容及 PR/Issue 文本。新设备克隆后请先按 [GIT-HOOKS.md](GIT-HOOKS.md) 启用仓库 hooks；贡献前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)，不要在公开提交、PR、Issue、评论或日志中填写私人邮箱。

## 当前能力

- Electron 主进程负责窗口、生命周期和原生权限；沙箱 preload 只暴露固定 IPC 白名单，Vue 不获得 Node.js 权限。
- .NET 自包含 Sidecar 复用 Application/Core/Infrastructure，通过私有 stdin/stdout 长度前缀 JSON 协议与 Electron 通信，不监听网络端口。
- 仓库继续保留旧 WPF/WebView2 宿主作为 v0.5.0 兼容与功能对照基线，但它不再是下一版本的公开发布产物。
- Vue 3 + TypeScript 仪表盘：额度、token、按模型核算后换算的等效金额、任务、趋势、项目、工具和 Skill。
- 界面随窗口整体缩放，支持 90%–140% 可配置缩放（默认 110%），窗口最小尺寸会按当前显示器工作区和 DPI 自动约束。
- 额度双路径：自动发现独立 Codex CLI 或 ChatGPT 桌面应用的可运行 `codex.exe`，优先调用 `codex app-server`；无法启动时，从最新 rollout `rate_limits` 恢复 5 小时/7 天额度、重置时间和套餐。
- `%USERPROFILE%\.codex\state_5.sqlite`：只读线程、任务和项目统计。
- session JSONL：精细 token delta、每日桶、工具和 Skill 计数。
- session 增量索引：复用未变化文件，并从已记录字节偏移续读增长中的 rollout。
- 模型 Token 分布、任务启动/完成/中止生命周期统计。
- automations、Goals 和日志 WARN/ERROR 健康计数。
- 本地待办：优先级、日期、筛选、编辑、完成、删除、清理，以及最近线程一键转待办。
- 设置与诊断：`CODEX_HOME`、Codex 可执行文件、工作区/子代理过滤、刷新频率和数据源状态。
- Claude Code 本地适配器：聚合 `%USERPROFILE%\.claude\projects` transcript 的 token、模型、工具和 Skill，读取本地任务。transcript 中的 HTTP 429 记录会作为近 7 天的限流次数单独提示，它只表明当时已用满，不能据此推算余量。
- 用量历史留存：所有统计原本每次刷新都从源 transcript 重算，源日志被轮转或清理后那段历史就永久消失了。现在每次刷新会把当天及窗口内每一天的实测结果（含缓存写入分档与点数）按运行时写入 `%LOCALAPPDATA%\codexU\history\daily-usage-<runtime>-v1.jsonl`。统计口径受工作区与子代理过滤影响，因此每行带作用域指纹；改过滤器只会另起一组记录，**不会覆盖或删除**既有作用域的历史。写入失败只记诊断，不影响刷新。
- Claude 额度采集：Claude Code 不在本机记录额度余量，唯一可机读的来源是 statusLine 命令收到的 JSON。`tools/claude-statusline-snapshot.mjs` 既照常输出状态行，又把 `rate_limits` 的 5 小时/7 天窗口写成应用读取的快照，详见下文「Claude 额度接入」。未接入时额度显示为 `--` 并说明接入方式。
- API 等价收益：内部先按模型及缓存/未缓存/输出费率计算点数，再按可配置的“每 1,000 点美元金额”显示；收益卡同时展示订阅月费、净等价值、回本倍数、三类消耗、缓存节省和本月投影。所有金额统一使用美元，默认 1,000 点 = US$40；订阅月费优先根据本机套餐标识自动推算，无法识别时使用设置页中的手动备用值（默认 US$200）。套餐名按运行时分表解析——同名套餐在两家厂商价格不同（Claude Pro 为 US$20，ChatGPT Pro 为 US$200），按席位议价的 Team/Enterprise 不做推算而是回退到手动值。
- 缓存写入按 TTL 分档计价：缓存写入不是普通输入，5 分钟档为基础输入价的 1.25 倍、1 小时档为 2 倍，缓存读取为 0.1 倍。倍率随目录版本管理而非逐模型标注，未提供该拆分的数据源（Codex）不受影响。
- 版本化费率目录：设置页可按模型和生效日期维护历史费率版本，记录来源/目录版本，导入、导出或恢复内置目录；历史用量按发生日期重放，未来费率不会回写旧数据。内置目录同时覆盖 OpenAI 与 Anthropic 两条版本线，包含 Claude Fable 5 / Opus 5 / Sonnet 5 / Haiku 4.5 等模型；Sonnet 5 的首发优惠价与到期后的标准价各占一行，按用量发生日期自动切换。导入缺少来源与版本标注的旧目录时，按定价特征还原每行自身的出处，而不是统一打上文档级版本。
- 针对本月等效金额、额度余量和费率覆盖率发送去重通知。
- 深色/浅色玻璃/跟随系统主题，以及紧凑/展开双模式。
- Electron 已支持托盘驻留、关闭隐藏、可配置全局快捷键、开机启动、单实例、自动刷新、紧凑布局和原生主题。
- 顶部悬浮状态条、Windows 通知、桌面底层模式，以及完整的按显示器工作区/DPI 边界恢复仍属于旧 WPF 功能对等基线，尚未迁移。
- 每日 GitHub Release 更新检查（私有仓库通过进程环境变量读取令牌）、发布页跳转，不静默下载或安装。
- 聚合 JSON/CSV 导出、设置与待办备份恢复、脱敏诊断包和非破坏式索引重建。
- 每用户 Inno Setup 安装包：开始菜单、卸载项和可选桌面快捷方式；开机启动由应用内设置统一管理，Release 可按仓库密钥配置进行 Authenticode 签名。
- 浏览器开发模式内置可重复的演示数据，不会影响桌面应用中的真实本地数据。
- Playwright 视觉回归、axe 无障碍扫描与键盘导航测试覆盖深浅主题、主要视图和紧凑布局；失败截图、差异图和 trace 可由 CI 留存。

## 设计方案

Electron 职责边界为：`Electron main → sandbox preload → Vue renderer → .NET Sidecar → Application/Core/Infrastructure`。Electron 自带 Chromium，不依赖系统 WebView2；旧 WPF 宿主仅作为兼容与功能对照基线保留。

### Electron 迁移状态

当前 Windows-first 路径已经打通真实 Vue 页面、.NET 数据与设置服务、私有双向 RPC、原生打开/保存/确认对话框、托盘与关闭驻留、全局快捷键、紧凑窗口、原生主题、单实例恢复，以及由 Sidecar 驱动的后台自动刷新。Electron 启动时先校验后端能力并读取宿主所需设置，应用原生状态后再创建 renderer；用量快照由 Application 层统一投影，手动刷新、自动刷新和数据变更不会各自重复发送同类事件。打包态 smoke 会验证 sandbox preload、`app.initialize` 和反向对话框 RPC，并发送宿主状态；整个过程不会打开窗口、对话框、外链或修改系统设置。

Windows 正式发布链已经改为 Electron：直接使用已修复的 Electron Packager 20，生成带限制性 fuses 的 ASAR，携带 Electron/Chromium、项目、Web 与自包含 .NET 运行时许可文件，再依次签名应用与 Sidecar、执行打包态 smoke、构建并安装/运行/卸载测试 Inno Setup，最后生成 ZIP、Setup 和校验和。发布链完成不等于功能已经完全对等；开机启动失败回滚、窗口工作区/DPI 恢复、Windows 通知、顶部状态条与桌面底层模式仍待迁移，因此首个 Electron 版本适合先作为预发布版验证。Linux 适配尚未开始，当前只验证 Windows。

本机历史 Token 的归一化、active/archive 合并、fork 去重与保守区间说明见 [docs/local-token-ledger.md](docs/local-token-ledger.md)。

原 WPF/WebView2 版本的完整设计与功能对等基线见 [docs/design-plan.md](docs/design-plan.md)；当前 Electron 进程布局、协议和打包约束见 [src/CodexU.Electron/README.md](src/CodexU.Electron/README.md)。

## 环境要求

- Windows 10 22H2 或 Windows 11，推荐 Windows 11。
- .NET SDK 10.0.400（由 `global.json` 精确锁定）。
- Node.js 22.23.2（Electron 构建与 CI/Release 使用同一精确版本）。
- Electron 与下一版本安装包不需要 WebView2 Evergreen Runtime；只有运行仓库中的旧 WPF 宿主时才需要。
- 已安装并登录的 ChatGPT 桌面应用（含 Codex）或 Codex CLI；没有 Codex 数据时应用仍可显示空状态。

## 构建

安装锁定的前端与 Electron 依赖：

```powershell
cd src\CodexU.Web
npm.cmd ci
cd ..\..
cd src\CodexU.Electron
npm.cmd ci
cd ..\..
```

构建整个解决方案：

```powershell
dotnet build CodexU.sln
```

### Electron + .NET Sidecar（发布目标）

先构建 Vue、发布 Windows x64 自包含 Sidecar，再测试并打包 Electron：

```powershell
cd src\CodexU.Web
npm.cmd ci
npm.cmd run build
cd ..\..
dotnet publish src\CodexU.Sidecar\CodexU.Sidecar.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishReadyToRun=false `
  -p:DebugSymbols=false `
  -p:DebugType=None `
  --output src\CodexU.Electron\backend
.\tools\Generate-ThirdPartyInventory.ps1
cd src\CodexU.Electron
npm.cmd ci
npm.cmd audit --audit-level=high
npm.cmd test
npm.cmd run package
cd ..\..
.\tools\Test-PackagedElectron.ps1 `
  -ApplicationDirectory src\CodexU.Electron\out\CodexU-win32-x64
```

`out\CodexU-win32-x64` 是完整便携目录，不能只复制其中的 `CodexU.exe`。正常开发启动使用 `npm.cmd start`。Release 会从同一目录制作 `CodexU-X.Y.Z-win-x64.zip` 和每用户 Inno Setup 安装包；安装包默认写入 `%LOCALAPPDATA%\Programs\codexU`，不要求管理员权限，也不检查 WebView2。更完整的进程布局、协议和打包约束见 [Electron host README](src/CodexU.Electron/README.md)。

### 前端和旧 WPF 宿主

前端独立开发使用 `cd src\CodexU.Web; npm.cmd run dev`，浏览器模式使用可重复的演示数据。需要对照 v0.5.0 行为时，可运行 `dotnet run --project src\CodexU.App\CodexU.App.csproj`；该旧宿主仍需要 WebView2，但不再进入下一版本 Release 产物。

## 测试

```powershell
dotnet test CodexU.sln
cd src\CodexU.Web
npm.cmd run test:unit
npm.cmd run build
npx.cmd playwright install chromium
npm.cmd run test:e2e
cd ..\CodexU.Electron
npm.cmd test
```

前端单元测试用 Vitest，位于 `src\CodexU.Web\tests\unit`，不需要浏览器或构建产物，可用 `npm.cmd run test:unit:watch` 监听、`npm.cmd run test:unit:coverage` 查看覆盖率。时区固定为 `Asia/Tokyo`，与 Playwright 一致，保证跨月投影一类的本地时间断言可复现。

仅更新经过确认的视觉基线时使用 `npm.cmd run test:e2e:update`。无障碍和键盘测试可单独运行 `npm.cmd run test:a11y`。

C# 测试覆盖 token delta、内置与自定义点数费率、按日期选择历史费率、费率目录导入导出、Claude transcript 容错、增量续读、设置与待办持久化、备份恢复、导出隐私、诊断脱敏、更新版本比较、Goals、automation 元数据、本机数据库只读烟雾验证，以及 IPC 方法白名单与宿主 dispatch 分支的一致性。前端单元测试覆盖金额与 token 格式化、等效收益换算与本月投影、费率覆盖率，以及仪表盘 store 的过期响应丢弃、运行时切换竞态、设置草稿合并与各类失败降级。端到端测试覆盖深浅主题截图、axe 扫描和 Tab 键盘模式。

## GitHub 自动化

- `CI` 在 pull request、`main` 推送和手动触发时运行，执行版本与 Electron 许可清单校验、完整 Electron 构建依赖审计、Vue/Electron/C# 测试、格式检查、Playwright 视觉与无障碍测试、Sidecar 自包含发布、Electron 打包态 smoke，以及 Inno Setup 的静默安装、真实 v0.5.0 WPF 同 AppId 原位升级、已安装应用 smoke 和静默卸载。旧安装器下载后会核对固定大小与 SHA-256；pull request 不保存体积较大的 Chromium 产物，`main` 推送和手动触发保留便携 ZIP 与安装包 3 天。产物上传本身不计入成败，避免共享 Actions 存储状态污染代码信号，但失败仍会显示在运行记录中。
- Dependabot 每周一检查 NuGet、Vue npm、Electron npm 和 GitHub Actions 依赖。`THIRD-PARTY-INVENTORY.md` 和 `THIRD-PARTY-LICENSES.txt` 覆盖自包含 .NET 运行时、Sidecar 的 NuGet 依赖、Web 生产 npm 图和实际分发的 Electron 运行时；Electron 包根另保留完整 `LICENSES.chromium.html`。升级这些依赖时，需要先安装两套 npm 依赖并发布 win-x64 自包含 Sidecar，再执行 `./tools/Generate-ThirdPartyInventory.ps1`，把两个生成文件以及同步后的 `LICENSES/dotnet-runtime-*.txt` 一并提交到 PR。
- `Release` 在 `main` 推送和从 `main` 手动触发时读取 `Directory.Build.props` 中的版本。只有对应版本尚未完整发布时，才重新完成同等级验证并创建 `vX.Y.Z` 标签；从其他分支手动触发不会发布。若同名标签已存在，工作流只接受当前 `main` 本身或其祖先提交，并在 checkout 后再次核对提交与版本，防止从旁支或错版本标签发布。
- Release 包含 Electron + Vue + 自包含 .NET Sidecar 的完整 ZIP、每用户 Setup EXE，以及两者各自的 `.sha256` 文件。ZIP 不能只提取其中的 EXE。
- 发布使用 GitHub Actions 自带的 `GITHUB_TOKEN`，不需要个人访问令牌或本机 GitHub CLI。新版本先创建草稿，只有四个资源均上传并验证非空后才公开；中断后可以安全复用草稿。已公开但不完整的 Release 会失败并要求人工审查，不会静默替换公开二进制。
- 若仓库同时配置 `WINDOWS_SIGNING_CERTIFICATE_BASE64` 与 `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`，发布流程会先签名并验证 `CodexU.exe` 与 `CodexU.Sidecar.exe`，再签名并测试 Setup EXE；两者都未配置时会明确产出未签名包，只配置一个则立即失败。
- 若该项目用于符合 Inno Setup 定义的商业场景，请购买许可证并将密钥配置为仓库 Secret `INNO_SETUP_LICENSE_KEY`；仅 Release 会在临时 Runner 的当前用户注册表中激活它，CI 不会使用该许可密钥。

发布新版本时，先修改 `Directory.Build.props` 的 `Version`、`AssemblyVersion` 和 `FileVersion`，并同步 Web/Electron package 与 lockfile，通过 PR 合并到 `main`；自动化会跳过已完整发布的同版本，防止覆盖历史 Release。当前 v0.5.0 因已完整发布而保持原 WPF 资产不变；首个 Electron 版本应使用预发布版本号（计划为 `0.6.0-beta.1`），验证功能对等后再提升为稳定版。

## Claude 额度接入

Claude Code 不在本机保存额度余量：transcript 只有 token 计数没有配额，HTTP 429 也只在撞上限之后才出现。唯一可机读的实时来源是 statusLine 命令收到的 JSON，其中 `rate_limits.five_hour` 与 `rate_limits.seven_day` 提供已用百分比和重置时间。

`tools/claude-statusline-snapshot.mjs` 承担这个角色：照常输出状态行，同时把两个窗口写成 `%LOCALAPPDATA%\codexU\claude-code\statusline-snapshot.json`。把脚本放到 `~/.claude/`，然后在 `%USERPROFILE%\.claude\settings.json` 中配置：

```json
{
  "statusLine": {
    "type": "command",
    "command": "node ~/.claude/claude-statusline-snapshot.mjs",
    "refreshInterval": 30
  }
}
```

**路径必须用正斜杠。** Claude Code 在装有 Git Bash 的 Windows 上通过 Git Bash 执行该命令，而 Git Bash 会静默吞掉未加引号的反斜杠。

几个限制来自 Claude Code 本身，不是本应用：`rate_limits` 只对 Claude.ai 订阅账号（Pro/Max）出现，API key、Bedrock、Vertex 与 Foundry 认证下没有；且要等会话内第一次 API 响应之后才出现，两个窗口也可能各自缺失。脚本在没有任何可用窗口时**不写文件**，以免用一份看似权威的空快照覆盖掉尚且新鲜的数据；应用侧另有过期窗口丢弃逻辑。

statusLine 的 JSON 里**没有套餐名字段**，所以订阅月费无法自动识别，需在设置页手动填写。

## 隐私

- 不上传本机 usage、线程、路径或账户数据。
- 为生成统计，应用会在本机解析必要字段；不保存、不展示、不上传 prompt、回复正文、工具参数、工具输出、附件正文或 auth token。
- 前端没有任意文件、SQL、Shell 或进程权限。
- Electron preload IPC 采用明确方法白名单；Electron 到 .NET Sidecar 的消息使用有大小上限的长度前缀协议。仓库保留的旧 WPF/WebView2 IPC 同样采用白名单。

## 许可

CodexU 采用 [MIT License](LICENSE)，Copyright (c) 2026 yuweiyang9611 and contributors。

本项目保留两个参考项目的 MIT 版权与许可声明。发布的 ZIP 和安装包同时包含依赖清单、完整依赖许可正文以及运行时第三方声明，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、[THIRD-PARTY-INVENTORY.md](THIRD-PARTY-INVENTORY.md) 和 `THIRD-PARTY-LICENSES.txt`。
