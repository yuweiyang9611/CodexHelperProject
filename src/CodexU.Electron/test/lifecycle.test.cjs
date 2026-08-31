const assert = require('node:assert/strict');
const {
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
} = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { requestTimeoutForMethod } = require('../dist/ipcRequestTimeouts.js');
const { decideQuitRequest } = require('../dist/lifecycle.js');
const {
  PersistentLog,
  OVERSIZED_STDERR_LINE,
  TextLineBuffer,
  redactSensitiveText,
} = require('../dist/persistentLog.js');
const { RecoverySupervisor } = require('../dist/recoverySupervisor.js');
const {
  CompletionQueue,
  GenerationFence,
  RecoveryPromptQueue,
  SingleFlightOperation,
} = require('../dist/runtimeCoordination.js');

test('keeps graceful quit while native shutdown is still protected', () => {
  assert.deepEqual(decideQuitRequest(0, 1, false), {
    exitCode: 1,
    action: 'quit',
  });
});

test('forces a late non-zero exit after graceful quit has been authorized', () => {
  assert.deepEqual(decideQuitRequest(0, 2, true), {
    exitCode: 2,
    action: 'exit',
  });
});

test('never lowers a previously requested fatal exit code', () => {
  assert.deepEqual(decideQuitRequest(2, 1, true), {
    exitCode: 2,
    action: 'exit',
  });
});

test('continues the normal quit path for a successful exit', () => {
  assert.deepEqual(decideQuitRequest(0, 0, true), {
    exitCode: 0,
    action: 'quit',
  });
});

test('budgets settings updates for startup mutation and compensating rollback', () => {
  const nestedStartupDeadlines = 25_000 * 2;
  const timeout = requestTimeoutForMethod('settings.update');

  assert.equal(timeout, 60_000);
  assert.ok(timeout >= nestedStartupDeadlines + 10_000);
  assert.equal(requestTimeoutForMethod('settings.get'), 30_000);
});

test('redacts credentials, tokens, identities, and user profile paths', () => {
  const fakeEmail = ['alice', 'example.test'].join('@');
  const fakeGitHubToken = ['ghp', '123456789012345678901234567890123456'].join('_');
  const credentialUrl = ['https://alice:hunter2', 'example.test/path'].join('@');
  const source = [
    'Authorization: Bearer bearer-secret',
    'password="correct horse battery staple"',
    '--api-key=command-line-secret',
    credentialUrl,
    fakeGitHubToken,
    fakeEmail,
    'C:\\Users\\Alice\\AppData\\Local',
    '/home/alice/.config/codexu',
  ].join(' ');

  const redacted = redactSensitiveText(source);
  for (const secret of [
    'bearer-secret',
    'correct horse battery staple',
    'command-line-secret',
    'hunter2',
    fakeGitHubToken,
    fakeEmail,
    '\\Users\\Alice\\',
    '/home/alice/',
  ]) {
    assert.equal(redacted.includes(secret), false, secret);
  }
  assert.match(redacted, /\[REDACTED\]/u);
  assert.match(redacted, /C:\\Users\\\[USER\]\\AppData/u);
  assert.match(redacted, /\/home\/\[USER\]\/\.config/u);
});

test('redacts authorization values in serialized objects', () => {
  const source = JSON.stringify({
    Authorization: 'Bearer serialized-secret',
    'Proxy-Authorization': 'Basic proxy-secret',
    password: 'json-password',
  });
  const redacted = redactSensitiveText(source);
  assert.equal(redacted.includes('serialized-secret'), false);
  assert.equal(redacted.includes('proxy-secret'), false);
  assert.equal(redacted.includes('json-password'), false);
});

test('buffers stderr chunks until complete lines can be redacted together', () => {
  const buffer = new TextLineBuffer();
  assert.deepEqual(buffer.push('Authorization: Bearer split-'), []);
  assert.deepEqual(buffer.push('secret\r\nnext line\npartial'), [
    'Authorization: Bearer split-secret',
    'next line',
  ]);
  const completed = buffer.flush();
  assert.equal(completed, 'partial');
  assert.equal(redactSensitiveText('Authorization: Bearer split-secret').includes('split-secret'), false);
  assert.equal(buffer.flush(), undefined);
});

test('bounds unterminated stderr lines without persisting oversized fragments', () => {
  const buffer = new TextLineBuffer(8);
  assert.deepEqual(buffer.push('secret-'), []);
  assert.deepEqual(buffer.push('material-without-newline'), []);
  assert.deepEqual(buffer.push('\nsafe\n'), [
    OVERSIZED_STDERR_LINE,
    'safe',
  ]);

  const unfinished = new TextLineBuffer(4);
  assert.deepEqual(unfinished.push('oversized'), []);
  assert.equal(unfinished.flush(), OVERSIZED_STDERR_LINE);
});

