'use strict';

const assert = require('node:assert/strict');
const { createHash, randomUUID } = require('node:crypto');
const { readFile, readdir } = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { app, net, session } = require('electron');
const { AutomaticUpdater, githubFetch } = require('../dist/automaticUpdates');

const root = process.env.CODEXU_NETWORK_TEST_ROOT;
if (!root) throw new Error('Updater network tests require an isolated directory.');
app.setPath('userData', path.join(root, 'electron'));
app.setPath('sessionData', path.join(root, 'session'));
app.disableHardwareAcceleration();

const version = '0.7.0';
const name = `CodexU-${version}-win-x64-setup.exe`;
const downloadBase = `https://github.com/yuweiyang9611/CodexHelperProject/releases/download/v${version}/`;
const api = `https://api.github.com/repos/yuweiyang9611/CodexHelperProject/releases/tags/v${version}`;
const cdn = 'https://release-assets.githubusercontent.com/updater-network-fixture/';
const bytes = Buffer.alloc(128 * 1024, 71);
const digest = createHash('sha256').update(bytes).digest('hex');
const release = { tag_name: `v${version}`, draft: false, prerelease: false, assets: [
  { name, state: 'uploaded', size: bytes.length, digest: `sha256:${digest}`, browser_download_url: downloadBase + name },
  { name: name.replace('.exe', '.sha256'), state: 'uploaded', browser_download_url: downloadBase + name.replace('.exe', '.sha256') },
] };
const unhandled = [];
let rejectUnexpected;
const safeError = error => String(error?.message ?? error).replaceAll(root, '<fixture>')
  .replace(/\bhttps?:\/\/[^\s]+/gi, '<URL>')
  .replaceAll('fixture-token-not-real', '[REDACTED]').split('\n')[0].slice(0, 180);
function unexpected(kind, error) {
  unhandled.push(kind);
  if (rejectUnexpected) rejectUnexpected(new Error(`${kind}: ${safeError(error)}`));
  else { console.error(`UPDATER_NETWORK_FAIL: ${kind}: ${safeError(error)}`); app.exit(1); }
}
process.on('unhandledRejection', error => unexpected('unhandled rejection', error));
process.on('uncaughtException', error => unexpected('uncaught exception', error));
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function eventually(predicate, description) {
  const until = Date.now() + 5000;
  while (!predicate()) {
    if (Date.now() > until) throw new Error(description);
    await delay(10);
  }
}

