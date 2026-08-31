const assert = require('node:assert/strict');
const test = require('node:test');
const {
  NativeNotificationAdapter,
  parseNativeNotificationPayload,
} = require('../dist/nativeNotifications.js');

class FakeNotification {
  listeners = new Map();
  showCount = 0;
  showFailure;

  once(event, listener) {
    this.listeners.set(event, listener);
    return this;
  }

  show() {
    this.showCount += 1;
    if (this.showFailure) throw this.showFailure;
  }

  emit(event, ...arguments_) {
    const listener = this.listeners.get(event);
    this.listeners.delete(event);
    listener?.(...arguments_);
  }
}

function payload(overrides = {}) {
  return {
    id: 'quota-below-threshold:test:1',
    title: 'codexU 额度提醒',
    body: 'Codex 5 小时额度剩余 10%。',
    ...overrides,
  };
}

function harness(overrides = {}) {
  const created = [];
  const failures = [];
  let activations = 0;
  const adapter = new NativeNotificationAdapter({
    isSupported: () => true,
    create: (options) => {
      const handle = new FakeNotification();
      created.push({ options, handle });
      return handle;
    },
    activateWindow: () => { activations += 1; },
    onFailure: (failure) => failures.push(failure),
    retryDelaysMs: [],
    ...overrides,
  });
  return {
    adapter,
    created,
    failures,
    activations: () => activations,
  };
}

test('parses the explicit sidecar notification contract and defaults click activation', () => {
  assert.deepEqual(parseNativeNotificationPayload({
    id: 'quota:test:1',
    title: 'title',
    body: 'body',
  }), {
    id: 'quota:test:1',
    title: 'title',
    body: 'body',
  });

  for (const invalid of [
    null,
    {},
    payload({ id: '' }),
    payload({ id: 'x'.repeat(65) }),
    payload({ title: 7 }),
    payload({ body: 'x'.repeat(2_049) }),
    payload({ activateWindowOnClick: false }),
    payload({ activateWindowOnClick: 'yes' }),
  ]) {
    assert.throws(() => parseNativeNotificationPayload(invalid), TypeError);
  }
});

test('shows a native notification and activates the existing window on click', () => {
  const context = harness();

  assert.equal(context.adapter.isAvailable, true);
  assert.equal(context.adapter.show(payload()), 'shown');
  assert.equal(context.created.length, 1);
  assert.deepEqual(context.created[0].options, {
    id: 'quota-below-threshold:test:1',
    title: 'codexU 额度提醒',
    body: 'Codex 5 小时额度剩余 10%。',
  });
  assert.equal(context.created[0].handle.showCount, 1);

  context.created[0].handle.emit('click');
  assert.equal(context.activations(), 1);
  assert.deepEqual(context.failures, []);
});

test('accepts only the always-activate legacy click policy', () => {
  assert.deepEqual(
    parseNativeNotificationPayload(payload({ activateWindowOnClick: true })),
    parseNativeNotificationPayload(payload()),
  );
  assert.throws(
    () => parseNativeNotificationPayload(payload({ activateWindowOnClick: false })),
    TypeError,
  );
});

test('deduplicates a successfully shown notification id with bounded memory', () => {
  const context = harness({ rememberedIdLimit: 2 });

  assert.equal(context.adapter.show(payload({ id: 'one' })), 'shown');
  assert.equal(context.adapter.show(payload({ id: 'one' })), 'duplicate');
  assert.equal(context.adapter.show(payload({ id: 'two' })), 'shown');
  assert.equal(context.adapter.show(payload({ id: 'three' })), 'shown');
  assert.equal(context.adapter.show(payload({ id: 'one' })), 'shown');
  assert.equal(context.created.length, 4);
});

test('a late event from an evicted handle cannot unlock its newer replacement', () => {
  const context = harness({ rememberedIdLimit: 1 });

  assert.equal(context.adapter.show(payload({ id: 'one' })), 'shown');
  const oldHandle = context.created[0].handle;
  assert.equal(context.adapter.show(payload({ id: 'two' })), 'shown');
  assert.equal(context.adapter.show(payload({ id: 'one' })), 'shown');

  oldHandle.emit('click');
  oldHandle.emit('failed', {}, 'late platform failure');
  oldHandle.emit('close');

  assert.equal(context.activations(), 0);
  assert.deepEqual(context.failures, []);
  assert.equal(context.adapter.show(payload({ id: 'one' })), 'duplicate');
  assert.equal(context.created.length, 3);
});

test('late handle events after dispose are completely inert', () => {
  const context = harness();

  assert.equal(context.adapter.show(payload({ id: 'dispose-events' })), 'shown');
  const handle = context.created[0].handle;
  context.adapter.dispose();

  handle.emit('click');
  handle.emit('failed', 'late failure after dispose');
  handle.emit('close');

  assert.equal(context.activations(), 0);
  assert.deepEqual(context.failures, []);
  assert.equal(context.adapter.show(payload({ id: 'dispose-events' })), 'failed');
});

test('reports unavailable notification support without throwing or consuming the id', () => {
  let supported = false;
  const context = harness({ isSupported: () => supported });

  assert.equal(context.adapter.isAvailable, false);
  assert.equal(context.adapter.show(payload()), 'unsupported');
  assert.equal(context.created.length, 0);
  assert.equal(context.failures.at(-1).stage, 'availability');

  supported = true;
  assert.equal(context.adapter.show(payload()), 'shown');
});

