import { app, BrowserWindow, ipcMain, net, protocol, screen, session } from 'electron';
import { spawn, type ChildProcess } from 'node:child_process';
import { createInterface } from 'node:readline';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { isRecord } from './protocol';
import type { SurfaceData } from './statusStrip';

export class DesktopWidgetHost {
  private child?: ChildProcess;
  private latest: SurfaceData = {};
  private restartTimer?: NodeJS.Timeout;
  private failures = 0;
  constructor(private readonly backend: string, private readonly action: (name: string) => Promise<unknown>,
    private readonly report: (message: string) => void,
    private readonly state: (attached: boolean, message: string) => void = () => {}) {}
  update(data: SurfaceData): void {
    this.latest = { ...this.latest, ...data };
    if (!this.latest.desktopMode) { this.dispose(); this.failures = 0; return; }
    if (!this.child && !this.restartTimer && this.failures < 5) this.start();
    if (this.child?.connected) this.child.send({ type: 'snapshot', data: this.latest });
  }
  private start(): void {
    const args = app.isPackaged ? ['--desktop-widget'] : [app.getAppPath(), '--desktop-widget'];
    const child = spawn(process.execPath, args, { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe', 'ipc'],
      env: { ...process.env, CODEXU_DESKTOP_BACKEND: this.backend,
        CODEXU_DESKTOP_STORAGE: path.join(app.getPath('userData'), 'desktop-widget') } });
    this.child = child;
    child.on('error', error => this.report(error.message));
    child.stderr?.on('data', data => console.error('[desktop-widget]', String(data).slice(0, 4096)));
    child.on('message', (value: unknown) => {
      if (!isRecord(value) || this.child !== child) return;
      if (value.type === 'ready' && child.connected) child.send({ type: 'snapshot', data: this.latest });
      if (value.type === 'error') this.report(String(value.message).slice(0, 1024));
      if (value.type === 'state') this.state(value.attached === true, String(value.message || '').slice(0, 1024));
      if (value.type === 'action' && ['open', 'todos', 'refresh'].includes(String(value.name))) {
        void this.action(String(value.name)).catch(e => this.report(String(e)));
      }
    });
    child.once('exit', () => {
      if (this.child !== child) return;
      this.child = undefined;
      this.state(false, '桌面仪表盘进程退出，正在恢复');
      this.failures++;
      if (this.failures < 5 && this.latest.desktopMode) this.restartTimer = setTimeout(() => {
        this.restartTimer = undefined;
        if (this.latest.desktopMode) this.start();
      }, Math.min(1000 * 2 ** (this.failures - 1), 16000));
      else this.report('桌面仪表盘恢复次数已用完，请重新启用桌面模式。');
    });
  }
  dispose(): void {
    if (this.restartTimer) clearTimeout(this.restartTimer);
    this.restartTimer = undefined;
    const child = this.child; this.child = undefined;
    if (child) this.state(false, '桌面仪表盘已关闭');
    if (child?.connected) child.send({ type: 'shutdown' });
    if (child) { const timer = setTimeout(() => child.kill(), 3000); child.once('exit', () => clearTimeout(timer)); timer.unref(); }
  }
}

