const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture() {
  const children = [];
  const scheduled = [];
  const actions = [];
  const states = [];
  const warnings = [];
  const exits = [];
  const processStub = { execPath: 'C:\\CodexU\\CodexU.exe', env: {} };
  const app = { isPackaged: true, getPath: () => 'C:\\Users\\Test\\CodexU', exit(code) { exits.push(code); } };
  function spawn(executable, arguments_, options) {
    const child = new EventEmitter();
    Object.assign(child, { connected: true, stderr: new EventEmitter(), sent: [],
      send(message) { this.sent.push(message); }, kill() { this.killed = true; } });
    children.push({ executable, arguments_, options, child });
    return child;
  }
  const module = { exports: {} };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../dist/desktopWidget.js'), 'utf8'), {
    exports: module.exports,
    require: name => name === 'electron' ? { app }
      : name === 'node:child_process' ? { spawn }
      : name === './protocol' ? require('../dist/protocol.js') : require(name),
    __dirname, process: processStub, console,
    setTimeout(callback, delay) { const timer = { callback, delay, unref() {} }; scheduled.push(timer); return timer; },
    clearTimeout(timer) { timer.cancelled = true; },
  });
  const host = new module.exports.DesktopWidgetHost('C:\\CodexU\\CodexU.Sidecar.exe',
    async action => actions.push(action), message => warnings.push(message),
    (attached, message) => states.push({ attached, message }));
  return { host, children, scheduled, actions, states, warnings, exits, module, processStub };
}

test('desktop mode launches a helper and carries the rich v0.6.0 presentation', async () => {
  const { host, children, actions, states, warnings } = fixture();
  host.update({ desktopMode: false });
  assert.equal(children.length, 0);
  host.update({ desktopMode: true, todayAmount: 42, theme: 'dark',
    presentation: { title: 'Codex', todayTokensText: '420K', weeklyUsageText: '72%' } });
  assert.equal(children.length, 1);
  const launch = children[0];
  assert.deepEqual(Array.from(launch.arguments_), ['--desktop-widget']);
  assert.equal(launch.options.windowsHide, true);
  assert.equal(launch.options.env.CODEXU_DESKTOP_BACKEND, 'C:\\CodexU\\CodexU.Sidecar.exe');
  assert.equal(launch.child.sent[0].data.presentation.todayTokensText, '420K');
  host.update({ todayAmount: 55 });
  launch.child.emit('message', { type: 'ready' });
  assert.equal(launch.child.sent.at(-1).data.todayAmount, 55);
  assert.equal(launch.child.sent.at(-1).data.presentation.weeklyUsageText, '72%');
  launch.child.emit('message', { type: 'state', attached: true, message: '已附着 Windows 桌面' });
  assert.deepEqual(states, [{ attached: true, message: '已附着 Windows 桌面' }]);
  launch.child.emit('message', { type: 'action', name: 'refresh' });
  launch.child.emit('message', { type: 'action', name: 'open' });
  launch.child.emit('message', { type: 'action', name: 'lock' });
  await Promise.resolve();
  assert.deepEqual(actions, ['refresh', 'open']);
  assert.deepEqual(warnings, []);
  host.dispose();
  assert.equal(launch.child.sent.at(-1).type, 'shutdown');
  launch.child.emit('message', { type: 'action', name: 'refresh' });
  launch.child.emit('exit', 0);
  assert.equal(actions.length, 2);
});

test('disabling desktop mode cancels recovery and a later enable starts a fresh helper', () => {
  const { host, children, scheduled } = fixture();
  host.update({ desktopMode: true });
  children[0].child.emit('exit', 1);
  const recovery = scheduled[0];
  assert.equal(recovery.delay, 1000);
  host.update({ desktopMode: false });
  assert.equal(recovery.cancelled, true);
  recovery.callback();
  assert.equal(children.length, 1);
  host.update({ desktopMode: true });
  assert.equal(children.length, 2);
  host.dispose();
  children[1].child.emit('exit', 0);
});

test('the desktop helper refuses startup without its parent IPC channel', async () => {
  const { module, processStub, exits, children } = fixture();
  processStub.env.CODEXU_DESKTOP_STORAGE = 'C:\\Users\\Test\\CodexU\\desktop-widget';
  processStub.env.CODEXU_DESKTOP_BACKEND = 'C:\\CodexU\\CodexU.Sidecar.exe';
  await module.exports.startDesktopWidget('C:\\CodexU\\dist');
  assert.deepEqual(exits, [2]);
  assert.equal(children.length, 0);
});
