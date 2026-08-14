# codexU Windows

[![CI](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/ci.yml/badge.svg)](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/ci.yml)
[![Release](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/release.yml/badge.svg)](https://github.com/yuweiyang9611/CodexHelperProject/actions/workflows/release.yml)

codexU Windows 参考 [shanggqm/codexU](https://github.com/shanggqm/codexU) 的产品设计，并吸收 [liu1198767931-bit/codexU-windows](https://github.com/liu1198767931-bit/codexU-windows) 的 Windows 功能边界。应用使用 WPF/.NET 10 作为桌面宿主，WebView2 + Vue 3 负责高保真界面，C# 服务只读聚合本机 Codex 数据。

## 当前能力

- WPF/.NET 10 无边框桌面宿主与 Windows 11 Mica 降级支持。
- WebView2 安全虚拟主机与类型化 JSON IPC。
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
- 可选 Codex 顶部悬浮状态条，独立于 WebView 刷新本机数据；支持即时快照回放、手动重试、悬停临时展开、点击/触摸/键盘固定展开，并按当前显示器工作区与 DPI 自适应。
- 额度阈值 Windows 通知、开机启动、单实例、自动刷新和桌面底层模式。
- 托盘驻留、关闭隐藏、可配置全局快捷键、状态条额度口径和窗口置顶 IPC。
- 每日 GitHub Release 更新检查（私有仓库通过进程环境变量读取令牌）、发布页跳转，不静默下载或安装。
- 聚合 JSON/CSV 导出、设置与待办备份恢复、脱敏诊断包和非破坏式索引重建。
- 每用户 Inno Setup 安装包：开始菜单、卸载项、可选桌面快捷方式和可选开机启动；Release 可按仓库密钥配置进行 Authenticode 签名。
- 浏览器开发模式内置可重复的演示数据，不会影响 WPF 中的真实本地数据。
- Playwright 视觉回归、axe 无障碍扫描与键盘导航测试覆盖深浅主题、主要视图和紧凑布局；失败截图、差异图和 trace 可由 CI 留存。

## 设计方案

本机历史 Token 的归一化、active/archive 合并、fork 去重与保守区间说明见 [docs/local-token-ledger.md](docs/local-token-ledger.md)。

完整设计和验收标准见 [docs/design-plan.md](docs/design-plan.md)。

## 环境要求

- Windows 10 22H2 或 Windows 11，推荐 Windows 11。
- .NET SDK 10。
- Node.js 22 或更新版本。
- WebView2 Evergreen Runtime。
- 已安装并登录的 ChatGPT 桌面应用（含 Codex）或 Codex CLI；没有 Codex 数据时应用仍可显示空状态。

## 构建

首次安装前端依赖：

```powershell
cd src\CodexU.Web
npm.cmd install
cd ..\..
```

构建整个解决方案：

```powershell
dotnet build CodexU.sln
```

`CodexU.App` 的 MSBuild target 会自动执行 Vue 生产构建，并把 `dist` 复制到应用输出目录的 `web` 文件夹。

运行：

```powershell
dotnet run --project src\CodexU.App\CodexU.App.csproj
```

生成可分发的 Windows x64 自包含目录：

```powershell
dotnet publish src\CodexU.App\CodexU.App.csproj -p:PublishProfile=WinX64
```

发布目录中的 `CodexU.App.exe` 必须与同目录 DLL、`runtimes` 和 `web` 文件夹一起分发；它不是可以单独复制的单文件 EXE。WebView2 浏览器数据固定写入 `%LOCALAPPDATA%\codexU\WebView2`，不会尝试写入安装目录。

正式 Release 同时提供每用户安装包 `CodexU-X.Y.Z-win-x64-setup.exe`。安装包默认写入 `%LOCALAPPDATA%\Programs\codexU`，不要求管理员权限；安装前会按微软公布的 `pv` 注册表位置检查 WebView2 Evergreen Runtime，缺失时停止安装并打开微软官方下载页。

前端独立开发：

```powershell
cd src\CodexU.Web
npm.cmd run dev
```

浏览器开发模式使用演示快照；WPF/WebView2 模式自动使用 C# 本地服务。

## 测试

```powershell
dotnet test CodexU.sln
cd src\CodexU.Web
npm.cmd run test:unit
npm.cmd run build
npx.cmd playwright install chromium
npm.cmd run test:e2e
```

前端单元测试用 Vitest，位于 `src\CodexU.Web\tests\unit`，不需要浏览器或构建产物，可用 `npm.cmd run test:unit:watch` 监听、`npm.cmd run test:unit:coverage` 查看覆盖率。时区固定为 `Asia/Tokyo`，与 Playwright 一致，保证跨月投影一类的本地时间断言可复现。

仅更新经过确认的视觉基线时使用 `npm.cmd run test:e2e:update`。无障碍和键盘测试可单独运行 `npm.cmd run test:a11y`。

C# 测试覆盖 token delta、内置与自定义点数费率、按日期选择历史费率、费率目录导入导出、Claude transcript 容错、增量续读、设置与待办持久化、备份恢复、导出隐私、诊断脱敏、更新版本比较、Goals、automation 元数据、本机数据库只读烟雾验证，以及 IPC 方法白名单与宿主 dispatch 分支的一致性。前端单元测试覆盖金额与 token 格式化、等效收益换算与本月投影、费率覆盖率，以及仪表盘 store 的过期响应丢弃、运行时切换竞态、设置草稿合并与各类失败降级。端到端测试覆盖深浅主题截图、axe 扫描和 Tab 键盘模式。

## GitHub 自动化

- `CI` 在 pull request、`main` 推送和手动触发时运行，执行版本/许可清单校验、前端单元测试、Release 构建、格式检查、C# 单元测试、Playwright 视觉与无障碍测试、自包含发布、真实 EXE 冒烟测试和 Inno Setup 安装包编译验证；前端单元测试排在浏览器下载和 .NET 构建之前，让展示层回归在数秒内暴露。失败时上传 Playwright 截图差异与 trace。pull request 不保存 74 MB 的构建产物（构建、冒烟测试和安装包验证照常执行，只是不留存）；`main` 推送和手动触发保留 3 天，可直接下载。Actions 存储按账号计费并由所有仓库共享，这样可避免单个仓库的 PR 挤占其他项目的构建配额。产物上传本身不计入成败：走到这一步时构建、校验、冒烟测试和安装包编译都已通过，存不下只是共享存储被占满（GitHub 每 6–12 小时才重算用量，空间释放后仍可能被拒绝数小时），把它记为构建失败等于用基础设施状态污染代码信号。该步骤仍会在运行记录里显示为失败，真正的上传故障不会被掩盖。
- Dependabot 每周一检查 NuGet、npm 和 GitHub Actions 依赖。`THIRD-PARTY-INVENTORY.md` 和 `THIRD-PARTY-LICENSES.txt` 只覆盖随应用分发的依赖，因此开发期依赖升级不会触发清单校验；升级生产依赖时需要重新运行 `./tools/Generate-ThirdPartyInventory.ps1` 并把两个生成文件一并提交到该 PR 分支。
- `Release` 在 `main` 推送和从 `main` 手动触发时读取 `Directory.Build.props` 中的版本。只有对应版本尚未完整发布时，才重新完成同等级验证并创建 `vX.Y.Z` 标签；从其他分支手动触发不会发布。若同名标签已存在，工作流只接受当前 `main` 本身或其祖先提交，并在 checkout 后再次核对提交与版本，防止从旁支或错版本标签发布。
- Release 包含完整自包含 ZIP、每用户 Setup EXE，以及两者各自的 `.sha256` 文件。ZIP 不能只提取其中的 EXE。
- 发布使用 GitHub Actions 自带的 `GITHUB_TOKEN`，不需要个人访问令牌或本机 GitHub CLI。草稿、资源上传和正式发布分阶段执行；中断后重新运行工作流会修复缺失资源。
- 若仓库配置 `WINDOWS_SIGNING_CERTIFICATE_BASE64` 与 `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`，发布流程会签名并验证应用 EXE 和 Setup EXE；未配置时会明确产出未签名包，不会伪装为已签名。
- 若该项目用于符合 Inno Setup 定义的商业场景，请购买许可证并将密钥配置为仓库 Secret `INNO_SETUP_LICENSE_KEY`；仅 Release 会在临时 Runner 的当前用户注册表中激活它，CI 不会使用该许可密钥。

发布新版本时，先修改 `Directory.Build.props` 的 `Version`、`AssemblyVersion` 和 `FileVersion`，通过 PR 合并到 `main`；自动化会跳过已完整发布的同版本，防止覆盖历史 Release。

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
- WebView2 IPC 采用明确方法白名单。

## 许可

CodexU 采用 [MIT License](LICENSE)，Copyright (c) 2026 yuweiyang9611 and contributors。

本项目保留两个参考项目的 MIT 版权与许可声明。发布的 ZIP 和安装包同时包含依赖清单、完整依赖许可正文以及运行时第三方声明，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、[THIRD-PARTY-INVENTORY.md](THIRD-PARTY-INVENTORY.md) 和 `THIRD-PARTY-LICENSES.txt`。
