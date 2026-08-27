'use strict';

const assert = require('node:assert/strict');
const { mkdtempSync } = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  resetMaintenanceShutdownMarker,
  resolveMaintenanceShutdownMarker,
  waitForMaintenanceShutdown,
  writeMaintenanceShutdownFailureMarker,
  writeMaintenanceShutdownMarker,
} = require('../dist/maintenance.js');

test('accepts only the dedicated maintenance marker inside the system temp tree', () => {
  const marker = path.join(tmpdir(), 'is-test.tmp', 'codexu-maintenance-shutdown.marker');
  assert.equal(resolveMaintenanceShutdownMarker([
    'CodexU.exe',
    '--maintenance-shutdown',
    `--maintenance-shutdown-marker=${marker}`,
  ], tmpdir()), path.resolve(marker));
  assert.equal(resolveMaintenanceShutdownMarker(['CodexU.exe'], tmpdir()), undefined);

  assert.throws(() => resolveMaintenanceShutdownMarker([
    'CodexU.exe',
    '--maintenance-shutdown',
    `--maintenance-shutdown-marker=${path.join(tmpdir(), 'is-test.tmp', 'unexpected.marker')}`,
  ], tmpdir()), /dedicated file/);
  assert.throws(() => resolveMaintenanceShutdownMarker([
    'CodexU.exe',
    '--maintenance-shutdown',
    `--maintenance-shutdown-marker=${path.resolve(tmpdir(), '..', 'codexu-maintenance-shutdown.marker')}`,
  ], tmpdir()), /system temp/);
});

test('waits for the marked process to be absent and removes the marker', async () => {
  const isolatedRoot = mkdtempSync(path.join(tmpdir(), 'codexu-maintenance-test-'));
  const marker = path.join(isolatedRoot, 'codexu-maintenance-shutdown.marker');
  resetMaintenanceShutdownMarker(marker);

  try {
    writeMaintenanceShutdownMarker(marker, 2147483647);
    await waitForMaintenanceShutdown(marker, 1_000);
    assert.equal(require('node:fs').existsSync(marker), false);
  } finally {
    resetMaintenanceShutdownMarker(marker);
    require('node:fs').rmSync(isolatedRoot, { recursive: true, force: true });
  }
});

test('fails immediately when the resident process reports an unsafe Sidecar shutdown', async () => {
  const isolatedRoot = mkdtempSync(path.join(tmpdir(), 'codexu-maintenance-test-'));
  const marker = path.join(isolatedRoot, 'codexu-maintenance-shutdown.marker');

  try {
    writeMaintenanceShutdownFailureMarker(marker);
    await assert.rejects(
      waitForMaintenanceShutdown(marker, 1_000),
      /could not stop its Sidecar safely/,
    );
    assert.equal(require('node:fs').existsSync(marker), false);
  } finally {
    resetMaintenanceShutdownMarker(marker);
    require('node:fs').rmSync(isolatedRoot, { recursive: true, force: true });
  }
});
