import { existsSync, mkdirSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  app,
  BrowserWindow,
  dialog,
  globalShortcut,
  ipcMain,
  Menu,
  net,
  nativeImage,
  nativeTheme,
  protocol,
  session,
  shell,
  Tray,
  type IpcMainInvokeEvent,
  type MenuItemConstructorOptions,
  type Rectangle,
} from 'electron';
import { createHostRequestHandler } from './hostRequests';
import {
  DEFAULT_HOST_SETTINGS,
  nativeActivationAction,
  parseHostSettings,
  shouldApplyStartupRegistration,
  shouldHideWindowOnClose,
  shouldSuppressHostEventInSmoke,
  windowLayout,
  type HostSettings,
} from './hostSettings';
import { decideQuitRequest } from './lifecycle';
import {
  resetMaintenanceShutdownMarker,
  resolveMaintenanceShutdownMarker,
  waitForMaintenanceShutdown,
  writeMaintenanceShutdownFailureMarker,
  writeMaintenanceShutdownMarker,
} from './maintenance';
import {
  EVENT_CHANNEL,
  REQUEST_CHANNEL,
  assertRequiredSidecarCapabilities,
  type SidecarEvent,
} from './protocol';
import {
  isAllowedEventMethod,
  isAllowedMethod,
  isTrustedRendererUrl,
  validateRendererPayload,
} from './security';
import { SidecarClient, type SidecarExit } from './sidecar/SidecarClient';

const APP_URL = 'app://codexu/index.html';
const APP_SCHEME = 'app';
const SMOKE_SUCCESS = 'CODEXU_ELECTRON_SMOKE_OK';
const SMOKE_FAILURE = 'CODEXU_ELECTRON_SMOKE_FAILED';
const SMOKE_READY_TIMEOUT_MS = 60_000;
const WINDOWS_LOGIN_ITEM_NAME = 'codexU';

protocol.registerSchemesAsPrivileged([
  {
    scheme: APP_SCHEME,
    privileges: {
      standard: true,
      secure: true,
      supportFetchAPI: true,
      codeCache: true,
    },
  },
]);
app.enableSandbox();

const smokeTest = process.argv.includes('--smoke-test');
if (smokeTest) app.disableHardwareAcceleration();
let mainWindow: BrowserWindow | undefined;
let sidecar: SidecarClient | undefined;
let tray: Tray | undefined;
let hostSettings: HostSettings = { ...DEFAULT_HOST_SETTINGS };
let registeredGlobalHotKey: string | undefined;
let isGlobalHotKeyRegistered = false;
let appliedCompactMode: boolean | undefined;
let expandedWindowBounds: Rectangle | undefined;
let sidecarHandshakeVersion = 'unknown';
let allowQuit = false;
let shutdownStarted = false;
let desiredExitCode = 0;
let pendingMaintenanceShutdownMarker: string | undefined;
let resolveRendererReady!: () => void;
const rendererReady = new Promise<void>((resolve) => {
  resolveRendererReady = resolve;
});

startElectronHost();

function startElectronHost(): void {
  let maintenanceShutdownMarker: string | undefined;
  try {
    maintenanceShutdownMarker = resolveMaintenanceShutdownMarker(process.argv, tmpdir());
    configureElectronStorageOverride();
    if (maintenanceShutdownMarker) resetMaintenanceShutdownMarker(maintenanceShutdownMarker);
  } catch (reason) {
    console.error('[startup] invalid maintenance or storage configuration:', errorMessage(reason));
    app.exit(2);
    return;
  }

  const hasSingleInstanceLock = app.requestSingleInstanceLock();
  if (!hasSingleInstanceLock) {
    if (maintenanceShutdownMarker) {
      void waitForMaintenanceShutdown(maintenanceShutdownMarker).then(
        () => app.exit(0),
        (reason) => {
          console.error('[shutdown] maintenance handshake failed:', errorMessage(reason));
          app.exit(1);
        },
      );
      return;
    }

    if (smokeTest) console.error(`${SMOKE_FAILURE}: another instance is already running`);
    app.exit(smokeTest ? 1 : 0);
    return;
  }

  if (maintenanceShutdownMarker) {
    app.releaseSingleInstanceLock();
    app.exit(0);
    return;
  }

  registerLifecycleHandlers();
  void app.whenReady().then(bootstrap).catch(failAndQuit);
}

