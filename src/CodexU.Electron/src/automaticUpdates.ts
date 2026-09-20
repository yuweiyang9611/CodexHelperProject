import { createHash } from 'node:crypto';
import { createReadStream, existsSync } from 'node:fs';
import { mkdir, open, readdir, rename, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';

const REPOSITORY = 'yuweiyang9611/CodexHelperProject';
const API = `https://api.github.com/repos/${REPOSITORY}/releases/tags/`;
const DOWNLOAD = `https://github.com/${REPOSITORY}/releases/download/`;
const MAX_INSTALLER_BYTES = 512 * 1024 * 1024;
const VERSION = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/;

export interface UpdateState {
  supported: boolean;
  phase: 'idle' | 'downloading' | 'ready' | 'installing' | 'error';
  message: string;
  version?: string;
  progress?: number;
}

interface ReleaseCheck { isUpdateAvailable: boolean; latestVersion?: string; isPrerelease: boolean }
interface Candidate { version: string; prerelease: boolean }
interface Package { file: string; hash: string; size: number; version: string }
interface Settings { check: boolean; automatic: boolean; prerelease: boolean }
interface Options {
  supported: boolean;
  directory: string;
  installDirectory: string;
  fetch: typeof fetch;
  check: (force: boolean) => Promise<unknown>;
  changed: (state: UpdateState) => void;
  launch?: (file: string, args: string[]) => Promise<void>;
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function supportsAutomaticUpdates(platform: string, packaged: boolean, smoke: boolean, executable: string): boolean {
  return platform === 'win32' && packaged && !smoke
    && existsSync(path.join(path.dirname(executable), 'unins000.exe'));
}

// Redirects may only leave the exact release asset for GitHub's asset CDN. Never
// forward an API token to a download URL, including its redirect targets.
export async function githubFetch(fetcher: typeof fetch, url: string, signal: AbortSignal, api = false): Promise<Response> {
  let current = url;
  for (let redirects = 0; redirects <= 5; redirects++) {
    const target = new URL(current);
    const allowed = target.protocol === 'https:' && !target.username && !target.password && !target.port
      && (api ? current.startsWith(API) && redirects === 0
        : current.startsWith(DOWNLOAD) || ['release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(target.hostname));
    if (!allowed) throw new Error('更新地址不属于受信任的 GitHub 发布源。');
    const headers: Record<string, string> = { 'User-Agent': 'codexU-updater' };
    if (api) {
      headers.Accept = 'application/vnd.github+json';
      headers['X-GitHub-Api-Version'] = '2022-11-28';
      const token = process.env.CODEXU_GITHUB_TOKEN?.trim();
      if (token) headers.Authorization = `Bearer ${token}`;
    }
    const response = await fetcher(current, { headers, redirect: 'manual', signal });
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const location = response.headers.get('location');
      await response.body?.cancel();
      if (!location) throw new Error('GitHub 下载重定向缺少地址。');
      current = new URL(location, current).href;
      continue;
    }
    if (!response.ok) {
      await response.body?.cancel();
      throw new Error(`GitHub 更新请求失败（HTTP ${response.status}），请稍后重试。`);
    }
    return response;
  }
  throw new Error('GitHub 下载重定向次数过多。');
}

async function limitedText(response: Response, limit: number): Promise<string> {
  if (!response.body) throw new Error('更新响应为空。');
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > limit) throw new Error('更新响应超出大小限制。');
      chunks.push(value);
    }
    return Buffer.concat(chunks).toString('utf8');
  } finally { await reader.cancel(); }
}

export function selectPackage(release: unknown, candidate: Candidate): { name: string; url: string; checksum: string; size: number; digest?: string } {
  if (!VERSION.test(candidate.version) || !record(release) || release.draft !== false
    || release.tag_name !== `v${candidate.version}` || release.prerelease !== candidate.prerelease || !Array.isArray(release.assets)) {
    throw new Error('GitHub 发布信息不匹配，请重新检查更新。');
  }
  const name = `CodexU-${candidate.version}-win-x64-setup.exe`;
  const checksumName = name.replace(/\.exe$/, '.sha256');
  const find = (assetName: string) => {
    const matches = (release.assets as unknown[]).filter(asset => record(asset) && asset.name === assetName);
    if (matches.length !== 1 || !record(matches[0])) throw new Error('此版本缺少唯一的 Windows 安装包或 SHA-256 校验文件。');
    const asset = matches[0];
    const expected = `${DOWNLOAD}v${candidate.version}/${assetName}`;
    if (asset.browser_download_url !== expected || asset.state !== 'uploaded') throw new Error('发布附件尚未就绪或地址不匹配。');
    return asset;
  };
  const installer = find(name);
  find(checksumName);
  if (typeof installer.size !== 'number' || !Number.isSafeInteger(installer.size) || installer.size <= 0 || installer.size > MAX_INSTALLER_BYTES) {
    throw new Error('安装包大小无效。');
  }
  const digest = installer.digest;
  if (digest != null && (typeof digest !== 'string' || !/^sha256:[a-f0-9]{64}$/i.test(digest))) throw new Error('发布附件摘要格式无效。');
  return { name, url: installer.browser_download_url as string, checksum: `${DOWNLOAD}v${candidate.version}/${checksumName}`,
    size: installer.size, digest: typeof digest === 'string' ? digest.slice(7).toLowerCase() : undefined };
}

