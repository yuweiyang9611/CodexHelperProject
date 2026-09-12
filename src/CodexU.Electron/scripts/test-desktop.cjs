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
  await fs.writeFile(transcript, JSON.stringify({ type: 'assistant', cwd: root, timestamp: new Date().toISOString(),
    message: { model: 'claude-sonnet-4-5', usage: { input_tokens: 100, output_tokens: 20 } } }) + '\n');
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
  try {
    let page = await launch();
    const capabilities = await request(page, 'app.initialize');
    assert(capabilities.capabilities.includes('statusStripControl'));
    await request(page, 'runtime.select', { runtime: 'claudeCode' });
    const initial = await request(page, 'usage.refresh'); assert.equal(initial.tokens.lifetime.tokens, 120);
    if (process.env.CODEXU_RUN_DESKTOP_ATTACH_TEST === '1') {
      await page.evaluate(() => { window.desktopTestStates = []; window.codexU.onEvent((method, state) => {
        if (method === 'desktop.stateChanged') window.desktopTestStates.push(state);
      }); });
      await request(page, 'settings.update', { patch: { desktopMode: true } });
      try { await page.waitForFunction(() => window.desktopTestStates.some(state => state.attached), null, { timeout: 45000 }); }
      catch (error) { console.error('Desktop attachment states:', await page.evaluate(() => window.desktopTestStates)); throw error; }
      await request(page, 'settings.update', { patch: { desktopMode: false } });
    }
    const state = await request(page, 'statusStrip.recover'); assert.equal(state.visible, true);
    const strip = application.windows().find(p => p.url().includes('surface=strip')); assert(strip);
    await strip.getByRole('button', { name: '展开或折叠状态条' }).click();
    await strip.getByRole('button', { name: '打开主界面' }).waitFor();
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
    await strip.getByRole('button', { name: /^待办/ }).click();
    await page.locator('#panel-todos').waitFor({ timeout: 15000 });
    assert.equal((await request(page, 'statusStrip.preview', { patch: { statusStripShowTodayTokens: false } })).visible, true);
    await strip.getByText('今日 Token', { exact: false }).waitFor({ state: 'hidden' });
    assert.equal((await request(page, 'settings.get')).statusStripShowTodayTokens, true, 'preview does not persist settings');
    await request(page, 'statusStrip.preview', { patch: { statusStripShowTodayTokens: true } });
    await strip.screenshot({ path: path.join(artifacts, 'status-strip.png') });
    await request(page, 'settings.update', { patch: { theme: 'light' } });
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
    await application.close(); application = undefined;
    page = await launch();
    assert.equal((await request(page, 'settings.get')).theme, 'light');
    await request(page, 'runtime.select', { runtime: 'claudeCode' });
    assert.equal((await request(page, 'usage.refresh')).tokens.lifetime.tokens, 120);
    const probe = path.join(root, 'recovery-result.json');
    const applicationProcess = application.process();
    await application.evaluate(({ BrowserWindow, app }, { probe, screenshot }) => {
      const fs = process.getBuiltinModule('node:fs');
      const main = BrowserWindow.getAllWindows().find(w => !w.webContents.getURL().includes('surface='));
      main.webContents.once('did-finish-load', async () => {
        try {
          const result = await main.webContents.executeJavaScript('window.codexU.request("usage.refresh")');
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
    console.log('DESKTOP_E2E_OK: settings, restart, history, backup/restore, status strip, sidecar/renderer recovery; desktop attachment=' + (process.env.CODEXU_RUN_DESKTOP_ATTACH_TEST === '1' ? 'verified' : 'not requested'));
  } finally {
    if (application) await application.close();
    await fs.rm(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
