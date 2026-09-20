const assert = require('node:assert/strict');
const test = require('node:test');
const { createHash } = require('node:crypto');
const { mkdtempSync, rmSync, readFileSync, writeFileSync, existsSync, readdirSync } = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { AutomaticUpdater, githubFetch, selectPackage, installerArguments, supportsAutomaticUpdates } = require('../dist/automaticUpdates');

const version = '0.7.0';
const name = `CodexU-${version}-win-x64-setup.exe`;
const base = `https://github.com/yuweiyang9611/CodexHelperProject/releases/download/v${version}/`;
const bytes = Buffer.from('test installer bytes');
const digest = createHash('sha256').update(bytes).digest('hex');
function release() {
  return { tag_name: `v${version}`, draft: false, prerelease: false, assets: [
    { name, state: 'uploaded', size: bytes.length, digest: `sha256:${digest}`, browser_download_url: base + name },
    { name: name.replace('.exe', '.sha256'), state: 'uploaded', browser_download_url: base + name.replace('.exe', '.sha256') },
  ] };
}
function fixture(t, options = {}) {
  const root = mkdtempSync(path.join(tmpdir(), 'codexu-auto-update-'));
  const calls = [];
  const launches = [];
  const states = [];
  const updater = new AutomaticUpdater({ supported: true, directory: root, installDirectory: 'C:\\Apps with spaces\\codexU',
    fetch: async (url, init) => {
      calls.push({ url, init });
      if (options.fetch) return options.fetch(url, init);
      if (url.includes('api.github.com')) return Response.json(options.release ?? release());
      if (url.endsWith('.sha256')) return new Response(`${digest}  ${name}\n`);
      return new Response(options.bytes ?? bytes);
    },
    check: options.check ?? (async () => ({ isUpdateAvailable: true, latestVersion: version, isPrerelease: false })),
    launch: async (...args) => { launches.push(args); if (options.launchError) throw new Error('spawn failed'); },
    changed: state => states.push(state),
    ...options.host,
  });
  t.after(() => { updater.stop(); rmSync(root, { recursive: true, force: true }); });
  updater.configure({ checkForUpdates: false, autoInstallUpdates: options.automatic ?? false, includePrereleaseUpdates: false });
  return { updater, root, calls, launches, states };
}
async function stage(f) { await f.updater.check(true); await f.updater.download(); }

test('downloads once, validates digest and size, and restarts only when requested', async t => {
  const f = fixture(t);
  await f.updater.check(true);
  await Promise.all([f.updater.download(), f.updater.download()]);
  assert.equal(f.updater.getState().phase, 'ready');
  assert.deepEqual(readFileSync(path.join(f.root, name)), bytes);
  assert.equal(f.calls.filter(c => c.url.endsWith('.exe')).length, 1);
  assert.equal(await f.updater.installOnQuit(), false);
  f.updater.requestRestart();
  f.updater.stop();
  assert.equal(await f.updater.installOnQuit(), true);
  assert.deepEqual(f.launches[0][1], installerArguments('C:\\Apps with spaces\\codexU', true));
  assert.ok(f.launches[0][1].includes('/NORESTART'));
  assert.ok(f.launches[0][1].includes('/NOCLOSEAPPLICATIONS'));
});

test('automatic mode installs on normal quit without reopening the app', async t => {
  const f = fixture(t, { automatic: true });
  await stage(f);
  f.updater.stop();
  assert.equal(await f.updater.installOnQuit(), true);
  assert.ok(!f.launches[0][1].includes('/CODEXURESTART=1'));
});

test('rechecks the on-disk hash immediately before launching', async t => {
  const f = fixture(t, { automatic: true });
  await stage(f);
  writeFileSync(path.join(f.root, name), Buffer.alloc(bytes.length));
  assert.equal(await f.updater.installOnQuit(), false);
  assert.equal(f.launches.length, 0);
  assert.match(f.updater.getState().message, /校验失败/);
});

for (const [label, payload] of [['truncated', bytes.subarray(1)], ['oversized', Buffer.concat([bytes, bytes])], ['corrupt', Buffer.alloc(bytes.length)]]) {
  test(`rejects ${label} installer and removes partial download`, async t => {
    const f = fixture(t, { bytes: payload });
    await stage(f);
    assert.equal(f.updater.getState().phase, 'error');
    assert.equal(readdirSync(f.root).length, 0);
    assert.throws(() => f.updater.requestRestart(), /尚未下载完成/);
    assert.equal(f.launches.length, 0);
  });
}

test('reuses a verified cache and only deletes updater-owned obsolete files', async t => {
  const f = fixture(t);
  writeFileSync(path.join(f.root, name), bytes);
  writeFileSync(path.join(f.root, 'CodexU-0.6.0-win-x64-setup.exe'), 'old');
  writeFileSync(path.join(f.root, 'my-file.txt'), 'keep');
  await stage(f);
  assert.equal(f.calls.filter(c => c.url.endsWith('.exe')).length, 0);
  assert.ok(existsSync(path.join(f.root, 'my-file.txt')));
  assert.ok(!existsSync(path.join(f.root, 'CodexU-0.6.0-win-x64-setup.exe')));
});

