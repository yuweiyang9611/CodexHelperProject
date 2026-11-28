# CodexU 0.6.14

本版把 Antigravity 的本机用量并入「三运行时」页，并修复一个启动卡住的情况。

- 「三运行时」页的今日、近 7 日、本月 Token 合计和「项目合并排行」现在包含 Antigravity。同一项目文件夹下的 Codex、Claude Code 与 Antigravity 用量合并为一行。
- 用量来自 Antigravity 保存在本机的对话记录，包括通过 T3 Code 运行 Antigravity 时的记录。应用只读取这些文件，不启动 `agy`，不访问网络，也不读取登录凭据。
- Gemini 模型暂无内置费率，其 Token 计入用量但不计入费用；费用估算下限、订阅月费与耗尽预测仍只含 Codex 与 Claude Code。可在「设置与诊断」的费率编辑器中自行添加 Gemini 费率。通过 Antigravity 调用的 Claude 模型按已有的 Claude 费率计算。
- Antigravity 没有记录每次调用的时间，一段对话的用量按它最后修改的日期计入，跨午夜的对话会全部算在后一天。
- 修复：界面页面加载既不完成也不失败时，启动会一直停住。现在加载超过 30 秒会自动重试，多次失败后提示。

安装版可在应用内检查更新；便携版请解压完整 ZIP。
