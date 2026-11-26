const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const vm = require('node:vm');

test('only user moves persist; recovery survives restart and clears pending saves', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codexu-strip-'));
  const display = { workArea: { x: -1000, y: 0, width: 1000, height: 800 }, id: 1 };
  const screen = Object.assign(new EventEmitter(), { getPrimaryDisplay: () => display, getDisplayMatching: () => display,
    screenToDipPoint: p => p, dipToScreenPoint: p => p });
  let window;
  let lastSurface;
  class Window extends EventEmitter {
    constructor(options) {
      super();
      window = this;
      this.bounds = { x: 0, y: 0, width: options.width, height: options.height };
      this.webContents = Object.assign(new EventEmitter(), {
        isDestroyed: () => !!this.destroyed,
        setWindowOpenHandler() {},
        send: (_channel, data) => {
          this.ensureAlive();
          lastSurface = data;
        },
      });
    }
    ensureAlive() {
      if (this.destroyed) throw new TypeError('Object has been destroyed');
    }
    isDestroyed() { return !!this.destroyed; }
    destroy() { this.destroyed = true; }
    close() {
      if (this.destroyed) return;
      this.destroyed = true;
      this.visible = false;
      this.emit('closed');
    }
    showInactive() { this.ensureAlive(); this.visible = true; }
    hide() { this.ensureAlive(); this.visible = false; }
    isVisible() { this.ensureAlive(); return !!this.visible; }
    getBounds() { this.ensureAlive(); return this.bounds; }
    setPosition(x, y) { this.ensureAlive(); this.setBounds({ x, y }); }
    setBounds(value) { this.ensureAlive(); this.bounds = { ...this.bounds, ...value }; this.emit('moved'); }
    async loadURL() { this.ensureAlive(); }
    drag(x, y) { let prevented = false; this.emit('will-move', { preventDefault() { prevented = true; } }); if (!prevented) this.setPosition(x, y); }
  }
  const module = { exports: {} };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../dist/statusStrip.js'), 'utf8'), {
    exports: module.exports, require: name => name === 'electron' ? { BrowserWindow: Window, screen } : require(name),
    __dirname, setTimeout, clearTimeout, console,
  });
  const { StatusStripHost } = module.exports;
  let host = new StatusStripHost(root, async () => {});
  const state = () => host.control({ action: 'getState' });
  const file = path.join(root, 'status-strip-placement.json');
  try {
    host.update({ statusStripEnabled: true, statusStripShowTodayTokens: false, statusStripQuotaMode: 'dual',
      todayValuation: { knownAmount: 42, isLowerBound: true }, presentation: { title: 'Codex', quotaText: '72%', todayTokensText: '420K' } });
    await host.request('ready');
    assert.deepEqual(lastSurface.todayValuation, { knownAmount: 42, isLowerBound: true });
    assert.equal(lastSurface.statusStripQuotaMode, 'dual');
    assert.equal(lastSurface.presentation.todayTokensText, '420K');
    await host.control({ action: 'preview', settings: { theme: 'dark', statusStripShowTodayTokens: true } });
    assert.equal(lastSurface.statusStripShowTodayTokens, true);
    assert.equal(lastSurface.statusStripQuotaMode, 'dual');
    await host.request('expand');
    assert.equal(lastSurface.expanded, true);
    assert.equal(window.bounds.height, 290);
    assert.equal((await state()).positionMode, 'automatic');
    window.setPosition(-800, 20);
    assert.equal((await state()).hasManualPosition, false);
    window.drag(-700, 30);
    assert.equal((await state()).hasManualPosition, true);
    host.dispose(); // Flush a pending user move.
    assert.equal(JSON.parse(fs.readFileSync(file)).left, -700);
    host = new StatusStripHost(root, async () => {});
    host.update({ statusStripEnabled: true, statusStripPositionLocked: true });
    window.drag(-200, 50);
    assert.equal(window.bounds.x, -700);
    fs.copyFileSync(file, file + '.bak');
    await host.control({ action: 'recover' });
    assert.equal((await state()).positionMode, 'automatic');
    assert.equal(fs.existsSync(file), false);
    assert.equal(fs.existsSync(file + '.bak'), false);
    host.dispose();
    host = new StatusStripHost(root, async () => {});
    host.update({ statusStripEnabled: true });
    assert.equal((await state()).positionMode, 'automatic');
    display.workArea = { x: 0, y: 0, width: 800, height: 600 };
    screen.emit('display-removed');
    assert(window.bounds.x >= 0);
    assert.equal((await state()).positionMode, 'automatic');
    window.drag(10, 20);
    await host.control({ action: 'recover' });
    await new Promise(resolve => setTimeout(resolve, 350));
    assert.equal(fs.existsSync(file), false);

    // Closing or destroying a preview/status-strip window must never throw on
    // subsequent settings saves, timer expiry, display changes, or state reads,
    // and re-enabling or previewing again must cleanly recreate the window.
    host.dispose();
    const scheduledTimers = [];
    const timerSandbox = {
      exports: {},
      require: name => name === 'electron' ? { BrowserWindow: Window, screen } : require(name),
      __dirname,
      setTimeout: (fn, delay) => {
        const entry = { fn: () => { entry.fired = true; fn(); }, delay, cleared: false, fired: false, unref() {} };
        scheduledTimers.push(entry);
        return entry;
      },
      clearTimeout: entry => { if (entry) entry.cleared = true; },
      console,
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../dist/statusStrip.js'), 'utf8'), timerSandbox);
    const { StatusStripHost: TimedStatusStripHost } = timerSandbox.exports;
    host = new TimedStatusStripHost(root, async () => {});
    host.update({ statusStripEnabled: false, theme: 'dark', presentation: { title: 'Codex' } });
    await host.control({ action: 'preview', settings: { theme: 'light', statusStripShowTodayTokens: true } });
    const firstPreviewWindow = window;
    assert.equal((await state()).visible, true);
    // Simulate external destruction without a 'closed' event first (worst case).
    firstPreviewWindow.destroy();
    const previewExpiry = scheduledTimers.find(t => t.delay === 10_000 && !t.cleared && !t.fired);
    assert.ok(previewExpiry, 'preview scheduled a 10s expiry timer');
    assert.doesNotThrow(() => previewExpiry.fn());
    assert.doesNotThrow(() => host.update({ statusStripEnabled: false, theme: 'light' }));
    assert.doesNotThrow(() => screen.emit('display-removed'));
    assert.equal((await state()).visible, false);

    // Simulate user closing the preview window normally ('closed' event) and saving settings.
    await host.control({ action: 'preview', settings: { theme: 'dark', statusStripShowTodayTokens: false } });
    const secondPreviewWindow = window;
    assert.notEqual(secondPreviewWindow, firstPreviewWindow);
    assert.equal((await state()).visible, true);
    secondPreviewWindow.close();
    assert.equal(scheduledTimers.filter(t => t.delay === 10_000 && !t.cleared && !t.fired).length, 0);
    assert.doesNotThrow(() => host.update({ statusStripEnabled: false }));
    assert.equal((await state()).visible, false);

    // Re-enabling after destruction creates a fresh live window.
    host.update({ statusStripEnabled: true });
    await new Promise(resolve => setImmediate(resolve));
    assert.notEqual(window, secondPreviewWindow);
    assert.equal(window.isDestroyed(), false);
    assert.equal((await state()).visible, true);
  } finally { host.dispose(); fs.rmSync(root, { recursive: true, force: true }); }
});
