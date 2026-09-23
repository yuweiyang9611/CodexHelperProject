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
    message: { model: 'claude-sonnet-4-5', usage: { input_tokens: 80, output_tokens: 20 } } }) + '\n');
  await fs.writeFile(childTranscript, JSON.stringify({ type: 'assistant', sessionId: 'desktop-e2e', agentId: 'worker', cwd: root, timestamp: new Date().toISOString(),
    message: { model: 'claude-sonnet-4-5', usage: { input_tokens: 15, output_tokens: 5 } } }) + '\n');
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
    await request(page, 'runtime.select', { runtime: 'claudeCode' });
    const initial = await request(page, 'usage.refresh'); assert.equal(initial.tokens.lifetime.tokens, 120);
    assertGroupedUsage(await query(page), 'live');
    const filtered = await request(page, 'usage.query', { runtime: 'claudeCode', model: 'claude-sonnet-4-5', project: root });
    assertGroupedUsage(filtered, 'live');
    assert(!capabilities.capabilities.includes('desktopMode'));
    const migrated = await request(page, 'settings.update', { patch: { desktopMode: true } });
    assert.equal(migrated.desktopMode, false);
    assert(!application.windows().some(p => p.url().includes('surface=desktop')));
    const state = await request(page, 'statusStrip.recover'); assert.equal(state.visible, true);
    assert.equal(state.positionMode, 'automatic');
    const strip = application.windows().find(p => p.url().includes('surface=strip')); assert(strip);
    await strip.getByRole('button', { name: '展开或折叠状态条' }).click();
    await strip.getByRole('button', { name: '打开主界面' }).waitFor();
    assert(!/今日 Token|近 7 天|累计|今日等效金额/.test(await strip.locator('main').innerText()));
    await strip.getByText('刷新时间未知').first().waitFor();
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
    await page.locator('#panel-overview').waitFor({ timeout: 15000 });
    assert.equal((await request(page, 'statusStrip.preview', { patch: { statusStripShowTodayTokens: false } })).visible, true);
    await strip.getByText('今日 Token', { exact: false }).waitFor({ state: 'hidden' });
    assert.equal((await request(page, 'settings.get')).statusStripShowTodayTokens, false, 'retired Token display remains disabled');
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
    console.log('DESKTOP_E2E_OK: settings, all-history private aggregate export, grouped and filtered usage query, retained history after source deletion, backup/restore, clear cancellation and confirmation, quota-only strip, removed desktop replica, sidecar/renderer recovery');
  } finally {
    if (application) await application.close();
    await fs.rm(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
