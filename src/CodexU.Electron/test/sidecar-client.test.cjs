const assert = require('node:assert/strict');
const { once } = require('node:events');
const path = require('node:path');
const test = require('node:test');
const { assertRequiredSidecarCapabilities } = require('../dist/protocol.js');
const { SidecarClient } = require('../dist/sidecar/SidecarClient.js');

const fixture = path.join(__dirname, 'fixtures', 'fake-sidecar.cjs');

function createClient(extraArguments = [], options = {}) {
  return new SidecarClient({
    executablePath: process.execPath,
    arguments: [fixture, ...extraArguments],
    handshakeTimeoutMs: 1_000,
    requestTimeoutMs: 1_000,
    ...options,
  });
}

test('handshakes, correlates a response, forwards an event, logs stderr, and shuts down', async () => {
  const client = createClient();
  const stderrPromise = once(client, 'stderr');
  const exitPromise = once(client, 'exit');
  const handshake = await client.start();
  assert.equal(handshake.backendVersion, 'fake-1.0.0');
  assert.deepEqual(handshake.capabilities, [
    'ipc.request.v1',
    'host.rpc.v1',
    'host.state.v1',
    'gracefulShutdown',
  ]);
  assert.match((await stderrPromise)[0], /fake sidecar ready/u);

  const eventPromise = once(client, 'event');
  const response = await client.request('emit', { value: 42 });
  assert.deepEqual(response, { method: 'emit', received: { value: 42 } });
  assert.deepEqual((await eventPromise)[0].payload, { sequence: 1 });

  await client.shutdown();
  assert.equal((await exitPromise)[0].expected, true);
});

test('requires both reverse-RPC and host-state handshake capabilities', () => {
  assert.doesNotThrow(() => assertRequiredSidecarCapabilities([
    'host.rpc.v1',
    'host.state.v1',
  ]));
  assert.throws(
    () => assertRequiredSidecarCapabilities(['host.state.v1']),
    /host\.rpc\.v1/u,
  );
  assert.throws(
    () => assertRequiredSidecarCapabilities(['host.rpc.v1']),
    /host\.state\.v1/u,
  );
});

test('rejects a pending request when its deadline expires', async () => {
  const client = createClient();
  await client.start();
  const request = client.request('never', {}, 25);
  const idle = client.waitForIdle(1_000);
  await assert.rejects(() => request, /timed out/u);
  await idle;
  await client.shutdown();
});

test('kills a sidecar that misses the handshake deadline', async () => {
  const client = createClient(['--no-handshake'], { handshakeTimeoutMs: 25 });
  const exitPromise = once(client, 'exit');
  await assert.rejects(() => client.start(), /handshake timed out/u);
  assert.equal((await exitPromise)[0].expected, true);
});

test('sends the exact one-way hostState envelope only while ready', async () => {
  const client = createClient();
  await assert.rejects(() => client.sendHostState(false), /not ready/u);
  await client.start();
  await assert.rejects(() => client.sendHostState('true'), TypeError);

  const receivedPromise = once(client, 'event');
  await client.sendHostState(true);
  assert.deepEqual((await receivedPromise)[0].payload, {
    version: 1,
    type: 'hostState',
    globalHotKeyRegistered: true,
  });

  await client.shutdown();
  await assert.rejects(() => client.sendHostState(false), /not ready/u);
});

test('answers sidecar host requests with direct success and cancellation payloads', async () => {
  const handled = [];
  const client = createClient([], {
    hostRequestHandler: async (request) => {
      handled.push(request);
      return request.method === 'host.dialog.confirm' ? true : null;
    },
  });
  await client.start();

  const confirmation = await client.request('host-confirm');
  assert.deepEqual(confirmation, {
    version: 1,
    id: 'host-1',
    type: 'hostResponse',
    ok: true,
    payload: true,
  });
  const cancellation = await client.request('host-cancel');
  assert.deepEqual(cancellation, {
    version: 1,
    id: 'host-2',
    type: 'hostResponse',
    ok: true,
    payload: null,
  });
  assert.deepEqual(handled.map((request) => request.method), [
    'host.dialog.confirm',
    'host.dialog.saveFile',
  ]);

  await client.shutdown();
});

