const assert = require('node:assert/strict');
const {
  mkdirSync,
  mkdtempSync,
  rmSync,
  writeFileSync,
} = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
  isAllowedMethod,
  isTrustedRendererUrl,
  validateRendererPayload,
} = require('../dist/security.js');

const releaseIntegrity = import('../scripts/release-integrity.mjs');

function createElectronVersionFixture(overrides = {}) {
  const root = mkdtempSync(path.join(tmpdir(), 'codexu-electron-version-test-'));
  const declared = overrides.declared ?? '43.4.1';
  mkdirSync(path.join(root, 'node_modules', 'electron'), { recursive: true });
  writeFileSync(path.join(root, 'package.json'), JSON.stringify({
    devDependencies: { electron: declared },
  }));
  writeFileSync(path.join(root, 'package-lock.json'), JSON.stringify({
    packages: {
      '': { devDependencies: { electron: overrides.lockDeclaration ?? declared } },
      'node_modules/electron': { version: overrides.lockVersion ?? declared },
    },
  }));
  writeFileSync(path.join(root, 'node_modules', 'electron', 'package.json'), JSON.stringify({
    version: overrides.installedVersion ?? declared,
  }));
  return root;
}

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
  assert.equal(isAllowedMethod('settings.reconcileStartupRegistration'), false);
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

test('requires one exact Electron version across manifest, lock and installation', async (context) => {
  const { resolvePinnedElectronVersion } = await releaseIntegrity;
  const validRoot = createElectronVersionFixture();
  const rangedRoot = createElectronVersionFixture({ declared: '^43.4.1' });
  const staleLockRoot = createElectronVersionFixture({ lockVersion: '43.4.2' });
  context.after(() => {
    for (const root of [validRoot, rangedRoot, staleLockRoot]) {
      rmSync(root, { recursive: true, force: true });
    }
  });

  assert.equal(resolvePinnedElectronVersion(validRoot), '43.4.1');
  assert.throws(() => resolvePinnedElectronVersion(rangedRoot), /exact version/);
  assert.throws(() => resolvePinnedElectronVersion(staleLockRoot), /version mismatch/);
});

test('rejects stale or unresolved generated legal payloads', async (context) => {
  const {
    assertLegalPayloadIsCurrent,
    generatedLegalPayloadPaths,
  } = await releaseIntegrity;
  const trackedRoot = mkdtempSync(path.join(tmpdir(), 'codexu-legal-tracked-test-'));
  const generatedRoot = mkdtempSync(path.join(tmpdir(), 'codexu-legal-generated-test-'));
  context.after(() => {
    rmSync(trackedRoot, { recursive: true, force: true });
    rmSync(generatedRoot, { recursive: true, force: true });
  });

  for (const relativePath of generatedLegalPayloadPaths) {
    const contents = relativePath === 'THIRD-PARTY-INVENTORY.md'
      ? '| npm | example | 1.0.0 | MIT | https://example.invalid |\n'
      : `legal payload: ${relativePath}\n`;
    for (const root of [trackedRoot, generatedRoot]) {
      const filePath = path.join(root, relativePath);
      mkdirSync(path.dirname(filePath), { recursive: true });
      writeFileSync(filePath, contents);
    }
  }

  await assert.doesNotReject(assertLegalPayloadIsCurrent(trackedRoot, generatedRoot));
  writeFileSync(path.join(generatedRoot, 'THIRD-PARTY-LICENSES.txt'), 'stale\n');
  await assert.rejects(
    assertLegalPayloadIsCurrent(trackedRoot, generatedRoot),
    /stale or incomplete/,
  );

  const unknownInventory = '| npm | example | 1.0.0 | UNKNOWN - review required | source |\n';
  writeFileSync(path.join(trackedRoot, 'THIRD-PARTY-LICENSES.txt'), 'same\n');
  writeFileSync(path.join(generatedRoot, 'THIRD-PARTY-LICENSES.txt'), 'same\n');
  writeFileSync(path.join(trackedRoot, 'THIRD-PARTY-INVENTORY.md'), unknownInventory);
  writeFileSync(path.join(generatedRoot, 'THIRD-PARTY-INVENTORY.md'), unknownInventory);
  await assert.rejects(
    assertLegalPayloadIsCurrent(trackedRoot, generatedRoot),
    /contains UNKNOWN/,
  );
});