async function hashFile(file: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

export function installerArguments(installDirectory: string, restart: boolean): string[] {
  return ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NORESTARTAPPLICATIONS',
    '/NOCLOSEAPPLICATIONS', '/NOFORCECLOSEAPPLICATIONS', '/CODEXUUPDATE=1',
    ...(restart ? ['/CODEXURESTART=1'] : []), `/DIR=${installDirectory}`, '/LOG'];
}

async function launchInstaller(file: string, args: string[]): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn(file, args, { detached: true, stdio: 'ignore', windowsHide: true, shell: false });
    child.once('error', reject);
    child.once('spawn', () => { child.unref(); resolve(); });
  });
}

export class AutomaticUpdater {
  private settings: Settings = { check: false, automatic: true, prerelease: false };
  private candidate?: Candidate;
  private ready?: Package;
  private controller?: AbortController;
  private downloading?: Promise<void>;
  private checking?: Promise<unknown>;
  private timer?: NodeJS.Timeout;
  private stopped = false;
  private restart = false;
  private state: UpdateState;

  constructor(private readonly options: Options) {
    this.state = { supported: options.supported, phase: 'idle', message: options.supported
      ? '发现更新后自动下载，正常退出时安装。'
      : '自动安装仅适用于 Windows 安装版；便携版请下载新版本后手动替换。' };
  }

  getState(): UpdateState { return { ...this.state }; }

  configure(value: unknown): void {
    if (!record(value) || this.stopped) return;
    const next = { check: value.checkForUpdates === true, automatic: value.autoInstallUpdates !== false,
      prerelease: value.includePrereleaseUpdates === true };
    const changed = JSON.stringify(next) !== JSON.stringify(this.settings);
    if (!changed) return;
    this.settings = next;
    this.controller?.abort();
    this.ready = undefined;
    this.candidate = undefined;
    this.restart = false;
    if (this.options.supported) this.publish({ phase: 'idle', version: undefined, progress: undefined,
      message: next.automatic ? '发现更新后自动下载，正常退出时安装。' : '自动安装已关闭，可手动下载更新。' });
    if (this.timer) clearInterval(this.timer);
    if (next.check && this.options.supported) {
      // The shared update service caches successful checks for one day. Polling
      // also retries transient failures without depending on an open renderer.
      this.timer = setInterval(() => { void this.check(false).catch(() => {}); }, 60 * 60 * 1000);
      this.timer.unref();
      void Promise.allSettled([this.checking, this.downloading]).then(() => {
        if (!this.stopped && this.settings === next) void this.check(false).catch(() => {});
      });
    }
  }

  check(force: boolean): Promise<unknown> {
    if (this.checking) return this.checking;
    const settings = this.settings;
    this.checking = this.options.check(force).then(result => {
      if (settings === this.settings && !this.stopped) this.accept(result);
      return result;
    }).finally(() => { this.checking = undefined; });
    return this.checking;
  }

  private accept(value: unknown): void {
    if (!record(value)) return;
    const result = value as unknown as ReleaseCheck;
    if (result.isUpdateAvailable === false && result.latestVersion) {
      this.controller?.abort();
      this.candidate = undefined;
      this.ready = undefined;
      if (this.options.supported) this.publish({ phase: 'idle', version: undefined, progress: undefined, message: '当前没有可安装的新版本。' });
      return;
    }
    if (!result.isUpdateAvailable || !result.latestVersion || !VERSION.test(result.latestVersion)
      || (result.isPrerelease && !this.settings.prerelease)) return;
    if (this.candidate && this.candidate.version !== result.latestVersion) {
      this.controller?.abort();
      this.ready = undefined;
    }
    const candidate = { version: result.latestVersion, prerelease: result.isPrerelease };
    this.candidate = candidate;
    if (this.options.supported && this.settings.automatic) {
      void Promise.resolve(this.downloading).then(() => {
        if (!this.stopped && this.candidate === candidate && this.settings.automatic) void this.download();
      });
    }
  }

  download(): Promise<void> {
    if (this.downloading) return this.downloading;
    if (this.stopped || !this.options.supported) return Promise.resolve();
    this.downloading = this.downloadCandidate().finally(() => { this.downloading = undefined; });
    return this.downloading;
  }