function configureElectronStorageOverride(): void {
  const configuredRoot = process.env.CODEXU_ELECTRON_USER_DATA_DIRECTORY?.trim();
  if (!configuredRoot) return;

  const userDataRoot = path.resolve(configuredRoot);
  const sessionDataRoot = path.join(userDataRoot, 'session');
  mkdirSync(sessionDataRoot, { recursive: true });
  app.setPath('userData', userDataRoot);
  app.setPath('sessionData', sessionDataRoot);
}

async function bootstrap(): Promise<void> {
  const rendererRoot = resolveRendererRoot();
  assertRendererExists(rendererRoot);
  registerAppProtocol(rendererRoot);
  configureSessionSecurity();

  const sidecarPath = resolveSidecarPath();
  if (!existsSync(sidecarPath) || !statSync(sidecarPath).isFile()) {
    throw new Error(`Sidecar executable was not found: ${sidecarPath}`);
  }

  sidecar = new SidecarClient({
    executablePath: sidecarPath,
    arguments: resolveSidecarArguments(),
    cwd: path.dirname(sidecarPath),
    environment: {
      ...process.env,
      CODEXU_PARENT_PID: String(process.pid),
    },
    hostRequestHandler: createHostRequestHandler(dialog, () => mainWindow, {
      // Smoke tests exercise the full reverse-RPC path without ever presenting
      // native UI or making a destructive confirmation choice.
      forceSafeCancellation: smokeTest,
    }),
  });
  sidecar.on('stderr', (message: string) => logSidecarStderr(message));
  sidecar.on('event', (event: SidecarEvent) => handleSidecarEvent(event));
  sidecar.on('exit', (exit: SidecarExit) => handleSidecarExit(exit));
  sidecar.on('protocolError', (error: Error) => {
    console.error('[sidecar protocol]', error.message);
    requestQuit(1);
  });

  const handshake = await sidecar.start();
  sidecarHandshakeVersion = handshake.backendVersion;
  assertRequiredSidecarCapabilities(handshake.capabilities);

  hostSettings = parseHostSettings(await sidecar.request('settings.get', {}));
  await applyHostSettings(hostSettings);
  if (smokeTest && (tray !== undefined || registeredGlobalHotKey !== undefined
      || isGlobalHotKeyRegistered)) {
    throw new Error('Smoke mode unexpectedly initialized native tray or shortcut state.');
  }
  registerRendererIpc();
  mainWindow = await createMainWindow(hostSettings);

  if (smokeTest) {
    await withTimeout(
      rendererReady,
      SMOKE_READY_TIMEOUT_MS,
      'Vue did not signal app.ready before the smoke-test deadline.',
    );
    await sidecar.waitForIdle(SMOKE_READY_TIMEOUT_MS);
    const bridgeRoundTripSucceeded = await mainWindow.webContents.executeJavaScript(`
      (async () => {
        const bridge = globalThis.codexU;
        if (!bridge || typeof bridge.request !== 'function' || typeof bridge.onEvent !== 'function') {
          throw new Error('window.codexU was not injected by the sandboxed preload.');
        }

        const initialized = await bridge.request('app.initialize', {});
        if (typeof initialized !== 'object' || initialized === null
            || !Array.isArray(initialized.capabilities)
            || !initialized.capabilities.includes('nativeDialogs')) {
          throw new Error('app.initialize does not advertise nativeDialogs.');
        }

        const exportResult = await bridge.request('rates.export', {});
        if (typeof exportResult !== 'object' || exportResult === null
            || exportResult.success !== false) {
          throw new Error('rates.export was not safely cancelled by the smoke host.');
        }
        if ('path' in exportResult && exportResult.path != null) {
          throw new Error('Cancelled rates.export unexpectedly returned an output path.');
        }
        return true;
      })()
    `, true);
    if (bridgeRoundTripSucceeded !== true) {
      throw new Error('Renderer bridge smoke round-trip returned an unexpected result.');
    }
    await sidecar.waitForIdle(SMOKE_READY_TIMEOUT_MS);
    console.log(
      `${SMOKE_SUCCESS}: app-loaded backend=${sidecarHandshakeVersion}`
      + ` host-state=${String(isGlobalHotKeyRegistered)}`
      + ' reverse-rpc=rates.export-cancelled',
    );
    requestQuit(0);
  }
}

