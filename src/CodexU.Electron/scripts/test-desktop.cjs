// Real preload + renderer + sidecar. Only native file pickers are substituted.
const { _electron } = require(require.resolve('playwright', { paths: [require('node:path').resolve(__dirname, '../../CodexU.Web')] }));
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');

(async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'codexu-real-desktop-'));
  const data = path.join(root, 'data'); const codex = path.join(root, 'codex'); const claude = path.join(root, 'claude');
  const artifacts = path.resolve(__dirname, '../../..', 'artifacts', 'desktop-e2e');
  await fs.mkdir(data); await fs.mkdir(path.join(codex, 'sessions'), { recursive: true });
  await fs.mkdir(path.join(claude, 'projects'), { recursive: true }); await fs.mkdir(artifacts, { recursive: true });
  await fs.writeFile(path.join(data, 'settings.json'), JSON.stringify({ codexHome: codex, statusStripEnabled: true,
    autoRefreshMinutes: 60, startAtLogin: false, desktopMode: false }));
  const transcript = path.join(claude, 'projects', 'session.jsonl');
  const subagents = path.join(claude, 'projects', 'subagents');
  await fs.mkdir(subagents);
  const childTranscript = path.join(subagents, 'agent-worker.jsonl');
  await fs.writeFile(transcript, JSON.stringify({ type: 'assistant', sessionId: 'desktop-e2e', cwd: root, timestamp: new Date().toISOString(),
    message: { model: 'claude-opus-5', usage: { input_tokens: 80, output_tokens: 20,
      cache_creation_input_tokens: 0, cache_read_input_tokens: 0 } } }) + '\n');
  await fs.writeFile(childTranscript, JSON.stringify({ type: 'assistant', sessionId: 'desktop-e2e', agentId: 'worker', cwd: root, timestamp: new Date().toISOString(),
    message: { model: 'claude-opus-5', usage: { input_tokens: 15, output_tokens: 5 } } }) + '\n');
  let application;
  async function launch() {
    application = await _electron.launch({ executablePath: require('electron'), args: [path.resolve(__dirname, '..')],
      env: { ...process.env, CODEXU_DATA_DIRECTORY: data, CODEX_HOME: codex, CODEXU_CLAUDE_DIRECTORY: claude,
        CODEXU_ELECTRON_USER_DATA_DIRECTORY: path.join(root, 'electron') }, timeout: 60000 });
    let page;
    const readyDeadline = Date.now() + 60000;
    while (Date.now() < readyDeadline) {
      page = application.windows().find(p => p.url().startsWith('app://codexu/') && !p.url().includes('surface='));
      if (page) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert(page, 'main window exists');
    await page.waitForFunction(() => Boolean(window.codexU), { timeout: 60000 });
    await page.locator('.overview-grid').waitFor({ timeout: 60000 });
    return page;
  }
  const request = (page, method, payload = {}) => page.evaluate(({ method, payload }) => window.codexU.request(method, payload), { method, payload });
  const query = page => request(page, 'usage.query', { runtime: 'claudeCode' });
  function assertGroupedUsage(analysis, source) {
    assert.equal(analysis.totals.tokens, 120);
    assert.equal(analysis.sessionCount, 1);
    assert.equal(analysis.sessions.length, 1);
    const group = analysis.sessions[0];
    assert.equal(group.totals.tokens, 120);
    assert.equal(group.members.length, 2);
    assert.equal(group.members.reduce((sum, member) => sum + member.totals.tokens, 0), group.totals.tokens);
    assert.equal(group.members.flatMap(member => member.contributions).reduce((sum, item) => sum + item.tokens, 0), 120);
    assert.equal(analysis.models.reduce((sum, model) => sum + model.totals.tokens, 0), 120);
    assert.equal(analysis.features.reduce((sum, feature) => sum + feature.totals.tokens, 0), 120);
    assert.equal(analysis.days.reduce((sum, day) => sum + day.totals.tokens, 0), 120);
    if (source) assert(group.members.every(member => member.sources.every(value => value === source)));
  }
  try {
    let page = await launch();
    const capabilities = await request(page, 'app.initialize');
    assert(capabilities.capabilities.includes('statusStripControl'));
    const draftRate = { model: 'gpt-5.2', inputCreditsPerMillion: 17, cachedInputCreditsPerMillion: 2,
      outputCreditsPerMillion: 64, effectiveFrom: '2026-10-01', matchMode: 'exact' };
    const draftPayload = { rates: [draftRate], model: 'gpt-5.2-codex-latest', effectiveOn: '2026-10-05' };
    const preview = await request(page, 'rates.previewDraft', draftPayload);
    assert.equal(preview.validationMessage ?? null, null);
    assert.equal(preview.effectiveRate.inputCreditsPerMillion, 17);
    assert.notEqual(preview.builtInRate.inputCreditsPerMillion, 17);
    const duplicate = await request(page, 'rates.previewDraft', { ...draftPayload,
      rates: [draftRate, { ...draftRate, model: 'gpt-5.2-codex-latest' }] });
    assert(duplicate.validationMessage);
    assert.equal((await request(page, 'settings.get')).customModelRates.length, 0);
    await request(page, 'runtime.select', { runtime: 'claudeCode' });
    const initial = await request(page, 'usage.refresh'); assert.equal(initial.tokens.lifetime.tokens, 120);
    const detail = await query(page);
    assertGroupedUsage(detail, 'live');
    assert.equal(initial.tokens.month.creditsUsed, detail.totals.creditsUsed);
    assert.equal(initial.tokens.month.unratedTokens, 20);
    assert(initial.monthlyValue.knownAmount > 0);
    assert.equal(initial.monthlyValue.isLowerBound, true);
    assert.equal(initial.monthlyValue.subscriptionSource, 'unknown');
    await request(page, 'settings.update', { patch: { claudeAutoDetectSubscriptionAmount: false, claudeMonthlySubscriptionAmount: 100 } });
    const combined = await request(page, 'usage.getCombined');
    const manual = combined.claudeCode.snapshot.monthlyValue;
    assert.equal(manual.subscriptionAmount, 100);
    assert.equal(manual.paybackStatus, 'unknown');
    assert.equal(manual.netAmount ?? null, null);
    assert.equal(combined.claudeCode.contribution, 'present');
    assert.equal(combined.summary.periods.month.tokens, 120);
    assert.equal(combined.summary.cost.knownAmount, manual.knownAmount);
    assert.equal(combined.summary.cost.isLowerBound, true);
    await page.locator('#tab-combined').click();
    const combinedCosts = page.locator('.combined-card').filter({ has: page.getByRole('heading', { name: '合计成本', exact: true }) });
    await combinedCosts.getByText(/≥/).first().waitFor();
    await page.locator('#tab-usage').click();
    const filtered = await request(page, 'usage.query', { runtime: 'claudeCode', model: 'claude-opus-5', project: root });
    assertGroupedUsage(filtered, 'live');
    assert(capabilities.capabilities.includes('desktopMode'));
    if (process.env.CODEXU_RUN_DESKTOP_ATTACH_TEST === '1') {
      await page.evaluate(() => { window.desktopTestStates = []; window.codexU.onEvent((method, state) => {
        if (method === 'desktop.stateChanged') window.desktopTestStates.push(state);
      }); });
      await request(page, 'settings.update', { patch: { desktopMode: true } });
      try { await page.waitForFunction(() => window.desktopTestStates.some(state => state.attached), null, { timeout: 45000 }); }
      catch (error) { console.error('Desktop attachment failed in the isolated test environment'); throw error; }
      await request(page, 'settings.update', { patch: { desktopMode: false } });
    }
    const state = await request(page, 'statusStrip.recover'); assert.equal(state.visible, true);
    assert.equal(state.positionMode, 'automatic');
    const strip = application.windows().find(p => p.url().includes('surface=strip')); assert(strip);
    await strip.getByRole('button', { name: '展开或折叠状态条' }).click();
    await strip.getByRole('button', { name: '打开主界面' }).waitFor();
    await strip.getByText(/^近 7 天/).waitFor();
    await strip.getByText(/^累计/).waitFor();
    await strip.getByRole('button', { name: '锁定位置', exact: true }).click();
    await strip.getByRole('button', { name: '解锁位置', exact: true }).waitFor();
    assert.equal((await request(page, 'statusStrip.getState')).positionLocked, true);
    await strip.getByRole('button', { name: '解锁位置', exact: true }).click();
    await strip.getByRole('button', { name: '锁定位置', exact: true }).waitFor();
    await application.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows().find(w => !w.webContents.getURL().includes('surface='))?.hide();
    });
    await strip.getByRole('button', { name: '刷新', exact: true }).click();
    await strip.getByRole('button', { name: '刷新', exact: true }).waitFor();
    assert(await application.evaluate(({ BrowserWindow }) => !BrowserWindow.getAllWindows().find(w => !w.webContents.getURL().includes('surface='))?.isVisible()));
    assert.equal(await strip.getByRole('button', { name: /^待办/ }).count(), 0);
    await strip.getByRole('button', { name: '打开主界面', exact: true }).click();
    await page.locator('#panel-usage').waitFor({ timeout: 15000 });
    assert.equal((await request(page, 'statusStrip.preview', { patch: { statusStripShowTodayTokens: false } })).visible, true);
    await strip.getByText('今日 Token', { exact: false }).waitFor({ state: 'hidden' });
    assert.equal((await request(page, 'settings.get')).statusStripShowTodayTokens, true, 'preview does not overwrite the saved today-token choice');
    await request(page, 'statusStrip.preview', { patch: { statusStripShowTodayTokens: true } });
    await strip.screenshot({ path: path.join(artifacts, 'status-strip.png') });
    await request(page, 'settings.update', { patch: { theme: 'light' } });
    const aggregateExport = path.join(root, 'all-history.json');
    await application.evaluate(({ dialog }, destination) => {
      dialog.showSaveDialog = async () => ({ canceled: false, filePath: destination });
    }, aggregateExport);
    assert.equal((await request(page, 'data.exportAggregates', { format: 'json' })).success, true);
    const exported = JSON.parse(await fs.readFile(aggregateExport, 'utf8'));
    assert.equal(exported.schemaVersion, 2);
    assert.equal(exported.scope, '当前工具全部本机历史');
    assert.equal(exported.runtime, 'claudeCode');
    assert.equal(exported.totals.tokens, 120);
    assert.equal(exported.dailyUsage.reduce((sum, day) => sum + day.totals.tokens, 0), 120);
    assert.equal('sessions' in exported, false, 'aggregate export excludes session details');
    const strings = value => typeof value === 'string' ? [value]
      : value && typeof value === 'object' ? Object.values(value).flatMap(strings) : [];
    assert(strings(exported).every(value => !value.includes(root)), 'aggregate export excludes full project paths');
    assert(strings(exported).every(value => !value.includes('session:desktop-e2e')), 'aggregate export excludes session identities');
    const selectedExport = path.join(root, 'selected.json');
    await application.evaluate(({ dialog }, destination) => {
      dialog.showSaveDialog = async () => ({ canceled: false, filePath: destination });
    }, selectedExport);
    assert.equal((await request(page, 'data.exportAggregates', {
      format: 'json', query: { runtime: 'claudeCode', model: 'claude-opus-5' },
    })).success, true);
    const selected = JSON.parse(await fs.readFile(selectedExport, 'utf8'));
    assert.equal(selected.schemaVersion, 3);
    assert.equal(selected.filters.model, 'claude-opus-5');
    assert.equal(selected.totals.tokens, 120);
    assert(Array.isArray(selected.appliedRates));
    assert(strings(selected).every(value => !value.includes(root)));
    await application.evaluate(({ dialog }) => {
      dialog.showSaveDialog = async () => ({ canceled: true });
    });
    await page.locator('#tab-diagnostics').click();
    const binding = page.locator('.desktop-usage-binding');
    const selectedTranscript = path.join(root, '11111111-2222-4333-8444-555555555555.jsonl');
    await fs.writeFile(selectedTranscript, '');
    await application.evaluate(({ dialog }, selection) => {
      dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [selection] });
    }, selectedTranscript);
    await binding.locator('summary').click();
    await binding.getByRole('button', { name: '选择文件' }).click();
    await binding.getByRole('button', { name: '验证绑定' }).click();
    await binding.getByRole('status').filter({ hasText: '尚未找到查询结果' }).waitFor();
    assert.equal(await binding.getByLabel('Claude Desktop 额度会话文件').inputValue(), selectedTranscript);
    assert.equal((await request(page, 'settings.get')).claudeDesktopUsageTranscript ?? null, null);
    await request(page, 'settings.update', { patch: { claudeDesktopUsageTranscript: selectedTranscript } });
    const bound = (await request(page, 'usage.refresh')).claudeQuotaState;
    assert.equal(bound.queryStatus, 'missingResult');
    assert.equal(bound.action, 'query');
    assert(!JSON.stringify(bound).includes(root));
    await binding.locator('summary').click();
    await page.getByRole('button', { name: '导出 CSV 日报', exact: true }).click();
    await page.getByRole('status').filter({ hasText: '已取消导出' }).waitFor();
    assert.equal(await page.getByRole('button', { name: '导出 JSON 统计', exact: true }).isEnabled(), true);
    // An empty filtered CSV still carries metadata through the real renderer/preload/Sidecar bridge.
    const emptyExport = path.join(root, 'empty.csv');
    await application.evaluate(({ dialog }, destination) => {
      dialog.showSaveDialog = async () => ({ canceled: false, filePath: destination });
    }, emptyExport);
    assert.equal((await request(page, 'data.exportAggregates', {
      format: 'csv', query: { runtime: 'claudeCode', from: '2099-01-01' },
    })).success, true);
    const emptyCsv = await fs.readFile(emptyExport, 'utf8');
    assert(emptyCsv.includes('metadata,'));
    assert(!emptyCsv.includes('day,'));
    await page.locator('#tab-usage').click();
    const backup = path.join(root, 'backup.json');
    await application.evaluate(({ dialog }, backup) => {
      dialog.showSaveDialog = async () => ({ canceled: false, filePath: backup });
      dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [backup] });
      dialog.showMessageBox = async () => ({ response: 1, checkboxChecked: false });
    }, backup);
    assert.equal((await request(page, 'data.backup')).success, true);
    await request(page, 'settings.update', { patch: { theme: 'dark' } });
    assert.equal((await request(page, 'data.restore')).success, true);
    assert.equal((await request(page, 'settings.get')).theme, 'light');
    const backendProbe = path.join(root, 'backend-recovery.json');
    await application.evaluate(({ BrowserWindow }, probe) => {
      const fs = process.getBuiltinModule('node:fs');
      const cp = process.getBuiltinModule('node:child_process');
      const output = cp.execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        `(Get-CimInstance Win32_Process -Filter "ParentProcessId = ${process.pid} AND Name = 'CodexU.Sidecar.exe'").ProcessId`],
        { windowsHide: true, encoding: 'utf8', timeout: 10000 });
      const pid = Number(output.trim());
      if (!Number.isSafeInteger(pid) || pid <= 0) throw new Error('Cannot identify this test application sidecar.');
      const main = BrowserWindow.getAllWindows().find(w => !w.webContents.getURL().includes('surface='));
      main.webContents.once('did-finish-load', async () => {
        try {
          const result = await main.webContents.executeJavaScript('window.codexU.request("runtime.select", {runtime:"claudeCode"})');
          fs.writeFileSync(probe, JSON.stringify({ recovered: true, tokens: result.tokens.lifetime.tokens }));
        } catch (error) { fs.writeFileSync(probe, JSON.stringify({ error: String(error) })); }
      });
      setTimeout(() => process.kill(pid), 300);
    }, backendProbe);
    let backendResult;
    const backendDeadline = Date.now() + 60000;
    while (Date.now() < backendDeadline) {
      try { backendResult = JSON.parse(await fs.readFile(backendProbe, 'utf8')); break; } catch { }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert.equal(backendResult?.recovered, true, 'sidecar restarted and answered a real request');
    assert.equal(backendResult.tokens, 120);
    await fs.unlink(transcript);
    await fs.unlink(childTranscript);
    await application.close(); application = undefined;
    page = await launch();
    assert.equal((await request(page, 'statusStrip.getState')).positionMode, 'automatic');
    assert.equal((await request(page, 'settings.get')).theme, 'light');
    await request(page, 'runtime.select', { runtime: 'claudeCode' });
    assert.equal((await request(page, 'usage.refresh')).tokens.lifetime.tokens, 120);
    assertGroupedUsage(await query(page), 'retained');
    const probe = path.join(root, 'recovery-result.json');
    const applicationProcess = application.process();
    await application.evaluate(({ BrowserWindow, app }, { probe, screenshot }) => {
      const fs = process.getBuiltinModule('node:fs');
      const main = BrowserWindow.getAllWindows().find(w => !w.webContents.getURL().includes('surface='));
      main.webContents.once('did-finish-load', async () => {
        try {
          const result = await main.webContents.executeJavaScript('window.codexU.request("usage.refresh")');
          await main.webContents.executeJavaScript(`new Promise((resolve, reject) => {
            const until = Date.now() + 30000;
            const check = () => {
              if (document.querySelector('.overview-grid') && !document.querySelector('.page-loading')) return resolve(true);
              if (Date.now() > until) return reject(new Error('Recovered UI did not finish initialization'));
              setTimeout(check, 100);
            };
            check();
          })`);
          main.show();
          await main.webContents.executeJavaScript('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
          fs.writeFileSync(screenshot, (await main.webContents.capturePage()).toPNG());
          fs.writeFileSync(probe, JSON.stringify({ recovered: true, tokens: result.tokens.lifetime.tokens }));
        } catch (error) { fs.writeFileSync(probe, JSON.stringify({ error: String(error) })); }
        app.quit();
      });
      setTimeout(() => main.webContents.forcefullyCrashRenderer(), 300);
    }, { probe, screenshot: path.join(artifacts, 'main-recovered.png') });
    const deadline = Date.now() + 60000;
    let result;
    while (Date.now() < deadline) {
      try { result = JSON.parse(await fs.readFile(probe, 'utf8')); break; } catch { }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert.equal(result?.recovered, true, 'renderer reloaded and completed a real backend request');
    assert.equal(result.tokens, 120);
    await new Promise(resolve => { if (applicationProcess.exitCode !== null) resolve(); else applicationProcess.once('exit', resolve); });
    application = undefined;
    // Clear only after both crash-recovery checks, so they still prove retention.
    page = await launch();
    await request(page, 'runtime.select', { runtime: 'claudeCode' });
    assertGroupedUsage(await query(page), 'retained');
    await application.evaluate(({ dialog }) => {
      dialog.showMessageBox = async () => ({ response: 0, checkboxChecked: false });
    });
    const canceled = await request(page, 'data.clearHistory');
    assert.equal(canceled.success, false);
    assertGroupedUsage(await query(page), 'retained');
    await application.evaluate(({ dialog }, backup) => {
      dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [backup] });
      dialog.showMessageBox = async () => ({ response: 1, checkboxChecked: false });
    }, backup);
    const cleared = await request(page, 'data.clearHistory');
    assert.equal(cleared.success, true);
    const empty = await query(page);
    assert.equal(empty.totals.tokens, 0);
    assert.equal(empty.sessionCount, 0);
    assert.equal(empty.unattributed.length, 0);
    assert.equal((await request(page, 'settings.get')).theme, 'light');
    assert.equal((await request(page, 'data.restore')).success, true);
    assertGroupedUsage(await query(page), 'retained');
    await page.locator('.overview-grid').waitFor();
    await page.screenshot({ path: path.join(artifacts, 'main-restored.png'), animations: 'disabled' });
    console.log('DESKTOP_E2E_OK: settings, all-history private aggregate export, grouped and filtered usage query, retained history after source deletion, backup/restore, clear cancellation and confirmation, rich status strip, desktop capability, sidecar/renderer recovery');
  } finally {
    if (application) await application.close();
    await fs.rm(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
