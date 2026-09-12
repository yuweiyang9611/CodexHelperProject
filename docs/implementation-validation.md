# 六项实施验证记录

日期：2026-09-12。首次实施代码和本机验证已落地；合并后的主线 CI 已通过安装生命周期。专用 Windows 桌面矩阵仍未验收，因此不声明所有平台验收完成，不发布稳定版。

远端补充证据：[主线 19f7c1c 的 CI](https://github.com/yuweiyang9611/CodexHelperProject/actions/runs/34665919361) 已成功，包含真实 Electron、打包 smoke、视觉/无障碍，以及安装、从 v0.5.0 升级和卸载。下方性能表是优化前记录，不代表后续缓存修复的性能。

后续六项修复及优化后测量见 [correctness-fixes-validation.md](correctness-fixes-validation.md)。

## 环境与产物

- 本机：Windows 11 Pro，10.0.26200，x64。
- .NET SDK：10.0.400，按 `global.json` 使用独立 SDK。
- Node：22.23.2；Web 和 Electron 均以仓库 lockfile 执行 `npm ci`。
- Electron：44.0.0；应用版本维持 0.6.0-beta.2。
- Windows 产物：`src/CodexU.Electron/out/CodexU-win32-x64/`，入口 `CodexU.exe`，需要整个目录。
- 截图：`artifacts/desktop-e2e/status-strip.png`、`artifacts/desktop-e2e/main-recovered.png`。

## 已执行结果

| 检查 | 结果 |
| --- | --- |
| 精确工具链预检、版本一致性 | 通过 |
| C# 整体测试 | 489 通过：Core 181、Contracts 11、Infrastructure 209、Sidecar 88 |
| 解决方案构建（包含 WPF） | 通过 |
| C# 格式检查 | 通过 |
| Web 单元测试和生产构建 | 181 通过，构建成功 |
| Web 视觉、键盘、axe 无障碍 | 40 通过，92 按原有项目规则跳过；未改动视觉基线 |
| Electron 单元测试和 TypeScript 构建 | 97 通过，构建成功 |
| 真实 Electron / preload / Sidecar E2E | 通过 |
| 当前 Windows 11 的真实 Explorer 附着 | 通过，桥接确认父窗口；未重启 Explorer |
| Electron 打包及最终目录 smoke | 通过：`CODEXU_ELECTRON_SMOKE_OK` |
| 依赖许可生成、发布链静态检查、仓库隐私检查 | 通过；40 项依赖许可清单无变更 |

真实桌面测试使用独立临时数据目录和合成 transcript，覆盖运行时切换、设置重启、删除日志后的留存、备份恢复、状态条展开、位置锁定/解锁、预览不保存设置、主窗口隐藏后的刷新、打开待办、Sidecar 退出恢复及 renderer 崩溃恢复。只替代系统文件选择对话框的返回路径，不使用演示 bridge。

打包 smoke 最初发现初始化查询状态条时无交互宿主没有窗口，现已返回安全的隐藏状态，仍不创建状态条、桌面副本或托盘，不打开系统对话框，也不修改系统设置。一次 Windows 文件替换临时拒绝促使恢复检查点增加有限重试；后续全量回归通过。

历史测试覆盖工作区目录边界、子目录和大小写、未知归属、SQLite 回填、跨工作区 fork、active/archive 冲突、子代理开关、项目/模型/趋势与总量一致，以及删除、读取失败、截断、改写、同日新增来源、重启、复制/移动会话、重新计价、旧快照、备份恢复与缓存清理。原有测试同时覆盖恢复失败及事务回滚。真实用户日志 smoke 为显式选择项，本次未启用。

## Claude 性能样本

固定 30,000 行、约 7.68 MB 的合成 transcript。下表是 2026-09-12 一次本机测试的采集/索引阶段结果，完整刷新还包括账本合并和投影；分配字节为测试期间进程累计分配差值，不是峰值工作集，也不作为稳定性能承诺。

| 阶段 | transcript 读取字节 | 解析完整行 | 耗时 ms | 分配字节 |
| --- | ---: | ---: | ---: | ---: |
| 首次解析 | 15,360,020 | 30,000 | 416.73 | 351,440,424 |
| 未变化刷新 | 0 | 0 | 280.59 | 162,701,912 |
| 单文件追加 | 15,360,512 | 1 | 197.97 | 226,325,184 |

追加会验证整个已提交前缀，并更新前缀指纹，所以读取量包含旧内容；只解析新增完整行。相比只校验首尾块，这能识别“中间改写后再追加”。新增专项测试证明这种改写保留原历史并标记冲突。CI 断言读取量及解析次数，不用易受机器影响的耗时阈值判定通过。

## 尚未执行的验收

- Windows 10 22H2 的实际运行与桌面附着。
- 专用 Windows 10/11 VM 中的 Win+D、Explorer 重启、混合 DPI、负坐标多屏、显示器移除及重启找回。

普通 Docker Windows 容器不提供所需的交互式桌面，不能替代上述矩阵；参见 [Microsoft Windows 容器兼容性说明](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/lift-shift-to-containers)。Docker 中承载完整 Windows VM 的方式仍需虚拟机资源、镜像和相应桌面访问。

复现入口为 `tools/Verify-Project.ps1 -Install -Desktop -Package`。可用 `-Dotnet` 指定 SDK，并以 `CODEXU_E2E_PORT` 选择独立 Web 测试端口；真实桌面附着需显式设置 `CODEXU_RUN_DESKTOP_ATTACH_TEST=1`。VM 验收清单见 [usage-history-and-desktop.md](usage-history-and-desktop.md)。