function registerLifecycleHandlers(): void {
  app.on('second-instance', (_event, commandLine) => {
    try {
      const marker = resolveMaintenanceShutdownMarker(commandLine, tmpdir());
      if (marker) {
        pendingMaintenanceShutdownMarker = marker;
        requestQuit(0);
        return;
      }
    } catch (reason) {
      console.error('[shutdown] rejected maintenance request:', errorMessage(reason));
      return;
    }

    const action = nativeActivationAction(smokeTest, 'second-instance');
    if (action === 'fail') {
      failAndQuit(new Error('Smoke test rejected a second-instance activation.'));
    } else if (action === 'show') {
      showAndFocusMainWindow();
    }
  });

  app.on('activate', () => {
    if (nativeActivationAction(smokeTest, 'activate') === 'show') {
      showAndFocusMainWindow();
    }
  });

  app.on('window-all-closed', () => app.quit());

  app.on('before-quit', (event) => {
    if (allowQuit) return;
    event.preventDefault();
    if (shutdownStarted) return;
    shutdownStarted = true;

    const maintenanceMarker = pendingMaintenanceShutdownMarker;
    void shutdownSidecar(maintenanceMarker !== undefined).then(
      () => {
        if (maintenanceMarker) {
          try {
            writeMaintenanceShutdownMarker(maintenanceMarker);
          } catch (reason) {
            console.error('[shutdown] failed to acknowledge maintenance request:', errorMessage(reason));
            desiredExitCode = 1;
          }
        }
        allowQuit = true;
        requestQuit(desiredExitCode);
      },
      (reason) => {
        console.error('[shutdown] maintenance Sidecar shutdown failed:', errorMessage(reason));
        desiredExitCode = 1;
        if (maintenanceMarker) {
          try {
            writeMaintenanceShutdownFailureMarker(maintenanceMarker);
          } catch (markerReason) {
            console.error(
              '[shutdown] failed to report maintenance shutdown failure:',
              errorMessage(markerReason),
            );
          }
        }
        allowQuit = true;
        requestQuit(desiredExitCode);
      },
    );
  });

  app.on('will-quit', () => disposeNativeShell());
  nativeTheme.on('updated', () => updateWindowBackground());

  process.once('SIGINT', () => requestQuit(0));
  process.once('SIGTERM', () => requestQuit(0));
}

function registerAppProtocol(rendererRoot: string): void {
  protocol.handle(APP_SCHEME, async (request) => {
    const assetPath = resolveRendererAsset(rendererRoot, request.url);
    if (!assetPath) return new Response('Not found', { status: 404 });

    try {
      return await net.fetch(pathToFileURL(assetPath).toString());
    } catch {
      return new Response('Not found', { status: 404 });
    }
  });
}

function configureSessionSecurity(): void {
  session.defaultSession.setPermissionCheckHandler(() => false);
  session.defaultSession.setPermissionRequestHandler(
    (_webContents, _permission, callback) => callback(false),
  );
}

function registerRendererIpc(): void {
  ipcMain.removeHandler(REQUEST_CHANNEL);
  ipcMain.handle(
    REQUEST_CHANNEL,
    async (event: IpcMainInvokeEvent, method: unknown, payload: unknown): Promise<unknown> => {
      if (!isTrustedSender(event)) {
        throw new Error('IPC request rejected: untrusted renderer.');
      }
      if (!isAllowedMethod(method)) {
        throw new Error(`IPC request rejected: method is not allowed (${String(method)}).`);
      }
      validateRendererPayload(method, payload);
      if (!sidecar) throw new Error('Sidecar is unavailable.');
      return sidecar.request(method, payload, requestTimeoutForMethod(method));
    },
  );
}

async function createMainWindow(settings: HostSettings): Promise<BrowserWindow> {
  const layout = windowLayout(settings.compactMode);
  const window = new BrowserWindow({
    width: layout.width,
    height: layout.height,
    minWidth: layout.minimumWidth,
    minHeight: layout.minimumHeight,
    show: false,
    autoHideMenuBar: true,
    backgroundColor: windowBackgroundColor(),
    icon: resolveAppIconPath(),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webviewTag: false,
    },
  });
  // Publish the trusted WebContents before navigation. Vue can invoke the host
  // bridge during its initial load, before loadURL's promise resolves.
  mainWindow = window;
  appliedCompactMode = settings.compactMode;

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => {
    if (!isTrustedRendererUrl(url)) event.preventDefault();
  });
  window.webContents.on('render-process-gone', (_event, details) => {
    console.error(`[renderer] process gone: ${details.reason}`);
  });
  window.on('close', (event) => {
    const trayAvailable = Boolean(tray && !tray.isDestroyed());
    if (shouldHideWindowOnClose(hostSettings, shutdownStarted || allowQuit, trayAvailable)) {
      event.preventDefault();
      window.hide();
    }
  });
  window.once('closed', () => {
    if (mainWindow === window) mainWindow = undefined;
    appliedCompactMode = undefined;
    expandedWindowBounds = undefined;
  });
  window.once('ready-to-show', () => {
    if (!smokeTest) window.show();
  });

  try {
    await window.loadURL(APP_URL);
    return window;
  } catch (reason) {
    if (mainWindow === window) mainWindow = undefined;
    if (!window.isDestroyed()) window.destroy();
    throw reason;
  }
}