test('revoking automatic updates disarms a staged installer', async t => {
  const f = fixture(t, { automatic: true });
  await stage(f);
  f.updater.configure({ checkForUpdates: false, autoInstallUpdates: false });
  assert.equal(f.updater.getState().phase, 'idle');
  assert.equal(await f.updater.installOnQuit(), false);
});

test('channel changes invalidate in-flight check results', async t => {
  let resolve;
  const f = fixture(t, { automatic: true, check: () => new Promise(r => { resolve = r; }) });
  const pending = f.updater.check(true);
  f.updater.configure({ checkForUpdates: false, autoInstallUpdates: true, includePrereleaseUpdates: true });
  resolve({ isUpdateAvailable: true, latestVersion: version, isPrerelease: false });
  await pending;
  await f.updater.download();
  assert.equal(f.calls.length, 0);
});

test('disabling automatic updates aborts a download before it can be staged', async t => {
  let started;
  const ready = new Promise(r => { started = r; });
  const f = fixture(t, { automatic: true, fetch: async (_url, init) => {
    started();
    await new Promise((_, reject) => init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true }));
  } });
  await f.updater.check(true);
  const pending = f.updater.download();
  await ready;
  f.updater.configure({ checkForUpdates: false, autoInstallUpdates: false });
  await pending;
  assert.equal(f.updater.getState().phase, 'idle');
  assert.equal(await f.updater.installOnQuit(), false);
});

test('portable mode never downloads or launches an installer', async t => {
  const f = fixture(t, { automatic: true, host: { supported: false } });
  await stage(f);
  assert.equal(f.calls.length, 0);
  assert.equal(await f.updater.installOnQuit(), false);
  assert.equal(supportsAutomaticUpdates('win32', true, false, path.join(f.root, 'CodexU.exe')), false);
});

test('failed installer launch is reported and cannot be retried without staging again', async t => {
  const f = fixture(t, { automatic: true, launchError: true });
  await stage(f);
  assert.equal(await f.updater.installOnQuit(), false);
  assert.equal(f.updater.getState().phase, 'error');
  assert.equal(await f.updater.installOnQuit(), false);
  assert.equal(f.launches.length, 1);
});

test('rejects missing/duplicate assets, wrong tags, drafts, URLs and digests', () => {
  const candidate = { version, prerelease: false };
  for (const mutate of [
    r => { r.assets.pop(); },
    r => { r.assets.push(r.assets[0]); },
    r => { r.tag_name = 'v9.9.9'; },
    r => { r.draft = true; },
    r => { r.prerelease = true; },
    r => { r.assets[0].browser_download_url = 'https://evil.example/install.exe'; },
    r => { r.assets[0].digest = 'md5:abc'; },
    r => { r.assets[0].size = 1024 ** 3; },
  ]) {
    const r = release(); mutate(r);
    assert.throws(() => selectPackage(r, candidate));
  }
  assert.throws(() => selectPackage(release(), { version: '../escape', prerelease: false }));
});

test('rejects untrusted redirects and never sends tokens to asset CDN', async t => {
  const previous = process.env.CODEXU_GITHUB_TOKEN;
  process.env.CODEXU_GITHUB_TOKEN = 'test-token';
  t.after(() => { if (previous === undefined) delete process.env.CODEXU_GITHUB_TOKEN; else process.env.CODEXU_GITHUB_TOKEN = previous; });
  const signal = new AbortController().signal;
  const calls = [];
  const fetcher = async (url, init) => {
    calls.push(init);
    return url === base + name ? new Response(null, { status: 302, headers: { location: 'https://release-assets.githubusercontent.com/asset' } }) : new Response(bytes);
  };
  await githubFetch(fetcher, base + name, signal);
  assert.ok(calls.every(c => !c.headers.Authorization));
  await assert.rejects(githubFetch(async () => new Response(null, { status: 302, headers: { location: 'https://evil.example/asset' } }), base + name, signal), /受信任/);
  await assert.rejects(githubFetch(fetcher, 'http://github.com/asset', signal), /受信任/);
});

test('a failed download can be retried without restarting the app', async t => {
  let attempt = 0;
  const f = fixture(t, { fetch: async url => {
    if (url.includes('api.github.com')) return Response.json(release());
    if (url.endsWith('.sha256')) return new Response(`${digest}  ${name}`);
    if (attempt++ === 0) throw new Error('offline');
    return new Response(bytes);
  } });
  await stage(f);
  assert.equal(f.updater.getState().phase, 'error');
  await f.updater.download();
  assert.equal(f.updater.getState().phase, 'ready');
});