export async function startDesktopWidget(rendererRoot: string): Promise<void> {
  if (!process.send || !process.env.CODEXU_DESKTOP_STORAGE || !process.env.CODEXU_DESKTOP_BACKEND) { app.exit(2); return; }
  mkdirSync(process.env.CODEXU_DESKTOP_STORAGE, { recursive: true });
  app.setPath('userData', process.env.CODEXU_DESKTOP_STORAGE);
  await app.whenReady();
  protocol.handle('app', async request => {
    const url = new URL(request.url);
    if (url.hostname !== 'codexu') return new Response('Denied', { status: 403 });
    const file = path.resolve(rendererRoot, '.' + decodeURIComponent(url.pathname));
    const relative = path.relative(rendererRoot, file);
    if (relative.startsWith('..') || path.isAbsolute(relative)) return new Response('Denied', { status: 403 });
    return net.fetch(pathToFileURL(file).toString());
  });
  session.defaultSession.setPermissionCheckHandler(() => false);
  session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  const area = screen.getPrimaryDisplay().workArea;
  const window = new BrowserWindow({ x: area.x + 18, y: area.y + 18, width: Math.min(430, area.width), height: Math.min(330, area.height),
    show: false, frame: false, resizable: false, skipTaskbar: true,
    webPreferences: { preload: path.join(__dirname, 'surfacePreload.js'), sandbox: true, contextIsolation: true, nodeIntegration: false, webviewTag: false } });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', e => e.preventDefault());
  window.webContents.on('render-process-gone', () => app.quit());
  app.on('window-all-closed', () => app.quit());
  let latest: SurfaceData = {};
  ipcMain.handle('codexu:surface', (event, name: unknown) => {
    if (event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame) throw new Error('Untrusted surface.');
    if (name === 'ready') { window.webContents.send('codexu:surface-data', latest); return true; }
    if (!['open', 'todos', 'refresh'].includes(String(name))) throw new Error('Unsupported desktop action.');
    process.send?.({ type: 'action', name }); return true;
  });
  process.on('message', (value: unknown) => {
    if (!isRecord(value)) return;
    if (value.type === 'shutdown') app.quit();
    if (value.type === 'snapshot' && isRecord(value.data)) { latest = value.data; if (!window.isDestroyed()) window.webContents.send('codexu:surface-data', latest); }
  });
  process.once('disconnect', () => app.quit());
  await window.loadURL('app://codexu/index.html?surface=desktop');
  const bridge = spawn(process.env.CODEXU_DESKTOP_BACKEND, ['--desktop-bridge'], {
    windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, CODEXU_PARENT_PID: String(process.pid) } });
  const handle = window.getNativeWindowHandle();
  const hwnd = (handle.length === 8 ? handle.readBigUInt64LE() : BigInt(handle.readUInt32LE())).toString();
  let retries = 0; let attached = false; let pending = false;
  let retryTimer: NodeJS.Timeout | undefined;
  let replyTimer: NodeJS.Timeout | undefined;
  const send = (action: string) => { if (bridge.stdin.writable && !pending) {
    pending = true;
    replyTimer = setTimeout(() => { process.send?.({ type: 'error', message: '桌面桥接响应超时' }); app.quit(); }, 8000);
    bridge.stdin.write(JSON.stringify({ action, handle: hwnd }) + '\n');
  } };
  const lines = createInterface({ input: bridge.stdout });
  lines.on('line', line => {
    pending = false;
    if (replyTimer) clearTimeout(replyTimer);
    if (window.isDestroyed()) { app.quit(); return; }
    try {
      const value: unknown = JSON.parse(line);
      if (!isRecord(value)) throw new Error('Invalid bridge reply.');
      attached = value.ok === true && value.attached === true;
      process.send?.({ type: 'state', attached, message: attached ? '已附着 Windows 桌面' : value.error || '正在重新连接桌面' });
      if (attached) { retries = 0; window.showInactive(); }
      else {
        window.hide();
        if (retries < 5) retryTimer = setTimeout(() => send('attach'), Math.min(1000 * 2 ** retries++, 16000));
        else process.send?.({ type: 'error', message: value.error || '桌面附着失败，请重新启用桌面模式。' });
      }
    } catch (e) { process.send?.({ type: 'error', message: String(e) }); window.hide(); }
  });
  bridge.on('error', error => { process.send?.({ type: 'error', message: error.message }); app.quit(); });
  bridge.once('exit', () => app.quit());
  bridge.stderr.on('data', data => console.error(String(data).slice(0, 4096)));
  const monitor = setInterval(() => { if (attached) send('status'); }, 2000);
  const fit = () => {
    if (window.isDestroyed()) return;
    const a = screen.getDisplayMatching(window.getBounds()).workArea;
    window.setBounds({ x: a.x + 8, y: a.y + 8, width: Math.min(430, a.width), height: Math.min(330, a.height) });
  };
  screen.on('display-removed', fit); screen.on('display-metrics-changed', fit);
  app.once('before-quit', () => {
    clearInterval(monitor); if (retryTimer) clearTimeout(retryTimer);
    if (replyTimer) clearTimeout(replyTimer);
    bridge.stdin.end(JSON.stringify({ action: 'shutdown' }) + '\n');
    const timer = setTimeout(() => bridge.kill(), 1500); timer.unref();
  });
  send('register'); process.send({ type: 'ready' });
}
