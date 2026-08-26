const assert = require('node:assert/strict');
const Module = require('node:module');
const test = require('node:test');

let exposedName;
let exposedBridge;
let registeredChannel;
let registeredListener;
let preloadExports;
const invocations = [];
const preloadDependencies = [];

const electronMock = {
  contextBridge: {
    exposeInMainWorld(name, bridge) {
      exposedName = name;
      exposedBridge = bridge;
    },
  },
  ipcRenderer: {
    invoke(...args) {
      invocations.push(args);
      return Promise.resolve({ ok: true });
    },
    on(channel, listener) {
      registeredChannel = channel;
      registeredListener = listener;
    },
  },
};

const originalLoad = Module._load;
try {
  Module._load = function load(request, parent, isMain) {
    if (parent?.filename.endsWith('dist\\preload.js') || parent?.filename.endsWith('dist/preload.js')) {
      preloadDependencies.push(request);
    }
    if (request === 'electron') return electronMock;
    return originalLoad.call(this, request, parent, isMain);
  };
  preloadExports = require('../dist/preload.js');
} finally {
  Module._load = originalLoad;
}

test('sandbox preload exposes the narrow codexU bridge without local module loads', async () => {
  assert.equal(exposedName, 'codexU');
  assert.equal(Object.isFrozen(exposedBridge), true);
  assert.equal(typeof exposedBridge.request, 'function');
  assert.equal(typeof exposedBridge.onEvent, 'function');
  assert.equal(registeredChannel, 'codexu:event');
  assert.deepEqual(preloadDependencies, ['electron']);

  const result = await exposedBridge.request('app.initialize', { smoke: true });
  assert.deepEqual(result, { ok: true });
  assert.deepEqual(invocations, [
    ['codexu:request', 'app.initialize', { smoke: true }],
  ]);
  await assert.rejects(() => exposedBridge.request('shell.execute', {}), /not allowed/u);
});

test('preload and main process use identical method and event allow-lists', () => {
  const security = require('../dist/security.js');
  assert.deepEqual(
    [...preloadExports.ALLOWED_METHODS].sort(),
    [...security.ALLOWED_METHODS].sort(),
  );
  assert.deepEqual(
    [...preloadExports.ALLOWED_EVENT_METHODS].sort(),
    [...security.ALLOWED_EVENT_METHODS].sort(),
  );
});

test('preload filters events and unsubscribe removes the listener', () => {
  const received = [];
  const unsubscribe = exposedBridge.onEvent((method, payload) => received.push([method, payload]));

  registeredListener({}, 'usage.snapshotChanged', { value: 1 });
  registeredListener({}, 'host.openExternal', { url: 'https://example.com' });
  unsubscribe();
  registeredListener({}, 'usage.snapshotChanged', { value: 2 });

  assert.deepEqual(received, [['usage.snapshotChanged', { value: 1 }]]);
});