(async () => {
  await app.whenReady();
  const isolated = session.fromPartition(`codexu-updater-network-${randomUUID()}`);
  await isolated.setProxy({ mode: 'direct' });
  const requestWritesClosed = new Set();
  const network = { request: options => {
    const request = net.request({ ...options, session: isolated });
    request.once('close', () => requestWritesClosed.add(new URL(options.url).pathname));
    return request;
  } };
  const transport = process.argv.includes('--legacy-net-fetch')
    ? (url, options) => isolated.fetch(url, options)
    : require('../dist/updaterFetch').createUpdaterFetch(network);
  const hits = new Map();
  const closed = new Set();
  const sockets = new Set();
  let largeSent = 0;
  let largeAtPause = 0;
  let largeComplete = false;
  let cookieReceived = false;
  const largeTotal = 64 * 1024 * 1024;
  const server = http.createServer((request, response) => {
    const route = new URL(request.url, 'http://localhost').pathname;
    cookieReceived ||= request.headers.cookie !== undefined;
    hits.set(route, (hits.get(route) ?? 0) + 1);
    response.on('close', () => closed.add(route));
    const redirect = location => { response.writeHead(302, { Location: location }); response.end(); };
    if (route === '/redirect') return redirect('/target');
    if (route === '/trusted') return redirect(cdn + 'asset');
    if (route === '/denied') return redirect('https://untrusted.invalid/asset');
    if (route === '/api-redirect') return redirect(cdn + 'asset');
    if (route === '/loop') return redirect(downloadBase + 'loop');
    if (route === '/checksum-redirect') return redirect(cdn + 'checksum');
    if (route === '/installer-redirect') return redirect(cdn + 'installer');
    if (route === '/missing-location') { response.writeHead(302); return response.end(); }
    if (route === '/release') { response.setHeader('Content-Type', 'application/json'); return response.end(JSON.stringify(release)); }
    if (route === '/checksum') return response.end(`${digest}  ${name}\n`);
    if (route === '/http-error') { response.writeHead(404); return response.end('not found'); }
    if (route === '/cacheable') {
      response.setHeader('Cache-Control', 'max-age=3600');
      return response.end(String(hits.get(route)));
    }
    if (route === '/no-content') { response.writeHead(204); return response.end(); }
    if (route === '/slow-headers') return;
    if (route === '/hold-body') {
      // Plain text avoids MIME-dependent response gating in Chromium while
      // keeping the HTTP body open for deterministic mid-stream cancellation.
      response.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8', 'X-Content-Type-Options': 'nosniff' });
      response.flushHeaders(); response.write(bytes.subarray(0, 16 * 1024));
      return;
    }
    if (route === '/truncated') {
      response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'X-Content-Type-Options': 'nosniff', 'Content-Length': bytes.length + 1 });
      response.flushHeaders(); response.write(bytes.subarray(0, 64));
      return setTimeout(() => response.destroy(), 40);
    }
    if (route === '/large') {
      response.writeHead(200, { 'Content-Length': largeTotal, 'Content-Type': 'application/octet-stream',
        'Content-Disposition': 'attachment; filename="fixture-installer.bin"', 'X-Content-Type-Options': 'nosniff' });
      const chunk = Buffer.alloc(64 * 1024, 23);
      const write = () => {
        while (largeSent < largeTotal && !response.destroyed) {
          largeSent += chunk.length;
          if (!response.write(chunk)) { response.once('drain', write); return; }
        }
        if (largeSent === largeTotal) { largeComplete = true; response.end(); }
      };
      return write();
    }
    if (route === '/installer') {
      response.writeHead(200, { 'Content-Length': bytes.length, 'Content-Type': 'application/octet-stream',
        'Content-Disposition': 'attachment; filename="fixture-installer.bin"', 'X-Content-Type-Options': 'nosniff' });
    }
    return response.end(bytes);
  });
  server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const local = `http://127.0.0.1:${server.address().port}`;
  await isolated.cookies.set({ url: local, name: 'fixture_cookie', value: 'synthetic_cookie' });
  const fetchLocal = (route, signal) => transport(local + route, { redirect: 'manual', signal });
  let checks = 0;
  let activeCheck = 'manual HTTP 302 response';
  const check = async (description, work) => {
    activeCheck = description;
    console.log(`UPDATER_NETWORK_CASE: ${description}`);
    let timer;
    const unexpectedError = new Promise((_, reject) => { rejectUnexpected = reject; });
    try {
      await Promise.race([work(), unexpectedError, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('fixture operation timed out')), 8000);
      })]);
      checks++;
    } finally { clearTimeout(timer); rejectUnexpected = undefined; }
  };
  try {
    await check('manual HTTP 302 response', async () => {
      let response;
      try { response = await fetchLocal('/redirect'); }
      catch (error) {
        assert.fail(`manual HTTP 302 failed: ${error.message}`);
      }
      assert.equal(response.status, 302);
      assert.equal(new URL(response.headers.get('location'), local).href, local + '/target');
      await response.body?.cancel();
      await delay(30);
      assert.equal(hits.get('/target') ?? 0, 0, 'adapter followed a redirect before validation');
    });
    await check('HTTP status and body', async () => {
      const response = await fetchLocal('/http-error');
      assert.equal(response.status, 404); assert.equal(response.ok, false);
      assert.equal(await response.text(), 'not found');
      assert.equal((await fetchLocal('/no-content')).body, null);
      assert.equal(await (await fetchLocal('/cacheable')).text(), '1');
      assert.equal(await (await fetchLocal('/cacheable')).text(), '2');
    });
    await check('pre-aborted signal', async () => {
      const controller = new AbortController(); controller.abort();
      await assert.rejects(fetchLocal('/never-requested', controller.signal), error => error.name === 'AbortError');
      assert.equal(hits.get('/never-requested') ?? 0, 0);
    });
    await check('abort before response headers', async () => {
      const controller = new AbortController();
      const pending = fetchLocal('/slow-headers', controller.signal);
      const rejected = assert.rejects(pending, error => error.name === 'AbortError');
      await eventually(() => hits.has('/slow-headers'), 'slow request never started');
      await eventually(() => requestWritesClosed.has('/slow-headers'), 'ClientRequest writable did not close before headers');
      controller.abort(); await rejected;
      await eventually(() => closed.has('/slow-headers'), 'pre-header abort left a connection open');
    });
    await check('abort during response body', async () => {
      const controller = new AbortController();
      const response = await fetchLocal('/hold-body', controller.signal);
      const reader = response.body.getReader();
      const first = (await reader.read()).value;
      assert(first.byteLength > 0 && first.every(byte => byte === 71));
      const rejected = assert.rejects(reader.read(), error => error.name === 'AbortError');
      controller.abort();
      await rejected;
      await eventually(() => closed.has('/hold-body'), 'body abort left a connection open');
    });
    await check('body cancellation', async () => {
      closed.delete('/hold-body');
      const response = await fetchLocal('/hold-body');
      await response.body.cancel();
      await eventually(() => closed.has('/hold-body'), 'body cancellation left a connection open');
    });
    await check('streaming and backpressure', async () => {
      const response = await fetchLocal('/large');
      assert.equal(largeComplete, false, 'adapter buffered the complete body before resolving');
      const reader = response.body.getReader();
      assert((await reader.read()).value.byteLength > 0);
      await delay(150);
      largeAtPause = largeSent;
      assert(largeAtPause < largeTotal, 'a paused consumer buffered the entire installer');
      await reader.cancel();
      await eventually(() => closed.has('/large'), 'stream cancellation left a connection open');
    });
    await check('interrupted response body', async () => {
      const response = await fetchLocal('/truncated');
      await assert.rejects(response.arrayBuffer());
    });

    const mapFetch = (routes, calls = []) => async (url, options) => {
      calls.push({ url, authorized: new Headers(options.headers).has('authorization') });
      assert(routes.has(url), 'transport received an unvalidated fixture URL');
      return transport(local + routes.get(url), options);
    };
    const signal = new AbortController().signal;
    await check('GitHub redirect validation and credential boundaries', async () => {
      process.env.CODEXU_GITHUB_TOKEN = 'fixture-token-not-real';
      const calls = [];
      const routes = new Map([[downloadBase + 'asset', '/trusted'], [cdn + 'asset', '/cdn'], [api, '/release']]);
      const mapped = mapFetch(routes, calls);
      assert.deepEqual(Buffer.from(await (await githubFetch(mapped, downloadBase + 'asset', signal)).arrayBuffer()), bytes);
      assert(calls.every(call => !call.authorized), 'download forwarded API credentials');
      assert.equal(calls.length, 2);
      await (await githubFetch(mapped, api, signal, true)).body.cancel();
      assert.equal(calls.at(-1).authorized, true, 'API request omitted its explicit token');
      const deniedCalls = [];
      await assert.rejects(githubFetch(mapFetch(new Map([[downloadBase + 'denied', '/denied']]), deniedCalls), downloadBase + 'denied', signal), /受信任/);
      assert.equal(deniedCalls.length, 1, 'untrusted redirect reached the transport');
      const apiCalls = [];
      await assert.rejects(githubFetch(mapFetch(new Map([[api, '/api-redirect']]), apiCalls), api, signal, true), /受信任/);
      assert.equal(apiCalls.length, 1, 'API redirect forwarded credentials');
      delete process.env.CODEXU_GITHUB_TOKEN;
    });
    await check('GitHub HTTP errors and invalid redirect cleanup', async () => {
      for (const [route, expected] of [['/http-error', /HTTP 404/], ['/missing-location', /缺少地址/], ['/loop', /次数过多/]]) {
        const url = downloadBase + (route === '/loop' ? 'loop' : 'error');
        await assert.rejects(githubFetch(mapFetch(new Map([[url, route]])), url, signal), expected);
      }
    });
    const updaterRoutes = new Map([[api, '/release'], [downloadBase + name.replace('.exe', '.sha256'), '/checksum-redirect'],
      [cdn + 'checksum', '/checksum'], [downloadBase + name, '/installer-redirect'], [cdn + 'installer', '/installer']]);
    await check('automatic updater through validated CDN redirects', async () => {
      let launches = 0;
      const directory = path.join(root, 'updates');
      const updater = new AutomaticUpdater({ supported: true, directory, installDirectory: root,
        fetch: mapFetch(updaterRoutes), check: async () => ({ isUpdateAvailable: true, latestVersion: version, isPrerelease: false }),
        changed: () => {}, launch: async () => { launches++; } });
      try {
        updater.configure({ checkForUpdates: false, autoInstallUpdates: false });
        await updater.check(true); await updater.download();
        assert.equal(updater.getState().phase, 'ready', 'verified updater did not reach ready');
        assert.deepEqual(await readFile(path.join(directory, name)), bytes);
        assert.equal(await updater.installOnQuit(), false);
        assert.equal(launches, 0);
      } finally { updater.stop(); }
    });
    await check('interrupted updater removes partial installer', async () => {
      const directory = path.join(root, 'interrupted');
      const routes = new Map(updaterRoutes); routes.set(cdn + 'installer', '/truncated');
      const updater = new AutomaticUpdater({ supported: true, directory, installDirectory: root,
        fetch: mapFetch(routes), check: async () => ({ isUpdateAvailable: true, latestVersion: version, isPrerelease: false }),
        changed: () => {}, launch: async () => assert.fail('interrupted installer was launched') });
      try {
        updater.configure({ checkForUpdates: false, autoInstallUpdates: false });
        await updater.check(true); await updater.download();
        assert.equal(updater.getState().phase, 'error');
        assert.deepEqual(await readdir(directory), []);
      } finally { updater.stop(); }
    });
    await delay(50);
    assert.equal(cookieReceived, false, 'updater forwarded session cookies');
    assert.equal(unhandled.length, 0, 'network cleanup emitted unhandled errors');
    console.log(`UPDATER_NETWORK_OK: ${checks} real Electron network checks; paused transfer ${largeAtPause}/${largeTotal} bytes; redirects, streaming, cancellation, security, updater staging`);
  } catch (error) {
    console.error(`UPDATER_NETWORK_FAIL: ${activeCheck}; ${safeError(error)}`);
    process.exitCode = 1;
  } finally {
    delete process.env.CODEXU_GITHUB_TOKEN;
    for (const socket of sockets) socket.destroy();
    await new Promise(resolve => server.close(resolve));
    app.exit(process.exitCode ?? 0);
  }
})().catch(() => { console.error('UPDATER_NETWORK_FAIL: harness initialization'); app.exit(1); });
