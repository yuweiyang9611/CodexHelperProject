# CodexU 0.6.1

以 0.6.0 为基础递增补丁版本。Windows x64 安装版和便携版包含 Electron、Vue 界面及自包含 .NET Sidecar。

## 本次更新

- 更新模型费率目录为 `2026.10.1`：支持 GPT-6.1 Sol、GPT-Rosalind-Research，以及 Claude Fable／Mythos 5.1、Opus／Sonnet 5.5；按各自真实发布或计费日期核算，并纠正官方已取消的 Sonnet 5 涨价。
- 重整总览、用量分析和设置，统一日期、模型及项目筛选；支持全部留存历史、会话分组和已确认子代理贡献下钻，未知金额及历史差额单独说明。
- 同一刷新轮次复用查询数据，翻页复用汇总；快速筛选只保留进行中查询和最新条件，减少重复读取和计算。
- 新增当前筛选 JSON／CSV 导出，包含全部匹配用量、筛选范围及实际适用费率；原有全部历史导出保持兼容。
- 改进历史分类回填与费率提示，统一 Electron 退出、更新安装及维护确认，补齐失败处理。
- 更新 Electron 构建依赖 `undici` 和 `brace-expansion` 的安全补丁，依赖审计通过。

模型价格与固定旧目录修正方式见 [2026-10-05 费率更新](rate-catalog-2026-10-05.md)。用户自定义费率与固定导入目录保留自身设置。

## 下载与更新

- `CodexU-0.6.1-win-x64-setup.exe`：每用户安装版。已有支持自动更新的安装版可在设置中检查更新，或使用安装包升级。
- `CodexU-0.6.1-win-x64.zip`：便携版，请解压完整目录后运行 `CodexU.exe`。
- 发布同时提供两份 `.sha256` 校验文件。GitHub Release 上传并验证四个资源后才会公开。

支持目标为 Windows 10 22H2／Windows 11。原生混合 DPI、多屏移除、Win+D 与 Explorer 重启验收仍按 [既有矩阵](windows-acceptance.md) 留待专用环境执行。
