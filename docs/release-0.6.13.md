# CodexU 0.6.13

本版新增 Antigravity 额度读取开关，并说明额度的读取方式。

- 「设置与诊断 → 数据源」新增「通过 Antigravity CLI 读取额度」，默认开启。关闭后应用不再为额度启动 `agy`，手动刷新也不会查询；Antigravity 页和「三运行时」页会提示已关闭及重新开启的位置。
- 额度由官方 `agy` CLI 自带的只读 `/usage` 命令查询，与在终端里手动输入 `/usage` 相同。codexU 不直接访问 Google 的任何接口，也不读取或转发 Antigravity 的登录凭据；查询只在 Antigravity 额度显示在屏幕上时进行。
- 升级后沿用默认开启；关闭的选择会被保存。

安装版可在应用内检查更新；便携版请解压完整 ZIP。
