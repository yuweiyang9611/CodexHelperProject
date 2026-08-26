const assert = require('node:assert/strict');
const test = require('node:test');
const {
  isAllowedMethod,
  isTrustedRendererUrl,
  validateRendererPayload,
} = require('../dist/security.js');

test('accepts only the trusted app origin', () => {
  assert.equal(isTrustedRendererUrl('app://codexu/index.html'), true);
  assert.equal(isTrustedRendererUrl('app://codexu/assets/app.js'), true);
  assert.equal(isTrustedRendererUrl('app://other/index.html'), false);
  assert.equal(isTrustedRendererUrl('https://codexu/index.html'), false);
  assert.equal(isTrustedRendererUrl('not a URL'), false);
});

test('uses a closed request-method allow-list', () => {
  assert.equal(isAllowedMethod('app.initialize'), true);
  assert.equal(isAllowedMethod('window.hide'), true);
  assert.equal(isAllowedMethod('host.dialog.confirm'), false);
  assert.equal(isAllowedMethod('shell.execute'), false);
});

test('rejects non-object and oversized renderer payloads', () => {
  assert.throws(() => validateRendererPayload('app.initialize', null), TypeError);
  assert.throws(() => validateRendererPayload('app.initialize', []), TypeError);
  assert.throws(
    () => validateRendererPayload('app.initialize', { value: 'x'.repeat(1024 * 1024) }),
    RangeError,
  );
});
