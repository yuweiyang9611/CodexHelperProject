const assert = require('node:assert/strict');
const test = require('node:test');
const { decideQuitRequest } = require('../dist/lifecycle.js');

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
