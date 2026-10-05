# 自动更新下载重定向修复

用户安装版 0.6.0 可以检测到 0.6.1，但自动下载显示 `Redirect was cancelled`。本机日志记录了同样错误。问题位于 Electron 下载阶段，而不是 .NET Release 检查或版本比较。

## 复现与根因

真实 Electron 44.0.0 中，用本地 HTTP 服务返回 302，即可稳定复现：Node Fetch 的手动模式返回 302／Location；Electron `net.fetch` 的手动模式直接抛出相同错误。更小的前提不涉及 GitHub 账户、代理或网络质量。

Electron 的 [ClientRequest 文档](https://www.electronjs.org/docs/latest/api/client-request) 与 [44.0.0 实现](https://github.com/electron/electron/blob/v44.0.0/lib/common/api/net-client-request.ts)说明，手动跳转事件没有同步调用 `followRedirect` 或 `abort` 时会被取消。旧 updater 期待标准 Fetch 的 302 响应，因此其 Location 检查尚未执行，底层请求就失败了。

## 修复

仅替换 updater 的网络适配器：使用 `net.request` 接收重定向事件，同步转换为带 Location 的 Response 后终止当前请求。由原 `githubFetch` 验证白名单、限制跳转次数、隔离 API 令牌，再发起下一跳。正常内容按流交付，保留背压与取消；窗口文件资源继续使用原 `net.fetch`。

不采用自动跟随跳转：那会在检查白名单前访问重定向目标。也不替换为 Node Fetch，以保留 Chromium 网络栈与系统代理行为。

## 验证与更新

旧单元测试模拟 Node Response，桌面测试为未打包应用，打包 smoke 跳过 updater；这些测试无法发现 Electron 的实际手动跳转语义。新增独立、无窗口的真实 Electron 网络回归，使用临时合成资源与本地 HTTP 服务，接入 CI 和 Release。

旧 0.6.0／0.6.1 安装版需手动安装修复包一次。测试与诊断没有执行用户的安装器、修改安装目录或注册表。

## 回归结果

- Node 22.23.2 启动真实 Electron 44：旧模式在本地 302 夹具稳定失败，精确输出 `Redirect was cancelled`；新适配器 12 类网络检查通过，Electron 单元测试 118 项通过。
- 网络回归覆盖手动跳转、HTTP 状态、可信 CDN、拒绝未知域名／API 跳转、令牌与 cookie 边界、禁用缓存、各阶段取消、正文截断、更新就绪和半成品清理。
- 64 MiB、明确长度、`application/octet-stream`／attachment 二进制响应中，暂停读取后仅传输约 4.6 MiB，完整传输尚未结束；正文队列按字节限制为 64 KiB。
- 真实 GitHub `v0.6.1` 校验文件与安装包均经 302 → CDN 200 下载成功。安装包为 136,670,133 字节，达到 `ready / 100%`，发布大小、校验文件、GitHub digest 与独立 SHA-256 重算一致；没有启动安装器。
- 适配器不把请求 Writable 的提前 `close` 当作响应终止；原生取消带幂等保护，避免多个取消／流关闭事件重复取消同一下载。
- 修复随 0.6.2 发布，更新白名单与安装前二次校验保持原约束。版本一致性、发布防护和差异空白检查通过。
