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
const LOAD_TIMEOUT_MS = 30_000;

function startupFixture(load) {
  const timers = [];
  // Each navigation arms its own timeout; kept apart from the recovery backoff timers.
  const loadTimers = [];
  const prompts = [];
  let navigations = 0;
  const display = { id: 1, scaleFactor: 1, workArea: { x: 0, y: 0, width: 1600, height: 900 } };
  const screen = Object.assign(new EventEmitter(), {
    getPrimaryDisplay: () => display, getAllDisplays: () => [display], getDisplayMatching: () => display,
  });
  class Window extends EventEmitter {
    webContents = Object.assign(new EventEmitter(), {
      isDestroyed: () => this.isDestroyed(), setWindowOpenHandler() {},
      stop: () => { this.stopped = (this.stopped ?? 0) + 1; },
    });
    isDestroyed() { return this.destroyed === true; }
    isVisible() { return this.visible === true; }
    destroy() { this.destroyed = true; this.emit('closed'); }
    show() { this.visible = true; }
    loadURL() { return load(this, ++navigations); }
  }
  const electron = {
    BrowserWindow: Window, screen, nativeTheme: {},
    app: { enableSandbox() {}, requestSingleInstanceLock: () => false, exit() {}, getPath: () => __dirname },
    protocol: { registerSchemesAsPrivileged() {} },
    dialog: { showMessageBox: (...args) => {
      // Called as (options) or (owner, options).
      prompts.push({ owner: args.length > 1 ? args[0] : undefined, ...args.at(-1) });
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
      const timer = { callback, delay, unref() {} };
      (delay === LOAD_TIMEOUT_MS ? loadTimers : timers).push(timer);
      return timer;
    },
    clearTimeout(timer) {
      for (const list of [timers, loadTimers]) {
        const index = list.indexOf(timer);
        if (index >= 0) list.splice(index, 1);
      }
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
  const expireLoad = async () => {
    assert.equal(loadTimers.length, 1);
    loadTimers[0].callback();
    await new Promise(resolve => setImmediate(resolve));
  };
  return { start, retry, expireLoad, timers, loadTimers, prompts };
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
  // The window never showed, so the dialog must not be owned by it.
  assert.equal(fixture.prompts[0].owner, undefined);
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

test('a startup navigation that never settles times out and is retried', async () => {
  const fixture = startupFixture((_window, navigation) => navigation === 1 ? new Promise(() => {}) : Promise.resolve());
  const startup = fixture.start();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(fixture.timers.length, 0);
  await fixture.expireLoad();
  const window = await startup;
  assert.equal(window.stopped, 1);
  await fixture.retry(500);
  assert.equal(window.visible, true);
  assert.equal(fixture.timers.length, 0);
  assert.equal(fixture.loadTimers.length, 0);
});

test('a recovery navigation that never settles times out and moves to the next retry', async () => {
  const fixture = startupFixture((_window, navigation) => {
    if (navigation === 1) return Promise.reject(loadFailure);
    return navigation === 2 ? new Promise(() => {}) : Promise.resolve();
  });
  const window = await fixture.start();
  await fixture.retry(500);
  assert.notEqual(window.visible, true);
  await fixture.expireLoad();
  assert.equal(window.stopped, 1);
  await fixture.retry(1000);
  assert.equal(window.visible, true);
  assert.equal(fixture.timers.length, 0);
  assert.equal(fixture.loadTimers.length, 0);
});