test('keeps persistent logs within file-size and retention limits', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'codexu-log-test-'));
  let tick = 0;
  const log = new PersistentLog({
    directory,
    maximumFileBytes: 256,
    maximumFiles: 3,
    now: () => new Date(Date.UTC(2026, 0, 1, 0, 0, tick++)),
  });

  try {
    for (let index = 0; index < 12; index += 1) {
      assert.equal(log.write(
        'error',
        'sidecar',
        `failure=${index} token=secret-${index} ${'x'.repeat(120)}`,
      ), true);
    }

    const files = readdirSync(directory)
      .filter((fileName) => /^codexu(?:\.\d+)?\.log$/u.test(fileName))
      .sort();
    assert.deepEqual(files, ['codexu.1.log', 'codexu.2.log', 'codexu.log']);
    for (const fileName of files) {
      const filePath = path.join(directory, fileName);
      assert.ok(statSync(filePath).size <= 256, fileName);
      const content = readFileSync(filePath, 'utf8');
      assert.doesNotMatch(content, /secret-\d+/u);
      assert.match(content, /token=\[REDACTED\]/u);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  }
});

test('truncates a single oversized persistent-log entry without exceeding its cap', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'codexu-log-test-'));
  const log = new PersistentLog({ directory, maximumFileBytes: 128 });

  try {
    assert.equal(log.write('warn', 'renderer', '🔐'.repeat(1_000)), true);
    assert.ok(statSync(log.filePath).size <= 128);
    assert.match(readFileSync(log.filePath, 'utf8'), /\[truncated\]\n$/u);
  } finally {
    rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  }
});

test('supervises recovery with idempotent backoff, a cap, and an open circuit', () => {
  const supervisor = new RecoverySupervisor({
    maximumAttempts: 3,
    initialDelayMilliseconds: 100,
    maximumDelayMilliseconds: 250,
    stablePeriodMilliseconds: 1_000,
  });

  assert.deepEqual(supervisor.recordFailure(0), {
    action: 'retry', attempt: 1, delayMilliseconds: 100,
  });
  assert.deepEqual(supervisor.recordFailure(1), {
    action: 'wait', attempt: 1, reason: 'retry-pending',
  });
  assert.equal(supervisor.markRecoveryStarted(1), true);

  assert.deepEqual(supervisor.recordFailure(100), {
    action: 'retry', attempt: 2, delayMilliseconds: 200,
  });
  assert.equal(supervisor.markRecoveryStarted(2), true);
  assert.deepEqual(supervisor.recordFailure(200), {
    action: 'retry', attempt: 3, delayMilliseconds: 250,
  });
  assert.equal(supervisor.markRecoveryStarted(3), true);
  assert.deepEqual(supervisor.recordFailure(300), {
    action: 'stop', attempts: 3, reason: 'circuit-open',
  });
  assert.deepEqual(supervisor.recordFailure(5_000), {
    action: 'stop', attempts: 3, reason: 'circuit-open',
  });
  assert.deepEqual(supervisor.snapshot(), {
    state: 'circuit-open',
    attempts: 3,
    maximumAttempts: 3,
    circuitOpenedAtMilliseconds: 300,
  });
});

test('resets recovery budget manually or after a stable recovered period', () => {
  const supervisor = new RecoverySupervisor({
    maximumAttempts: 2,
    initialDelayMilliseconds: 10,
    stablePeriodMilliseconds: 500,
  });

  assert.equal(supervisor.recordFailure(0).action, 'retry');
  assert.equal(supervisor.markRecoveryStarted(1), true);
  supervisor.markRecovered(100);
  assert.equal(supervisor.markStable(599), false);
  assert.equal(supervisor.markStable(600), true);
  assert.deepEqual(supervisor.snapshot(), {
    state: 'healthy', attempts: 0, maximumAttempts: 2,
  });

  assert.deepEqual(supervisor.recordFailure(700), {
    action: 'retry', attempt: 1, delayMilliseconds: 10,
  });
  supervisor.reset();
  assert.equal(supervisor.markRecoveryStarted(1), false);
  assert.deepEqual(supervisor.recordFailure(701), {
    action: 'retry', attempt: 1, delayMilliseconds: 10,
  });
});

test('coalesces concurrent renderer navigation into one in-flight operation', async () => {
  const singleFlight = new SingleFlightOperation();
  let invocationCount = 0;
  let finish;
  const operation = () => {
    invocationCount += 1;
    return new Promise((resolve) => {
      finish = resolve;
    });
  };

  const first = singleFlight.run(operation);
  const second = singleFlight.run(operation);
  assert.equal(first, second);
  assert.equal(singleFlight.running, true);
  await Promise.resolve();
  assert.equal(invocationCount, 1);
  finish();
  await first;
  assert.equal(singleFlight.running, false);

  await singleFlight.run(async () => {
    invocationCount += 1;
  });
  assert.equal(invocationCount, 2);
});