  private async downloadCandidate(): Promise<void> {
    const candidate = this.candidate;
    if (!candidate || this.stopped) return;
    if (this.ready?.version === candidate.version) return;
    const controller = new AbortController();
    this.controller = controller;
    const timeout = setTimeout(() => controller.abort(new Error('下载超时，请重试。')), 20 * 60 * 1000);
    timeout.unref();
    const signal = controller.signal;
    let partial: string | undefined;
    this.ready = undefined;
    this.publish({ phase: 'downloading', version: candidate.version, progress: 0, message: '正在下载并校验更新…' });
    try {
      const get = (url: string, api = false) => githubFetch(this.options.fetch, url, signal, api);
      const release = JSON.parse(await limitedText(await get(`${API}v${candidate.version}`, true), 2 * 1024 * 1024));
      const asset = selectPackage(release, candidate);
      const checksum = (await limitedText(await get(asset.checksum), 4096)).trim();
      const match = /^([a-f0-9]{64})\s+\*?([^\r\n]+)$/i.exec(checksum);
      if (!match || match[2] !== asset.name) throw new Error('SHA-256 校验文件与安装包不匹配。');
      const expected = match[1].toLowerCase();
      if (asset.digest && asset.digest !== expected) throw new Error('GitHub 摘要与校验文件不一致。');
      await mkdir(this.options.directory, { recursive: true });
      const file = path.join(this.options.directory, asset.name);
      const cached = await stat(file).catch(() => undefined);
      if (!cached || cached.size !== asset.size || await hashFile(file) !== expected) {
        partial = file + '.part';
        const response = await get(asset.url);
        if (!response.body) throw new Error('安装包响应为空。');
        const handle = await open(partial, 'w');
        const reader = response.body.getReader();
        const hash = createHash('sha256');
        let received = 0;
        let lastProgress = -1;
        try {
          while (true) {
            const { done, value } = await reader.read();
            signal.throwIfAborted();
            if (done) break;
            received += value.byteLength;
            if (received > asset.size) throw new Error('下载内容超过发布的安装包大小。');
            hash.update(value);
            await handle.writeFile(value);
            const progress = Math.floor(received / asset.size * 100);
            if (progress !== lastProgress) { lastProgress = progress; this.publish({ progress }); }
          }
          if (received !== asset.size || hash.digest('hex') !== expected) throw new Error('安装包校验失败，已丢弃下载内容。');
          await handle.sync();
        } finally { await handle.close(); await reader.cancel(); }
        signal.throwIfAborted();
        await rename(partial, file);
        partial = undefined;
      }
      signal.throwIfAborted();
      // Only remove our own obsolete cached assets; never recurse through the
      // cache directory or touch unrelated user files.
      for (const name of await readdir(this.options.directory)) {
        if (name !== asset.name && /^CodexU-\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?-win-x64-setup\.exe(?:\.part)?$/.test(name)) {
          await rm(path.join(this.options.directory, name), { force: true }).catch(() => {});
        }
      }
      signal.throwIfAborted();
      this.ready = { file, hash: expected, size: asset.size, version: candidate.version };
      this.publish({ phase: 'ready', progress: 100, message: this.settings.automatic
        ? '更新已就绪，将在正常退出时安装，也可立即重启更新。' : '更新已就绪，点击“重启并更新”完成安装。' });
    } catch (reason) {
      if (!signal.aborted || signal.reason instanceof Error && signal.reason.name !== 'AbortError') {
        this.publish({ phase: 'error', message: reason instanceof Error ? reason.message : '下载失败，请稍后重试。' });
      }
    } finally {
      clearTimeout(timeout);
      if (partial) await rm(partial, { force: true }).catch(() => {});
      if (this.controller === controller) this.controller = undefined;
    }
  }

  requestRestart(): void {
    if (!this.ready || this.state.phase !== 'ready') throw new Error('更新尚未下载完成。');
    this.restart = true;
  }

  stop(): void {
    this.stopped = true;
    if (this.timer) clearInterval(this.timer);
    this.controller?.abort();
  }

  async installOnQuit(): Promise<boolean> {
    const ready = this.ready;
    if (!ready || (!this.settings.automatic && !this.restart)) return false;
    try {
      if ((await stat(ready.file)).size !== ready.size || await hashFile(ready.file) !== ready.hash) {
        throw new Error('安装前校验失败，请重新下载更新。');
      }
      this.publish({ phase: 'installing', message: '正在退出并安装更新…' });
      await (this.options.launch ?? launchInstaller)(ready.file, installerArguments(this.options.installDirectory, this.restart));
      return true;
    } catch (reason) {
      this.ready = undefined;
      this.publish({ phase: 'error', message: reason instanceof Error ? reason.message : '无法启动更新安装程序。' });
      return false;
    }
  }

  private publish(patch: Partial<UpdateState>): void {
    this.state = { ...this.state, ...patch };
    this.options.changed(this.getState());
  }
}
