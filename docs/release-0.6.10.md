# CodexU 0.6.10

新增「Antigravity」页，通过 Google 官方 Antigravity CLI 查看订阅额度。打开页面时会查询一次，也可点击「刷新额度」。

- 按 CLI 返回的模型组和额度窗口显示剩余百分比或数量，并显示重置时间、倒计时和查询时间。
- 区分额度已用完、窗口未启用和数据未知。找不到 CLI、版本过旧、未返回额度或查询失败时，页面会显示对应提示。
- 额度查询在后台运行，使用 CLI 的只读 `/usage` 命令，无需消耗模型 Token。

请使用 Google 官方 `agy` 1.1.11 或以上版本，并在 CLI 中登录有额度的 Google 账号。Windows 安装和查询说明见 [Antigravity 额度](../README.md#antigravity-额度)。

安装版可在应用内检查更新，便携版请解压完整 ZIP。
