# 本机历史 Token 账本

CodexU 可以仅从历史 JSONL 重建本机原始处理 Token，不依赖官方账户统计，也不要求运行实时拦截账本。Token 概览、状态条、趋势和热力图都使用这套本机账本；app-server 仍可读取账户身份与额度窗口，但不会请求 `account/usage/read`。该结果描述“当前设备仍可见日志中的原始处理量”，不是订阅额度或官方计费量。

## 重建流程

1. 枚举 `sessions` 与 `archived_sessions`，逐文件只读取完整 JSONL 行。
2. 对 `token_count` 优先采用 `last_token_usage` 作为单次响应增量；`total_token_usage` 维护字段感知的累计高水位并识别计数器 epoch。累计未推进时忽略重复通知，`last_token_usage` 缺失时才回退累计差。
3. 将规范化事件、双 64 位身份指纹、session/fork 身份和解析状态写入增量索引。缓存只保存物理文件事实，全局去重关系每次读取重新计算。
4. 同一 `session_meta.id` 同时出现在 active/archive 时只选一个 canonical 副本：前缀关系取较长者；内容分叉时不相加，选择较完整副本并把质量标为 `Partial`。
5. `thread_spawn` 有完整结构边界时直接排除复制的父 transcript。边界缺失时，按 `forked_from_id`（或不完整 thread-spawn 的 `parent_thread_id`）查找父 session，并删除父子规范化事件的严格最长公共前缀。
6. 不对无关 session 做全局哈希去重；相同 Token 数可能是两个真实响应。
7. 最后才按事件日期、模型和周期聚合。日桶使用运行程序的本机时区；lifetime 与时区无关。

## 精度与保守区间

结构边界或父子公共前缀可验证时，继承事件会被确定性删除。父文件缺失或公共前缀为零时，CodexU 保留子 session 的用量作为主值，因此主值是保守上界；同时将结果标为 `Partial`，并在诊断中显示可能受影响的 Token 数与 lifetime 下界。

历史 JSONL 没有跨文件全局唯一的 usage/request ID，所以纯离线方法无法数学证明 100% 去重。它仍能消除已知的累计基线放大、重复通知、active/archive 副本和 fork transcript 重放，并对无法判定的部分保持可审计。

父子事件最长公共前缀与 `last_token_usage` 归一化思路借鉴了 MIT 许可的 [shanggqm/codexU v1.1.5](https://github.com/shanggqm/codexU/tree/v1.1.5)；许可声明见仓库根目录的 `THIRD-PARTY-NOTICES.md`。
