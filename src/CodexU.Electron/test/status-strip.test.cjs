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
  class Window extends EventEmitter {
    constructor(options) { super(); window = this; this.bounds = { x: 0, y: 0, width: options.width, height: options.height };
      this.webContents = Object.assign(new EventEmitter(), { setWindowOpenHandler() {}, send() {} }); }
    isDestroyed() { return !!this.destroyed; }
    destroy() { this.destroyed = true; }
    showInactive() { this.visible = true; }
    hide() { this.visible = false; }
    isVisible() { return !!this.visible; }
    getBounds() { return this.bounds; }
    setPosition(x, y) { this.setBounds({ x, y }); }
    setBounds(value) { this.bounds = { ...this.bounds, ...value }; this.emit('moved'); }
    async loadURL() {}
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
    host.update({ statusStripEnabled: true });
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
  } finally { host.dispose(); fs.rmSync(root, { recursive: true, force: true }); }
});
