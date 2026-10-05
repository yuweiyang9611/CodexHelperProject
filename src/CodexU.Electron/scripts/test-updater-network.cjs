'use strict';

// Run the production updater transport in Electron, not Node's fetch runtime.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm } = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');

(async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'codexu-updater-network-'));
  let child;
  try {
    const env = { ...process.env, CODEXU_NETWORK_TEST_ROOT: root };
    delete env.ELECTRON_RUN_AS_NODE;
    delete env.CODEXU_GITHUB_TOKEN;
    child = spawn(require('electron'), [path.join(__dirname, 'updater-network-test-app.cjs'),
      ...(process.argv.includes('--legacy-net-fetch') ? ['--legacy-net-fetch'] : [])],
    { env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    let output = '';
    let errors = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => { if (output.length < 256 * 1024) output += chunk; });
    child.stderr.on('data', chunk => { if (errors.length < 256 * 1024) errors += chunk; });
    let timedOut = false;
    const code = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { timedOut = true; child.kill(); }, 60000);
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.once('exit', code => { clearTimeout(timer); resolve(code); });
    });
    // Only emit controlled fixture records, never Electron diagnostics containing
    // session paths, credentials, or network URLs.
    const records = [...output.split(/\r?\n/), ...errors.split(/\r?\n/)]
      .filter(line => /^UPDATER_NETWORK_(?:OK|FAIL|CASE):/.test(line))
      .filter(line => !line.startsWith('UPDATER_NETWORK_CASE:') || code !== 0 || timedOut);
    for (const record of records) console.log(record);
    assert(!timedOut, 'Electron updater network tests timed out.');
    assert.equal(code, 0, 'real Electron updater network regression failed');
    assert(records.some(line => line.startsWith('UPDATER_NETWORK_OK:')), 'Electron did not report completion');
  } finally {
    if (child?.pid && child.exitCode === null && child.signalCode === null) {
      const exited = new Promise(resolve => child.once('exit', resolve));
      child.kill();
      await exited;
    }
    await rm(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
