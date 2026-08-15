# Contributing and privacy

This is a public repository. Do not include personal email addresses or other private contact details in commits, commit messages, source files, pull requests, issues, reviews, comments, screenshots, logs or test fixtures.

## Git identity

Enable **Keep my email addresses private** and **Block command line pushes that expose my email** in GitHub email settings. Configure this repository with the GitHub-provided noreply address shown on that page:

```powershell
git config --local user.email "<id>+<username>@users.noreply.github.com"
git config --local user.useConfigOnly true
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Enable-GitHooks.ps1
```

The hook directory is versioned, but Git does not enable repository hooks automatically after clone. See [GIT-HOOKS.md](GIT-HOOKS.md) for setup, verification and manual fallback instructions.

Before committing, verify that the effective address ends in `@users.noreply.github.com`:

```powershell
git config --get user.email
```

The privacy guard rejects commits whose author or committer email is not a GitHub noreply address. It also rejects email addresses in commit messages, ordinary repository files, and GitHub PR/Issue event text. Third-party license documents and reserved `example.com`/`example.test` test addresses are the only content exceptions.

Automated checks cannot prevent someone from deliberately typing private data into a public GitHub form: the text becomes public before a workflow can report it. Review all text and attachments before submitting them.