function isTrustedSender(event: IpcMainInvokeEvent): boolean {
  const frame = event.senderFrame;
  return Boolean(
    mainWindow
    && !mainWindow.isDestroyed()
    && event.sender === mainWindow.webContents
    && frame
    && frame.top === frame
    && isTrustedRendererUrl(frame.url),
  );
}

function handleSidecarEvent(event: SidecarEvent): void {
  if (shouldSuppressHostEventInSmoke(smokeTest, event.method)) {
    failAndQuit(new Error(`Smoke test rejected native host event: ${event.method}`));
    return;
  }

  switch (event.method) {
    case 'settings.changed':
      void applyChangedSettings(event);
      return;
    case 'host.window.setAlwaysOnTop':
      if (isRecordWithBoolean(event.payload, 'enabled')) {
        mainWindow?.setAlwaysOnTop(event.payload.enabled);
      }
      return;
    case 'host.window.show':
      showAndFocusMainWindow();
      return;
    case 'host.window.activate':
      showAndFocusMainWindow();
      return;
    case 'host.window.hide':
      mainWindow?.hide();
      return;
    case 'host.openExternal':
      if (isSafeExternalUrl(event.payload)) {
        void shell.openExternal(event.payload.url).catch((reason) => {
          console.error('[shell] failed to open external URL:', errorMessage(reason));
        });
      }
      return;
    case 'host.startupRegistrationRequested':
      applyStartupRegistrationRequest(event.payload);
      return;
    case 'host.webReady':
      resolveRendererReady();
      return;
    case 'sidecar.protocolError':
      console.error('[sidecar] backend rejected a protocol message.');
      return;
    default:
      forwardRendererEvent(event);
  }
}

function forwardRendererEvent(event: SidecarEvent): void {
  if (!isAllowedEventMethod(event.method)) return;
  if (!mainWindow || mainWindow.isDestroyed() || mainWindow.webContents.isDestroyed()) return;
  mainWindow.webContents.send(EVENT_CHANNEL, event.method, event.payload);
}

async function applyChangedSettings(event: SidecarEvent): Promise<void> {
  try {
    const settings = parseHostSettings(event.payload);
    await applyHostSettings(settings);
    forwardRendererEvent(event);
  } catch (reason) {
    if (!shutdownStarted) failAndQuit(reason);
  }
}

async function applyHostSettings(settings: HostSettings): Promise<void> {
  hostSettings = settings;
  nativeTheme.themeSource = settings.theme;
  reconcileStartupRegistration(settings.startAtLogin);
  applyCompactWindowMode(settings.compactMode);
  ensureTray();
  rebuildTrayMenu();

  isGlobalHotKeyRegistered = configureGlobalHotKey(settings.globalHotKey);
  if (!sidecar) throw new Error('Sidecar is unavailable while applying host settings.');
  await sidecar.sendHostState(isGlobalHotKeyRegistered);
}

function ensureTray(): void {
  if (smokeTest || (tray && !tray.isDestroyed())) return;

  const trayImage = nativeImage.createFromPath(resolveAppIconPath());
  if (trayImage.isEmpty()) throw new Error('The packaged tray icon could not be loaded.');
  tray = new Tray(trayImage);
  tray.setToolTip('codexU · 本地 AI 用量');
  tray.on('click', () => showAndFocusMainWindow());
}

function rebuildTrayMenu(): void {
  if (smokeTest || !tray || tray.isDestroyed()) return;

  const template: MenuItemConstructorOptions[] = [
    {
      label: '打开 codexU',
      click: () => showAndFocusMainWindow(),
    },
    {
      label: '刷新',
      click: () => runTrayAction(async () => {
        showAndFocusMainWindow();
        if (!sidecar) throw new Error('Sidecar is unavailable.');
        await sidecar.request('usage.refresh', {}, 120_000);
      }),
    },
    {
      label: '紧凑模式',
      type: 'checkbox',
      checked: hostSettings.compactMode,
      click: () => runTrayAction(async () => {
        if (!sidecar) throw new Error('Sidecar is unavailable.');
        await sidecar.request('window.toggleCompact', {});
      }),
    },
    { type: 'separator' },
    {
      label: '退出',
      click: () => requestQuit(0),
    },
  ];
  tray.setContextMenu(Menu.buildFromTemplate(template));
}

