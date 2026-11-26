const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { readFileSync } = require('node:fs');
const { createRequire } = require('node:module');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const mainPath = require.resolve('../dist/main.js');
const requireFromMain = createRequire(mainPath);
const loadFailure = new Error("ERR_FAILED (-2) loading 'app://codexu/index.html'");

function startupFixture(load) {
  const timers = [];
  const prompts = [];
  let navigations = 0;
  const display = { id: 1, scaleFactor: 1, workArea: { x: 0, y: 0, width: 1600, height: 900 } };
  const screen = Object.assign(new EventEmitter(), {
    getPrimaryDisplay: () => display, getAllDisplays: () => [display], getDisplayMatching: () => display,
  });
  class Window extends EventEmitter {
    webContents = Object.assign(new EventEmitter(), {
      isDestroyed: () => this.isDestroyed(), setWindowOpenHandler() {},
    });
    isDestroyed() { return this.destroyed === true; }
    destroy() { this.destroyed = true; this.emit('closed'); }
    show() { this.visible = true; }
    loadURL() { return load(this, ++navigations); }
  }
  const electron = {
    BrowserWindow: Window, screen, nativeTheme: {},
    app: { enableSandbox() {}, requestSingleInstanceLock: () => false, exit() {}, getPath: () => __dirname },
    protocol: { registerSchemesAsPrivileged() {} },
    dialog: { showMessageBox: (_window, options) => {
      prompts.push(options);
      return new Promise(() => {});
    } },
  };
  const context = vm.createContext({
    exports: {}, __dirname: path.dirname(mainPath), AbortController,
    process: { platform: 'linux', env: {}, argv: [] },
    require(name) {
      if (name === 'electron') return electron;
      if (name === './desktopWidget' || name === './statusStrip') return {};
      return requireFromMain(name);
    },
    setTimeout(callback, delay) {
      timers.push({ callback, delay });
      return { unref() {} };
    },
    console: { log() {}, warn() {}, error() {} },
  });
  vm.runInContext(readFileSync(mainPath, 'utf8'), context);
  // Start at window creation with a connected backend. No native host or sidecar runs.
  const start = () => vm.runInContext('sidecar = {}; createMainWindow(hostSettings)', context);
  const retry = async expectedDelay => {
    assert.equal(timers.length, 1);
    const timer = timers.shift();
    assert.equal(timer.delay, expectedDelay);
    timer.callback();
    await new Promise(resolve => setImmediate(resolve));
  };
  return { start, retry, timers, prompts };
}

for (const crashEvent of [true, false]) {
  test(`recovers a rejected startup navigation ${crashEvent ? 'after' : 'without'} a renderer crash event`, async () => {
    const fixture = startupFixture(async (window, navigation) => {
      if (navigation === 1) {
        if (crashEvent) window.webContents.emit('render-process-gone', {}, { reason: 'crashed' });
        throw loadFailure;
      }
    });
    const window = await fixture.start();
    assert.equal(window.isDestroyed(), false);
    await fixture.retry(500);
    assert.equal(window.visible, true);
    assert.equal(fixture.timers.length, 0);
  });
}

test('repeated startup navigation failures exhaust the normal renderer retry budget', async () => {
  const fixture = startupFixture(async () => { throw loadFailure; });
  await fixture.start();
  for (const delay of [500, 1000, 2000]) {
    assert.equal(fixture.prompts.length, 0);
    await fixture.retry(delay);
  }
  assert.equal(fixture.timers.length, 0);
  assert.equal(fixture.prompts.length, 1);
  assert.match(fixture.prompts[0].message, /界面进程/u);
});

test('the first retry performs a fresh load when the crashed startup navigation is still settling', async () => {
  let rejectStartup;
  const fixture = startupFixture((window, navigation) => {
    if (navigation > 1) return Promise.resolve();
    window.webContents.emit('render-process-gone', {}, { reason: 'crashed' });
    return new Promise((_resolve, reject) => { rejectStartup = reject; });
  });
  const startup = fixture.start();
  await new Promise(resolve => setImmediate(resolve));
  await fixture.retry(500);
  rejectStartup(loadFailure);
  const window = await startup;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(window.visible, true);
  assert.equal(fixture.timers.length, 0);
});

test('propagates startup failure when its renderer window is gone', async () => {
  const fixture = startupFixture(async window => {
    window.destroy();
    throw loadFailure;
  });
  await assert.rejects(fixture.start(), loadFailure);
  assert.equal(fixture.timers.length, 0);
});