test('queues a fresh renderer navigation instead of joining an obsolete flight', async () => {
  const singleFlight = new SingleFlightOperation();
  const events = [];
  let finishObsolete;
  let finishFresh;

  const obsolete = singleFlight.run(async () => {
    events.push('obsolete:start');
    await new Promise((resolve) => {
      finishObsolete = resolve;
    });
    events.push('obsolete:end');
  });
  await Promise.resolve();

  const fresh = singleFlight.runFresh(async () => {
    events.push('fresh:start');
    await new Promise((resolve) => {
      finishFresh = resolve;
    });
    events.push('fresh:end');
  });
  const joinedAfterPublication = singleFlight.run(async () => {
    assert.fail('a regular caller must join the already queued fresh flight');
  });

  assert.notEqual(fresh, obsolete);
  assert.equal(joinedAfterPublication, fresh);
  assert.deepEqual(events, ['obsolete:start']);

  finishObsolete();
  await obsolete;
  await Promise.resolve();
  assert.deepEqual(events, ['obsolete:start', 'obsolete:end', 'fresh:start']);
  assert.equal(singleFlight.running, true);

  finishFresh();
  await fresh;
  assert.deepEqual(events, ['obsolete:start', 'obsolete:end', 'fresh:start', 'fresh:end']);
  assert.equal(singleFlight.running, false);
});

test('invalidates a renderer recovery result when a newer crash arrives in flight', () => {
  const failures = new GenerationFence();
  failures.advance();
  const attemptGeneration = failures.snapshot();
  assert.equal(failures.isCurrent(attemptGeneration), true);

  failures.advance();
  assert.equal(failures.isCurrent(attemptGeneration), false);
  assert.throws(() => failures.isCurrent(-1), /non-negative safe integer/u);
});

test('fences a native settings refresh after a newer renderer update', () => {
  const updates = new GenerationFence();
  const refreshGeneration = updates.snapshot();
  assert.equal(updates.isCurrent(refreshGeneration), true);
  updates.advance();
  assert.equal(updates.isCurrent(refreshGeneration), false);
});

test('retains distinct recovery prompts while coalescing duplicate components', () => {
  const queue = new RecoveryPromptQueue();
  assert.equal(queue.enqueue('sidecar', '后台服务'), true);
  assert.equal(queue.enqueue('sidecar', '重复后台服务'), false);
  assert.equal(queue.enqueue('renderer', '界面进程'), true);
  assert.equal(queue.size, 2);

  assert.deepEqual(queue.take(), { key: 'sidecar', value: '后台服务' });
  assert.equal(queue.take(), undefined);
  assert.equal(queue.enqueue('sidecar', '仍然重复'), false);
  queue.complete('sidecar');
  assert.deepEqual(queue.take(), { key: 'renderer', value: '界面进程' });
  queue.complete('renderer');
  assert.equal(queue.size, 0);
});

test('clears queued recovery prompts without invalidating the active prompt', () => {
  const queue = new RecoveryPromptQueue();
  queue.enqueue('sidecar', '后台服务');
  queue.enqueue('renderer', '界面进程');
  assert.equal(queue.take().key, 'sidecar');
  queue.clearQueued();
  assert.equal(queue.size, 1);
  queue.complete('sidecar');
  assert.equal(queue.take(), undefined);
});

test('cancels queued or active recovery prompts without losing other components', () => {
  const queue = new RecoveryPromptQueue();
  queue.enqueue('sidecar', '后台服务');
  queue.enqueue('renderer', '界面进程');
  assert.equal(queue.cancel('renderer'), 'queued');
  assert.equal(queue.size, 1);
  assert.equal(queue.take().key, 'sidecar');
  assert.equal(queue.cancel('sidecar'), 'active');
  assert.equal(queue.isCancelled('sidecar'), true);
  queue.complete('sidecar');
  assert.equal(queue.isCancelled('sidecar'), false);
  assert.equal(queue.size, 0);
});

test('returns a completed shutdown outcome to late maintenance registrations', () => {
  const queue = new CompletionQueue();
  assert.deepEqual(queue.register('before'), { completed: false });
  assert.deepEqual(queue.complete({ ok: true }), ['before']);
  assert.deepEqual(queue.register('after'), { completed: true, outcome: { ok: true } });
  assert.throws(() => queue.complete({ ok: false }), /already completed/u);
});