function runTrayAction(action: () => Promise<void>): void {
  void action().catch((reason) => {
    console.error('[tray] action failed:', errorMessage(reason));
    rebuildTrayMenu();
  });
}

function configureGlobalHotKey(accelerator: HostSettings['globalHotKey']): boolean {
  if (smokeTest) return false;
  if (registeredGlobalHotKey === accelerator && globalShortcut.isRegistered(accelerator)) {
    return true;
  }

  if (registeredGlobalHotKey) {
    globalShortcut.unregister(registeredGlobalHotKey);
    registeredGlobalHotKey = undefined;
  }
  isGlobalHotKeyRegistered = false;

  try {
    if (!globalShortcut.register(accelerator, () => showAndFocusMainWindow())) {
      console.error(`[shortcut] registration failed: ${accelerator}`);
      return false;
    }
    registeredGlobalHotKey = accelerator;
    return true;
  } catch (reason) {
    console.error(`[shortcut] registration failed: ${errorMessage(reason)}`);
    return false;
  }
}

function showAndFocusMainWindow(): void {
  const window = mainWindow;
  if (!window || window.isDestroyed()) return;
  if (window.isMinimized()) window.restore();
  window.show();
  window.focus();
}

function applyCompactWindowMode(compactMode: boolean): void {
  const window = mainWindow;
  if (!window || window.isDestroyed()) return;
  const layout = windowLayout(compactMode);

  if (appliedCompactMode === compactMode) {
    window.setMinimumSize(layout.minimumWidth, layout.minimumHeight);
    updateWindowBackground();
    return;
  }

  if (window.isMaximized()) window.unmaximize();
  if (compactMode) {
    if (appliedCompactMode === false && !window.isFullScreen()) {
      expandedWindowBounds = window.getBounds();
    }
    window.setMinimumSize(layout.minimumWidth, layout.minimumHeight);
    window.setSize(layout.width, layout.height);
  } else {
    if (expandedWindowBounds) {
      window.setBounds(expandedWindowBounds);
      expandedWindowBounds = undefined;
    } else {
      window.setSize(layout.width, layout.height);
    }
    window.setMinimumSize(layout.minimumWidth, layout.minimumHeight);
  }
  appliedCompactMode = compactMode;
  updateWindowBackground();
}

function updateWindowBackground(): void {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.setBackgroundColor(windowBackgroundColor());
}

function windowBackgroundColor(): string {
  return nativeTheme.shouldUseDarkColors ? '#111318' : '#edf3ff';
}

function reconcileStartupRegistration(enabled: boolean): void {
  if (!shouldApplyStartupRegistration(process.platform, app.isPackaged, smokeTest)) return;

  try {
    const loginItemIdentity = {
      path: process.execPath,
      args: [] as string[],
    };
    app.setLoginItemSettings({
      openAtLogin: enabled,
      enabled,
      name: WINDOWS_LOGIN_ITEM_NAME,
      ...loginItemIdentity,
    });
  } catch (reason) {
    console.error('[startup] reconciliation failed:', errorMessage(reason));
  }
}

function disposeNativeShell(): void {
  if (registeredGlobalHotKey) {
    globalShortcut.unregister(registeredGlobalHotKey);
    registeredGlobalHotKey = undefined;
  }
  isGlobalHotKeyRegistered = false;
  if (tray && !tray.isDestroyed()) tray.destroy();
  tray = undefined;
}

function handleSidecarExit(exit: SidecarExit): void {
  if (exit.expected) return;
  console.error(
    `[sidecar] unexpected exit code=${String(exit.code)} signal=${String(exit.signal)}`,
  );
  requestQuit(1);
}

function logSidecarStderr(message: string): void {
  for (const line of message.split(/\r?\n/u)) {
    if (line.length > 0) console.error(`[sidecar] ${line}`);
  }
}

function requestTimeoutForMethod(method: string): number {
  if (method === 'usage.getCombined') return 300_000;
  if (method.startsWith('usage.') || method === 'runtime.select') return 120_000;
  return 30_000;
}