test('turns host-handler exceptions and invalid results into correlated failures', async () => {
  let invocation = 0;
  const client = createClient([], {
    hostRequestHandler: async () => {
      invocation += 1;
      if (invocation === 1) {
        const error = new Error('Dialog backend failed.');
        error.code = 'native_dialog_failed';
        throw error;
      }
      return 'not a confirmation result';
    },
  });
  await client.start();

  const thrown = await client.request('host-confirm');
  assert.deepEqual(thrown, {
    version: 1,
    id: 'host-1',
    type: 'hostResponse',
    ok: false,
    error: { code: 'native_dialog_failed', message: 'Dialog backend failed.' },
  });
  const invalid = await client.request('host-confirm');
  assert.equal(invalid.ok, false);
  assert.equal(invalid.error.code, 'host_request_failed');
  assert.match(invalid.error.message, /must return a boolean/u);

  await client.shutdown();
});

test('rejects a malformed or unsupported host request as a protocol failure', async () => {
  const client = createClient([], { hostRequestHandler: async () => false });
  const protocolErrorPromise = once(client, 'protocolError');
  const exitPromise = once(client, 'exit');
  await client.start();

  const request = client.request('host-malformed');
  await assert.rejects(() => request, /invalid host request/u);
  assert.match((await protocolErrorPromise)[0].message, /invalid host request/u);
  assert.equal((await exitPromise)[0].expected, true);
});

test('responds safely to an active host request before shutting down', async () => {
  let signalHandlerStarted;
  const handlerStarted = new Promise((resolve) => {
    signalHandlerStarted = resolve;
  });
  const client = createClient([], {
    hostRequestHandler: async () => {
      signalHandlerStarted();
      return new Promise(() => {});
    },
  });
  await client.start();

  const request = client.request('host-pending');
  await handlerStarted;
  const stderrPromise = once(client, 'stderr');
  const exitPromise = once(client, 'exit');
  const shutdownPromise = client.shutdown();
  await assert.rejects(() => request, /shutting down/u);
  await shutdownPromise;

  const responseLog = (await stderrPromise)[0];
  assert.match(responseLog, /"type":"hostResponse"/u);
  assert.match(responseLog, /"code":"host_shutting_down"/u);
  assert.equal((await exitPromise)[0].expected, true);
});

test('shutdown has one absolute deadline even when kill never produces close', async () => {
  const client = createClient();
  await client.start();
  const child = client.child;
  assert.ok(child);

  const originalWrite = child.stdin.write.bind(child.stdin);
  const originalEnd = child.stdin.end.bind(child.stdin);
  const originalKill = child.kill.bind(child);
  let killCalls = 0;

  // Keep every shutdown phase unresolved: the graceful write callback never runs,
  // stdin teardown throws before EOF, and the termination signal never produces close.
  child.stdin.write = () => true;
  child.stdin.end = () => {
    throw new Error('simulated synchronous stdin teardown failure');
  };
  child.kill = () => {
    killCalls += 1;
    return true;
  };

  const exitPromise = once(client, 'exit');
  const timeoutMs = 50;
  const startedAt = Date.now();
  const watchdog = new Promise((_, reject) => {
    const timer = setTimeout(
      () => reject(new Error('shutdown remained stuck past its total deadline')),
      500,
    );
    timer.unref();
  });

  try {
    await Promise.race([client.shutdown(timeoutMs), watchdog]);
    const elapsedMs = Date.now() - startedAt;
    assert.ok(
      elapsedMs < 400,
      `shutdown exceeded its ${timeoutMs} ms deadline: ${elapsedMs} ms`,
    );
    assert.equal(killCalls, 1);
    assert.equal(child.exitCode, null, 'fixture must still be open after the mocked kill');
  } finally {
    child.stdin.write = originalWrite;
    child.stdin.end = originalEnd;
    child.kill = originalKill;
    originalKill();
  }

  assert.equal((await exitPromise)[0].expected, true);
});

test('rejects invalid shutdown deadlines without changing client state', async () => {
  const client = createClient();
  await client.start();
  await assert.rejects(() => client.shutdown(0), RangeError);
  await assert.rejects(() => client.shutdown(Number.POSITIVE_INFINITY), RangeError);
  await client.shutdown();
});
