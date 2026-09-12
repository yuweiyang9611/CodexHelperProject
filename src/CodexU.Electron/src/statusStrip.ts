import { BrowserWindow, screen, type IpcMainInvokeEvent } from 'electron';
import { readFileSync, writeFileSync, renameSync, mkdirSync } from 'node:fs';
import path from 'node:path';

export interface SurfaceData {
  presentation?: Record<string, unknown>;
  theme?: string;
  statusStripEnabled?: boolean;
  statusStripPositionLocked?: boolean;
  desktopMode?: boolean;
  todayAmount?: number;
  layout?: { width: number; collapsedHeight: number; expandedHeight: number; margin: number; rightOffset: number; topOffset: number };
  [key: string]: unknown;
}

export class StatusStripHost {
  private window?: BrowserWindow;
  private data: SurfaceData = {};
  private expanded = false;
  private manual = false;
  private previewTimer?: NodeJS.Timeout;
  private preview?: SurfaceData;
  private saveTimer?: NodeJS.Timeout;
  private readonly displayChanged = () => this.fit();

  constructor(private readonly root: string, private readonly action: (name: string) => Promise<unknown>) {
    screen.on('display-removed', this.displayChanged);
    screen.on('display-metrics-changed', this.displayChanged);
  }
  owns(event: IpcMainInvokeEvent): boolean {
    return Boolean(this.window && !this.window.isDestroyed() && event.sender === this.window.webContents
      && event.senderFrame === event.sender.mainFrame && event.senderFrame?.url === 'app://codexu/index.html?surface=strip');
  }
  update(data: SurfaceData): void {
    this.data = { ...this.data, ...data };
    if (this.data.statusStripEnabled) void this.ensure().catch(e => console.error('[status-strip]', e));
    else if (!this.previewTimer) this.window?.hide();
    this.send();
  }
  async control(payload: Record<string, unknown>): Promise<unknown> {
    if (payload.action === 'preview') {
      await this.ensure();
      if (this.previewTimer) clearTimeout(this.previewTimer);
      const settings = payload.settings as SurfaceData | undefined;
      this.preview = { theme: settings?.theme, statusStripShowTodayTokens: settings?.statusStripShowTodayTokens,
        presentation: payload.presentation as SurfaceData['presentation'] ?? this.data.presentation };
      this.send();
      this.previewTimer = setTimeout(() => { this.previewTimer = undefined; this.preview = undefined; this.update(this.data); }, 10_000);
    } else if (payload.action === 'recover') {
      this.manual = false;
      await this.ensure();
      this.placeDefault();
      this.persist();
      if (!this.data.statusStripEnabled) {
        if (this.previewTimer) clearTimeout(this.previewTimer);
        this.previewTimer = setTimeout(() => { this.previewTimer = undefined; this.update(this.data); }, 10_000);
      }
    } else if (payload.action !== 'getState') throw new Error('Unsupported status strip control.');
    const display = this.window ? screen.getDisplayMatching(this.window.getBounds()) : screen.getPrimaryDisplay();
    return { configuredEnabled: this.data.statusStripEnabled === true, visible: this.window?.isVisible() ?? false,
      positionLocked: this.data.statusStripPositionLocked === true, hasManualPosition: this.manual,
      positionMode: this.manual ? 'manual' : 'automatic', displayName: display.label || String(display.id), message: 'Electron 状态条已接入' };
  }
  async request(name: string): Promise<unknown> {
    if (name === 'ready') { this.send(); return true; }
    if (name === 'expand') { this.expanded = !this.expanded; this.fit(); this.send(); return true; }
    if (name === 'lock') return this.action('lock');
    return this.action(name);
  }
  dispose(): void {
    if (this.previewTimer) clearTimeout(this.previewTimer);
    if (this.saveTimer) clearTimeout(this.saveTimer);
    screen.removeListener('display-removed', this.displayChanged);
    screen.removeListener('display-metrics-changed', this.displayChanged);
    this.window?.destroy(); this.window = undefined;
  }
  private async ensure(): Promise<void> {
    if (this.window && !this.window.isDestroyed()) { this.window.showInactive(); return; }
    const window = new BrowserWindow({ width: 430, height: 46, frame: false, show: false,
      resizable: false, skipTaskbar: true, alwaysOnTop: true, autoHideMenuBar: true,
      webPreferences: { preload: path.join(__dirname, 'surfacePreload.js'), sandbox: true,
        contextIsolation: true, nodeIntegration: false, webviewTag: false } });
    this.window = window;
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('will-navigate', event => event.preventDefault());
    window.webContents.on('render-process-gone', () => { window.destroy(); this.window = undefined; });
    window.on('will-move', event => { if (this.data.statusStripPositionLocked) event.preventDefault(); });
    window.on('moved', () => {
      this.manual = true;
      if (this.saveTimer) clearTimeout(this.saveTimer);
      this.saveTimer = setTimeout(() => this.persist(), 300);
    });
    this.placeDefault();
    for (const suffix of ['', '.bak']) {
      try {
        const value = JSON.parse(readFileSync(path.join(this.root, 'status-strip-placement.json' + suffix), 'utf8'));
        if (value.version !== 1 || !Number.isFinite(value.left) || !Number.isFinite(value.top)) continue;
        const point = screen.screenToDipPoint({ x: value.left, y: value.top });
        window.setPosition(Math.round(point.x), Math.round(point.y)); this.manual = true; break;
      } catch { /* A missing or damaged placement falls back onto the current screen. */ }
    }
    this.fit();
    await window.loadURL('app://codexu/index.html?surface=strip');
    if (!window.isDestroyed()) { this.send(); window.showInactive(); }
  }
  private send(): void {
    if (this.window && !this.window.isDestroyed()) this.window.webContents.send('codexu:surface-data', { ...this.data, ...this.preview, expanded: this.expanded });
  }
  private placeDefault(): void {
    const area = screen.getPrimaryDisplay().workArea;
    const l = this.layout;
    this.window?.setPosition(Math.round(area.x + area.width - Math.min(l.width, area.width) - l.rightOffset), area.y + l.topOffset);
  }
  private get layout() {
    return this.data.layout ?? { width: 430, collapsedHeight: 46, expandedHeight: 290, margin: 8, rightOffset: 18, topOffset: 10 };
  }
  private fit(): void {
    if (!this.window || this.window.isDestroyed()) return;
    const bounds = this.window.getBounds();
    const area = screen.getDisplayMatching(bounds).workArea;
    const l = this.layout;
    const mx = Math.min(l.margin, Math.max(0, Math.floor((area.width - 1) / 2)));
    const my = Math.min(l.margin, Math.max(0, Math.floor((area.height - 1) / 2)));
    const width = Math.min(l.width, area.width - 2 * mx);
    const height = Math.min(this.expanded ? l.expandedHeight : l.collapsedHeight, area.height - 2 * my);
    this.window.setBounds({ width, height, x: Math.max(area.x + mx, Math.min(bounds.x, area.x + area.width - mx - width)),
      y: Math.max(area.y + my, Math.min(bounds.y, area.y + area.height - my - height)) });
  }
  private persist(): void {
    if (!this.window || this.window.isDestroyed()) return;
    const b = this.window.getBounds(); const p = screen.dipToScreenPoint({ x: b.x, y: b.y });
    try {
      mkdirSync(this.root, { recursive: true });
      const file = path.join(this.root, 'status-strip-placement.json');
      writeFileSync(file + '.tmp', JSON.stringify({ version: 1, left: p.x, top: p.y }));
      renameSync(file + '.tmp', file);
    } catch (error) { console.error('[status-strip] position save failed', error); }
  }
}