function resolveRendererRoot(): string {
  return app.isPackaged
    ? path.join(process.resourcesPath, 'dist')
    : path.resolve(__dirname, '..', '..', 'CodexU.Web', 'dist');
}

function assertRendererExists(rendererRoot: string): void {
  const indexPath = path.join(rendererRoot, 'index.html');
  if (!existsSync(indexPath) || !statSync(indexPath).isFile()) {
    throw new Error(`Vue renderer assets were not found: ${indexPath}`);
  }
}

function resolveRendererAsset(rendererRoot: string, requestUrl: string): string | undefined {
  let url: URL;
  try {
    url = new URL(requestUrl);
  } catch {
    return undefined;
  }
  if (!isTrustedRendererUrl(requestUrl)) return undefined;

  let relativePath: string;
  try {
    relativePath = decodeURIComponent(url.pathname).replace(/^\/+/, '');
  } catch {
    return undefined;
  }
  if (relativePath.includes('\0')) return undefined;
  if (relativePath.length === 0) relativePath = 'index.html';

  const root = path.resolve(rendererRoot);
  const candidate = path.resolve(root, relativePath);
  const relative = path.relative(root, candidate);
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    return undefined;
  }
  if (!existsSync(candidate) || !statSync(candidate).isFile()) return undefined;
  return candidate;
}

function resolveSidecarPath(): string {
  const executableName = process.platform === 'win32' ? 'CodexU.Sidecar.exe' : 'CodexU.Sidecar';
  if (app.isPackaged) return path.join(process.resourcesPath, 'backend', executableName);

  const commandLinePath = process.argv
    .find((argument) => argument.startsWith('--sidecar-path='))
    ?.slice('--sidecar-path='.length)
    .trim();
  const configuredPath = commandLinePath || process.env.CODEXU_SIDECAR_PATH?.trim();
  if (configuredPath) return path.resolve(configuredPath);
  return path.resolve(__dirname, '..', 'backend', executableName);
}

function resolveSidecarArguments(): string[] {
  const arguments_ = ['--app-version', app.getVersion()];
  if (app.isPackaged) arguments_.push('--packaged');
  return arguments_;
}

function isRecordWithBoolean(
  value: unknown,
  property: string,
): value is Record<string, unknown> & Record<typeof property, boolean> {
  return typeof value === 'object'
    && value !== null
    && !Array.isArray(value)
    && typeof (value as Record<string, unknown>)[property] === 'boolean';
}

function isSafeExternalUrl(value: unknown): value is { url: string } {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const candidate = (value as Record<string, unknown>).url;
  if (typeof candidate !== 'string') return false;

  try {
    const url = new URL(candidate);
    return url.protocol === 'https:'
      && url.username === ''
      && url.password === '';
  } catch {
    return false;
  }
}

function applyStartupRegistrationRequest(payload: unknown): void {
  if (!isRecordWithBoolean(payload, 'enabled')) return;
  reconcileStartupRegistration(payload.enabled);
}

function resolveAppIconPath(): string {
  const fileName = process.platform === 'win32' ? 'AppIcon.ico' : 'AppIcon.png';
  const iconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'Assets', fileName)
    : path.resolve(__dirname, '..', '..', 'CodexU.App', 'Assets', fileName);
  if (!existsSync(iconPath) || !statSync(iconPath).isFile()) {
    throw new Error(`Application icon was not found: ${iconPath}`);
  }
  return iconPath;
}

async function shutdownSidecar(failOnError = false): Promise<void> {
  if (!sidecar) return;
  try {
    await sidecar.shutdown();
  } catch (reason) {
    console.error('[sidecar] shutdown failed:', errorMessage(reason));
    if (failOnError) throw reason;
  }
}

function requestQuit(exitCode: number): void {
  const decision = decideQuitRequest(desiredExitCode, exitCode, allowQuit);
  desiredExitCode = decision.exitCode;
  if (decision.action === 'exit') {
    disposeNativeShell();
    app.exit(decision.exitCode);
    return;
  }
  app.quit();
}

function failAndQuit(reason: unknown): void {
  const message = errorMessage(reason);
  console.error(smokeTest ? `${SMOKE_FAILURE}: ${message}` : `[fatal] ${message}`);
  requestQuit(1);
}

function errorMessage(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason);
}

function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(message)), timeoutMs);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (reason) => {
        clearTimeout(timer);
        reject(reason);
      },
    );
  });
}