test('show and native failures are downgraded to diagnostics', () => {
  const first = new FakeNotification();
  first.showFailure = new Error('show rejected');
  const second = new FakeNotification();
  const third = new FakeNotification();
  const failures = [];
  const handles = [first, second, third];
  const adapter = new NativeNotificationAdapter({
    isSupported: () => true,
    create: () => handles.shift(),
    activateWindow: () => {},
    onFailure: (failure) => failures.push(failure),
    retryDelaysMs: [],
  });

  assert.equal(adapter.show(payload()), 'failed');
  assert.equal(failures[0].stage, 'show');
  // A synchronous failure is retryable because it never enters the de-dupe set.
  assert.equal(adapter.show(payload()), 'shown');
  second.emit('failed', {}, 'platform rejected notification');
  assert.equal(failures[1].stage, 'native');
  assert.match(failures[1].error.message, /platform rejected/u);
  // A later Sidecar replay of the stable logical ID must retry after an
  // asynchronous native failure instead of being suppressed as a duplicate.
  assert.equal(adapter.show(payload()), 'shown');
  assert.equal(third.showCount, 1);
});

test('malformed payloads and activation failures never escape the adapter', () => {
  const context = harness({
    activateWindow: () => { throw new Error('window was destroyed'); },
  });

  assert.equal(context.adapter.show({ title: 'missing fields' }), 'failed');
  assert.equal(context.failures[0].stage, 'payload');
  assert.equal(context.adapter.show(payload()), 'shown');
  assert.doesNotThrow(() => context.created[0].handle.emit('click'));
  assert.equal(context.failures[1].stage, 'activate');
});

test('availability API failures are treated as unsupported', () => {
  const context = harness({
    isSupported: () => { throw new Error('desktop session unavailable'); },
  });

  assert.equal(context.adapter.isAvailable, false);
  assert.equal(context.failures[0].stage, 'availability');
  assert.equal(context.adapter.show(payload()), 'unsupported');
});

test('coalesces repeated payloads while availability retry is pending', async () => {
  let supported = false;
  const context = harness({
    isSupported: () => supported,
    retryDelaysMs: [0, 0],
  });

  assert.equal(context.adapter.show(payload({ body: 'stale body' })), 'unsupported');
  assert.equal(context.adapter.show(payload({ body: 'fresh body' })), 'duplicate');
  supported = true;

  await waitFor(() => context.created.length === 1);
  assert.equal(context.created[0].options.body, 'fresh body');
  assert.equal(context.created[0].handle.showCount, 1);
});

test('retries synchronous show and asynchronous native failures automatically', async () => {
  const first = new FakeNotification();
  first.showFailure = new Error('first show rejected');
  const second = new FakeNotification();
  const third = new FakeNotification();
  const handles = [first, second, third];
  const failures = [];
  const adapter = new NativeNotificationAdapter({
    isSupported: () => true,
    create: () => handles.shift(),
    activateWindow: () => {},
    onFailure: (failure) => failures.push(failure),
    retryDelaysMs: [0, 0],
  });

  assert.equal(adapter.show(payload()), 'failed');
  await waitFor(() => second.showCount === 1);
  second.emit('failed', 'platform rejected the retry');
  await waitFor(() => third.showCount === 1);

  assert.deepEqual(failures.map((failure) => failure.stage), ['show', 'native']);
  assert.equal(adapter.show(payload()), 'duplicate');
});

test('bounds retry attempts and dispose cancels pending timers', async () => {
  let availabilityChecks = 0;
  const bounded = harness({
    isSupported: () => {
      availabilityChecks += 1;
      return false;
    },
    retryDelaysMs: [0, 0],
  });

  assert.equal(bounded.adapter.show(payload()), 'unsupported');
  await waitFor(() => availabilityChecks === 3);
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal(availabilityChecks, 3);

  let supported = false;
  const disposed = harness({
    isSupported: () => supported,
    retryDelaysMs: [25],
  });
  assert.equal(disposed.adapter.show(payload({ id: 'dispose-me' })), 'unsupported');
  disposed.adapter.dispose();
  supported = true;
  await new Promise((resolve) => setTimeout(resolve, 40));
  assert.equal(disposed.created.length, 0);
});

test('remembered id limit evicts pending availability retry timers', async () => {
  let supported = false;
  const context = harness({
    rememberedIdLimit: 2,
    isSupported: () => supported,
    retryDelaysMs: [0],
  });

  assert.equal(context.adapter.show(payload({ id: 'pending-one' })), 'unsupported');
  assert.equal(context.adapter.show(payload({ id: 'pending-two' })), 'unsupported');
  assert.equal(context.adapter.show(payload({ id: 'pending-three' })), 'unsupported');
  supported = true;

  await waitFor(() => context.created.length === 2);
  assert.deepEqual(
    context.created.map(({ options }) => options.id),
    ['pending-two', 'pending-three'],
  );
});

test('remembered id limit evicts pending synchronous show retries', async () => {
  const creationIds = [];
  const context = harness({
    rememberedIdLimit: 1,
    retryDelaysMs: [0],
    create: (options) => {
      creationIds.push(options.id);
      const handle = new FakeNotification();
      if (options.id === 'show-failure') handle.showFailure = new Error('show rejected');
      return handle;
    },
  });

  assert.equal(context.adapter.show(payload({ id: 'show-failure' })), 'failed');
  assert.equal(context.adapter.show(payload({ id: 'replacement' })), 'shown');
  await new Promise((resolve) => setTimeout(resolve, 10));

  assert.deepEqual(creationIds, ['show-failure', 'replacement']);
});

async function waitFor(condition) {
  const deadline = Date.now() + 1_000;
  while (!condition()) {
    if (Date.now() >= deadline) throw new Error('Timed out waiting for notification retry.');
    await new Promise((resolve) => setTimeout(resolve, 1));
  }
}
