# 启用仓库 Git Hooks

`.githooks` 目录会随仓库一起克隆，但 Git 出于安全原因不会自动启用仓库中的 hooks。每次在新设备或新的工作目录克隆本仓库后，需要执行一次初始化脚本。

## Windows PowerShell

在项目根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Enable-GitHooks.ps1
```

如果安装了 PowerShell 7，也可以运行：

```powershell
pwsh -NoProfile -File ./tools/Enable-GitHooks.ps1
```

脚本可以重复执行。它会：

1. 确认当前文件属于有效的 Git 工作目录；
2. 确认 `.githooks/pre-push` 已随仓库克隆；
3. 写入仓库级配置 `core.hooksPath=.githooks`；
4. 验证 Git 实际解析到的 hook 路径正确。

## 配置隐私安全的提交身份

`pre-push` hook 会拒绝作者或提交者使用私人邮箱的提交。请先在 GitHub 的邮箱设置中启用隐私保护，并将该页面提供的 noreply 邮箱配置到当前仓库：

```powershell
git config --local user.email "<id>+<username>@users.noreply.github.com"
git config --local user.useConfigOnly true
```

不要照抄占位符；请使用 GitHub 为你的账户显示的真实 noreply 地址。

## 验证

查看当前仓库的 hooks 配置：

```powershell
git config --local --get core.hooksPath
git rev-parse --path-format=absolute --git-path hooks/pre-push
```

手动运行与推送前相同的隐私检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-RepositoryPrivacy.ps1 -RequireSafeLocalConfig -ScanAllLocalBranches
```

成功后，正常执行 `git push` 时会自动运行 `.githooks/pre-push`。如果 hooks 未启用，GitHub Actions 的 Privacy Guard 仍会检查公开提交，但它只能在内容推送后报告问题，不能替代本地阻止。

## 手动配置

如果无法运行初始化脚本，可以在项目根目录手动执行：

```powershell
git config --local core.hooksPath .githooks
```

在 macOS 或 Linux 上使用此方式时，还需要安装 PowerShell 7（`pwsh`），因为当前隐私检查器是 PowerShell 脚本。
