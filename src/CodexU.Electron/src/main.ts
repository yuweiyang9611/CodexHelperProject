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
  Notification,
  protocol,
  screen,
  session,
  shell,
  Tray,
  type Display,
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
  shouldEnableNativeDesktopFeatures,
  shouldHideWindowOnClose,
  shouldSuppressHostEventInSmoke,
  windowLayout,
  type HostSettings,
} from './hostSettings';
import { decideQuitRequest } from './lifecycle';
import { requestTimeoutForMethod } from './ipcRequestTimeouts';
import {
  resetMaintenanceShutdownMarker,
  resolveMaintenanceShutdownMarker,
  waitForMaintenanceShutdown,
  writeMaintenanceShutdownFailureMarker,
  writeMaintenanceShutdownMarker,
} from './maintenance';
import { NativeNotificationAdapter } from './nativeNotifications';
import { PersistentLog, TextLineBuffer, type RuntimeLogLevel } from './persistentLog';
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
import { RecoverySupervisor } from './recoverySupervisor';
import {
  CompletionQueue,
  GenerationFence,
  RecoveryPromptQueue,
  SingleFlightOperation,
} from './runtimeCoordination';
import { SidecarClient, type SidecarExit } from './sidecar/SidecarClient';
import {
  createWindowState,
  equalWindowBounds,
  fitWindowBoundsToWorkArea,
  fitWindowSizeToWorkArea,
  loadWindowState,
  restoreWindowState,
  saveWindowState,
  type WindowDisplay,
} from './windowState';
import {
  applyWindowsStartupRegistration,
  configureWindowsDesktopIdentity,
  createWindowsStartupIdentity,
  ensureWindowsNotificationShortcut,
  nativeNotificationsAvailable,
  readWindowsStartupRegistration,
  windowsNotificationShortcutPath,
} from './windowsHost';

const APP_URL = 'app://codexu/index.html';
const APP_SCHEME = 'app';
const SMOKE_SUCCESS = 'CODEXU_ELECTRON_SMOKE_OK';
const SMOKE_FAILURE = 'CODEXU_ELECTRON_SMOKE_FAILED';
const SMOKE_READY_TIMEOUT_MS = 60_000;
const WINDOW_STATE_FILE_NAME = 'window-state.json';
const WINDOW_STATE_SAVE_DELAY_MS = 300;

type MaintenanceShutdownOutcome =
  | { success: true }
  | { success: false; reason: unknown };

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
let sidecarExecutablePath: string | undefined;
let tray: Tray | undefined;
let persistentLog: PersistentLog | undefined;
let nativeNotifications: NativeNotificationAdapter | undefined;
let hostSettings: HostSettings = { ...DEFAULT_HOST_SETTINGS };
let registeredGlobalHotKey: string | undefined;
let isGlobalHotKeyRegistered = false;
let appliedCompactMode: boolean | undefined;
let expandedWindowBounds: Rectangle | undefined;
let sidecarHandshakeVersion = 'unknown';
let allowQuit = false;
let shutdownStarted = false;
let desiredExitCode = 0;
let windowStateSaveTimer: NodeJS.Timeout | undefined;
let sidecarRecoveryTimer: NodeJS.Timeout | undefined;
let rendererRecoveryTimer: NodeJS.Timeout | undefined;
let sidecarRecoveryRunning = false;
let rendererRecoveryRunning = false;
let recoveryDialogDraining = false;
let windowsDesktopIdentityConfigured = false;
let windowsNotificationShortcutConfigured = false;
let notificationActivationPending = false;
const activeSidecars = new Set<SidecarClient>();
const sidecarStderrBuffers = new Map<SidecarClient, TextLineBuffer>();
const sidecarRecovery = new RecoverySupervisor();
const rendererRecovery = new RecoverySupervisor();
const rendererFailureGeneration = new GenerationFence();
const settingsUpdateGeneration = new GenerationFence();
const rendererNavigation = new SingleFlightOperation();
const startupRegistrationRefresh = new SingleFlightOperation();
const maintenanceShutdownRequests = new CompletionQueue<string, MaintenanceShutdownOutcome>();
const recoveryPrompts = new RecoveryPromptQueue<{
  component: string;
  retry: () => void;
}>();
let activeRecoveryDialog: { key: string; controller: AbortController } | undefined;
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
    initializeWindowsDesktopIdentity();
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
    try {
      writeMaintenanceShutdownMarker(maintenanceShutdownMarker);
      app.releaseSingleInstanceLock();
      app.exit(0);
    } catch (reason) {
      console.error('[shutdown] failed to acknowledge empty resident set:', errorMessage(reason));
      app.releaseSingleInstanceLock();
      app.exit(1);
    }
    return;
  }

  initializePersistentLog();
  registerWindowsNotificationActivation();
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

function initializePersistentLog(): void {
  try {
    persistentLog = new PersistentLog({
      directory: path.join(app.getPath('userData'), 'logs'),
      onError: (error) => console.error('[logging] persistent log failed:', error.message),
    });
    persistentLog.write('info', 'electron.lifecycle', 'host starting');
  } catch (reason) {
    console.error('[logging] persistent log unavailable:', errorMessage(reason));
  }
}

function initializeWindowsDesktopIdentity(): void {
  try {
    windowsDesktopIdentityConfigured = configureWindowsDesktopIdentity(process.platform, app);
  } catch (reason) {
    windowsDesktopIdentityConfigured = false;
    console.error('[notification] Windows desktop identity is unavailable:', errorMessage(reason));
  }
}

function registerWindowsNotificationActivation(): void {
  if (!windowsDesktopIdentityConfigured || smokeTest) return;
  Notification.handleActivation((details) => {
    if (shutdownStarted || allowQuit) return;
    runtimeLog('info', 'notification.activation', `type=${details.type}`);
    if (!mainWindow || mainWindow.isDestroyed()) {
      notificationActivationPending = true;
      return;
    }
    showAndFocusMainWindow();
  });
}

function initializeWindowsNotificationShortcut(): void {
  windowsNotificationShortcutConfigured = false;
  if (process.platform !== 'win32' || !app.isPackaged || smokeTest
      || !windowsDesktopIdentityConfigured) {
    return;
  }

  try {
    const shortcutPath = windowsNotificationShortcutPath(app.getPath('appData'));
    mkdirSync(path.dirname(shortcutPath), { recursive: true });
    windowsNotificationShortcutConfigured = ensureWindowsNotificationShortcut(
      process.platform,
      app.isPackaged,
      shell,
      shortcutPath,
      process.execPath,
    );
    if (!windowsNotificationShortcutConfigured) {
      runtimeLog(
        'warn',
        'notification.identity',
        'Windows Start Menu notification identity could not be verified; native notifications are disabled',
      );
    }
  } catch (reason) {
    windowsNotificationShortcutConfigured = false;
    runtimeLog(
      'error',
      'notification.identity',
      'failed to create or verify the Windows Start Menu shortcut',
      reason,
    );
  }
}

function createNativeNotificationAdapter(): NativeNotificationAdapter {
  return new NativeNotificationAdapter({
    isSupported: () => nativeNotificationsAvailable(
      process.platform,
      app.isPackaged,
      smokeTest,
      windowsDesktopIdentityConfigured && windowsNotificationShortcutConfigured,
      () => Notification.isSupported(),
    ),
    create: (options) => new Notification(options),
    activateWindow: showAndFocusMainWindow,
    onFailure: (failure) => {
      runtimeLog(
        failure.stage === 'availability' ? 'warn' : 'error',
        `notification.${failure.stage}`,
        failure.error,
        failure.notification ? { notificationId: failure.notification.id } : undefined,
      );
    },
  });
}

async function bootstrap(): Promise<void> {
  const rendererRoot = resolveRendererRoot();
  assertRendererExists(rendererRoot);
  registerAppProtocol(rendererRoot);
  configureSessionSecurity();

  sidecarExecutablePath = resolveSidecarPath();
  if (!existsSync(sidecarExecutablePath) || !statSync(sidecarExecutablePath).isFile()) {
    throw new Error(`Sidecar executable was not found: ${sidecarExecutablePath}`);
  }

  initializeWindowsNotificationShortcut();
  nativeNotifications = createNativeNotificationAdapter();
  await startSidecar();
  if (smokeTest && (tray !== undefined || registeredGlobalHotKey !== undefined
      || isGlobalHotKeyRegistered)) {
    throw new Error('Smoke mode unexpectedly initialized native tray or shortcut state.');
  }
  registerRendererIpc();
  mainWindow = await createMainWindow(hostSettings);
  if (notificationActivationPending) {
    notificationActivationPending = false;
    showAndFocusMainWindow();
  }

  if (smokeTest) {
    await withTimeout(
      rendererReady,
      SMOKE_READY_TIMEOUT_MS,
      'Vue did not signal app.ready before the smoke-test deadline.',
    );
    await sidecar?.waitForIdle(SMOKE_READY_TIMEOUT_MS);
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
    await sidecar?.waitForIdle(SMOKE_READY_TIMEOUT_MS);
    console.log(
      `${SMOKE_SUCCESS}: app-loaded backend=${sidecarHandshakeVersion}`
      + ` host-state=${String(isGlobalHotKeyRegistered)}`
      + ' reverse-rpc=rates.export-cancelled',
    );
    requestQuit(0);
  }
}

async function startSidecar(): Promise<void> {
  const executablePath = sidecarExecutablePath;
  if (!executablePath) throw new Error('Sidecar executable path has not been resolved.');

  const client = new SidecarClient({
    executablePath,
    arguments: resolveSidecarArguments(),
    cwd: path.dirname(executablePath),
    environment: {
      ...process.env,
      CODEXU_PARENT_PID: String(process.pid),
      ...(persistentLog ? { CODEXU_ELECTRON_LOG_DIRECTORY: path.dirname(persistentLog.filePath) } : {}),
    },
    hostRequestHandler: createHostRequestHandler(dialog, () => mainWindow, {
      // Smoke tests exercise the full reverse-RPC path without ever presenting
      // native UI or making a destructive confirmation choice.
      forceSafeCancellation: smokeTest,
      startupRegistration: shouldApplyStartupRegistration(
        process.platform,
        app.isPackaged,
        smokeTest,
      ) ? { setEnabled: applyStartupRegistrationVerified } : undefined,
    }),
  });
  let candidateFailure: Error | undefined;
  activeSidecars.add(client);
  sidecarStderrBuffers.set(client, new TextLineBuffer());
  client.on('stderr', (message: string) => bufferSidecarStderr(client, message));
  client.once('exit', (exit: SidecarExit) => {
    candidateFailure ??= new Error('Sidecar exited before it became the active connection.');
    flushSidecarStderr(client);
    activeSidecars.delete(client);
    handleSidecarExit(client, exit);
  });
  client.on('processError', (error: Error) => {
    candidateFailure ??= error;
    runtimeLog('error', 'sidecar.process', error);
  });
  client.on('protocolError', (error: Error) => {
    candidateFailure ??= error;
    runtimeLog('error', 'sidecar.protocol', error);
    if (sidecar === client && !shutdownStarted && !allowQuit) {
      sidecar = undefined;
      scheduleSidecarRecovery();
    }
  });

  try {
    const handshake = await client.start();
    assertRequiredSidecarCapabilities(handshake.capabilities);
    const initialSettings = await reconcileStartupRegistrationState(
      client,
      parseHostSettings(await client.request('settings.get', {})),
    );
    await applyHostSettings(initialSettings, client);
    if (shutdownStarted || allowQuit) {
      throw new Error('Sidecar completed startup after application shutdown began.');
    }
    if (candidateFailure || !activeSidecars.has(client)) {
      throw candidateFailure ?? new Error('Sidecar exited before it became the active connection.');
    }

    client.on('event', (event: SidecarEvent) => {
      if (sidecar === client) handleSidecarEvent(client, event);
    });
    sidecarHandshakeVersion = handshake.backendVersion;
    sidecar = client;
    runtimeLog('info', 'sidecar.lifecycle', `connected backend=${handshake.backendVersion}`);
  } catch (reason) {
    if (sidecar === client) sidecar = undefined;
    try {
      await client.shutdown();
      flushSidecarStderr(client);
      activeSidecars.delete(client);
    } catch (shutdownReason) {
      runtimeLog('warn', 'sidecar.lifecycle', 'failed to retire startup candidate', shutdownReason);
      // Preserve the original connection failure.
    }
    throw reason;
  }
}

function registerLifecycleHandlers(): void {
  app.on('second-instance', (_event, commandLine) => {
    try {
      const marker = resolveMaintenanceShutdownMarker(commandLine, tmpdir());
      if (marker) {
        registerMaintenanceShutdownRequest(marker);
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
    void refreshStartupRegistrationState();
    if (nativeActivationAction(smokeTest, 'activate') === 'show') {
      showAndFocusMainWindow();
    }
  });

  app.on('browser-window-focus', () => {
    void refreshStartupRegistrationState();
  });

  app.on('window-all-closed', () => app.quit());

  app.on('before-quit', (event) => {
    if (allowQuit) return;
    event.preventDefault();
    if (shutdownStarted) return;
    shutdownStarted = true;

    void shutdownActiveSidecars().then(
      () => completeApplicationShutdown({ success: true }),
      (reason) => completeApplicationShutdown({ success: false, reason }),
    );
  });

  app.on('will-quit', () => disposeNativeShell());
  nativeTheme.on('updated', () => updateWindowBackground());

  process.once('SIGINT', () => requestQuit(0));
  process.once('SIGTERM', () => requestQuit(0));
}

function registerMaintenanceShutdownRequest(marker: string): void {
  const registration = maintenanceShutdownRequests.register(marker);
  if (registration.completed) {
    if (registration.outcome) acknowledgeMaintenanceShutdown(marker, registration.outcome);
    requestQuit(desiredExitCode);
    return;
  }
  if (!shutdownStarted) requestQuit(0);
}

function completeApplicationShutdown(outcome: MaintenanceShutdownOutcome): void {
  if (!outcome.success) {
    runtimeLog('error', 'sidecar.shutdown', 'one or more Sidecars did not close', outcome.reason);
    desiredExitCode = 1;
  }
  for (const marker of maintenanceShutdownRequests.complete(outcome)) {
    acknowledgeMaintenanceShutdown(marker, outcome);
  }
  allowQuit = true;
  requestQuit(desiredExitCode);
}

function acknowledgeMaintenanceShutdown(
  maintenanceMarker: string,
  outcome: MaintenanceShutdownOutcome,
): void {
  try {
    if (outcome.success) writeMaintenanceShutdownMarker(maintenanceMarker);
    else writeMaintenanceShutdownFailureMarker(maintenanceMarker);
  } catch (reason) {
    desiredExitCode = 1;
    runtimeLog('error', 'shutdown.maintenance', 'failed to write maintenance marker', reason);
  }
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
      if (method === 'settings.update') settingsUpdateGeneration.advance();
      return sidecar.request(method, payload, requestTimeoutForMethod(method));
    },
  );
}

async function createMainWindow(settings: HostSettings): Promise<BrowserWindow> {
  const layout = windowLayout(settings.compactMode);
  const persistedState = loadWindowState(windowStateFilePath());
  const restoredState = restoreWindowState(
    persistedState,
    orderedDisplays().map(toWindowDisplay),
    { x: 0, y: 0, width: layout.width, height: layout.height },
    { width: layout.minimumWidth, height: layout.minimumHeight },
  );
  const restoredWorkArea = screen.getDisplayMatching(restoredState.bounds).workArea;
  const effectiveMinimum = fitWindowSizeToWorkArea(
    { width: layout.minimumWidth, height: layout.minimumHeight },
    restoredWorkArea,
  );
  const window = new BrowserWindow({
    ...restoredState.bounds,
    minWidth: effectiveMinimum.width,
    minHeight: effectiveMinimum.height,
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
    rendererFailureGeneration.advance();
    runtimeLog('error', 'renderer.process', `process gone: ${details.reason}`, details);
    scheduleRendererRecovery(window);
  });
  const refreshDisplayConstraints = () => updateWindowMinimumSizeForDisplay(window);
  window.on('move', () => {
    refreshDisplayConstraints();
    scheduleWindowStateSave(window);
  });
  window.on('resize', () => scheduleWindowStateSave(window));
  window.on('maximize', () => scheduleWindowStateSave(window));
  window.on('unmaximize', () => scheduleWindowStateSave(window));
  screen.on('display-metrics-changed', refreshDisplayConstraints);
  screen.on('display-removed', refreshDisplayConstraints);
  window.on('close', (event) => {
    flushWindowStateSave(window);
    const trayAvailable = shouldEnableNativeDesktopFeatures(process.platform, smokeTest)
      && Boolean(tray && !tray.isDestroyed());
    if (shouldHideWindowOnClose(hostSettings, shutdownStarted || allowQuit, trayAvailable)) {
      event.preventDefault();
      window.hide();
    }
  });
  window.once('closed', () => {
    screen.removeListener('display-metrics-changed', refreshDisplayConstraints);
    screen.removeListener('display-removed', refreshDisplayConstraints);
    clearWindowStateSaveTimer();
    if (mainWindow === window) mainWindow = undefined;
    appliedCompactMode = undefined;
    expandedWindowBounds = undefined;
  });
  window.once('ready-to-show', () => {
    if (restoredState.maximized && !settings.compactMode) window.maximize();
    if (!smokeTest) window.show();
  });

  try {
    await loadRenderer(window);
    return window;
  } catch (reason) {
    if (mainWindow === window) mainWindow = undefined;
    if (!window.isDestroyed()) window.destroy();
    throw reason;
  }
}

function loadRenderer(window: BrowserWindow): Promise<void> {
  return rendererNavigation.run(() => navigateRenderer(window));
}

async function navigateRenderer(window: BrowserWindow): Promise<void> {
  if (window.isDestroyed() || window.webContents.isDestroyed()) {
    throw new Error('Renderer navigation target is no longer available.');
  }
  await window.loadURL(APP_URL);
}

function orderedDisplays(): Display[] {
  const primary = screen.getPrimaryDisplay();
  return [primary, ...screen.getAllDisplays().filter((display) => display.id !== primary.id)];
}

function toWindowDisplay(display: Display): WindowDisplay {
  return {
    id: String(display.id),
    scaleFactor: display.scaleFactor,
    workArea: { ...display.workArea },
  };
}

function windowStateFilePath(): string {
  return path.join(app.getPath('userData'), WINDOW_STATE_FILE_NAME);
}

function scheduleWindowStateSave(window: BrowserWindow): void {
  if (smokeTest || shutdownStarted || window.isDestroyed()) return;
  clearWindowStateSaveTimer();
  windowStateSaveTimer = setTimeout(() => {
    windowStateSaveTimer = undefined;
    persistWindowState(window);
  }, WINDOW_STATE_SAVE_DELAY_MS);
  windowStateSaveTimer.unref();
}

function flushWindowStateSave(window: BrowserWindow): void {
  if (smokeTest || window.isDestroyed()) return;
  clearWindowStateSaveTimer();
  persistWindowState(window);
}

function clearWindowStateSaveTimer(): void {
  if (!windowStateSaveTimer) return;
  clearTimeout(windowStateSaveTimer);
  windowStateSaveTimer = undefined;
}

function persistWindowState(window: BrowserWindow): void {
  if (window.isDestroyed() || window.isMinimized() || window.isFullScreen()) return;
  try {
    const bounds = window.isMaximized() ? window.getNormalBounds() : window.getBounds();
    const display = screen.getDisplayMatching(bounds);
    saveWindowState(
      windowStateFilePath(),
      createWindowState(bounds, toWindowDisplay(display), window.isMaximized()),
    );
  } catch (reason) {
    runtimeLog('warn', 'window.state', 'failed to persist window state', reason);
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

function handleSidecarEvent(source: SidecarClient, event: SidecarEvent): void {
  if (shouldSuppressHostEventInSmoke(smokeTest, event.method)) {
    failAndQuit(new Error(`Smoke test rejected native host event: ${event.method}`));
    return;
  }

  switch (event.method) {
    case 'settings.changed':
      void applyChangedSettings(source, event);
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
    case 'host.notification.show': {
      const result = nativeNotifications?.show(event.payload) ?? 'unsupported';
      if (result !== 'shown' && result !== 'duplicate') {
        runtimeLog('warn', 'notification.event', `notification result=${result}`);
      }
      return;
    }
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

async function applyChangedSettings(source: SidecarClient, event: SidecarEvent): Promise<void> {
  try {
    const settings = parseHostSettings(event.payload);
    if (sidecar !== source) return;
    await applyHostSettings(settings, source);
    if (sidecar === source) forwardRendererEvent(event);
  } catch (reason) {
    if (shutdownStarted || allowQuit) return;
    if (sidecar !== source) {
      runtimeLog('warn', 'settings.changed', 'ignored failure from a retired Sidecar', reason);
      return;
    }

    sidecar = undefined;
    runtimeLog('error', 'settings.changed', 'failed to reconcile settings; restarting Sidecar', reason);
    try {
      await source.shutdown();
    } catch (shutdownReason) {
      runtimeLog('warn', 'settings.changed', 'failed to retire Sidecar cleanly', shutdownReason);
    }
    scheduleSidecarRecovery();
  }
}

async function applyHostSettings(
  settings: HostSettings,
  targetSidecar: SidecarClient | undefined = sidecar,
): Promise<void> {
  hostSettings = settings;
  nativeTheme.themeSource = settings.theme;
  applyCompactWindowMode(settings.compactMode);
  ensureTray();
  rebuildTrayMenu();

  isGlobalHotKeyRegistered = configureGlobalHotKey(settings.globalHotKey);
  if (!targetSidecar) throw new Error('Sidecar is unavailable while applying host settings.');
  await targetSidecar.sendHostState(isGlobalHotKeyRegistered);
}

function ensureTray(): void {
  if (!shouldEnableNativeDesktopFeatures(process.platform, smokeTest)
      || (tray && !tray.isDestroyed())) return;

  const trayImage = nativeImage.createFromPath(resolveAppIconPath());
  if (trayImage.isEmpty()) throw new Error('The packaged tray icon could not be loaded.');
  tray = new Tray(trayImage);
  tray.setToolTip('codexU · 本地 AI 用量');
  tray.on('click', () => showAndFocusMainWindow());
}

function rebuildTrayMenu(): void {
  if (!shouldEnableNativeDesktopFeatures(process.platform, smokeTest)
      || !tray || tray.isDestroyed()) return;

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
  if (!shouldEnableNativeDesktopFeatures(process.platform, smokeTest)) return false;
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
  const workArea = screen.getDisplayMatching(window.getBounds()).workArea;
  const minimum = fitWindowSizeToWorkArea(
    { width: layout.minimumWidth, height: layout.minimumHeight },
    workArea,
  );
  const desired = fitWindowSizeToWorkArea(
    { width: layout.width, height: layout.height },
    workArea,
  );

  if (appliedCompactMode === compactMode) {
    window.setMinimumSize(minimum.width, minimum.height);
    updateWindowBackground();
    return;
  }

  if (window.isMaximized()) window.unmaximize();
  if (compactMode) {
    if (appliedCompactMode === false && !window.isFullScreen()) {
      expandedWindowBounds = window.getBounds();
    }
    window.setMinimumSize(minimum.width, minimum.height);
    setWindowBoundsIfChanged(window, fitWindowBoundsToWorkArea({
      ...window.getBounds(),
      width: desired.width,
      height: desired.height,
    }, workArea));
  } else {
    window.setMinimumSize(minimum.width, minimum.height);
    if (expandedWindowBounds) {
      setWindowBoundsIfChanged(
        window,
        fitWindowBoundsToWorkArea(expandedWindowBounds, workArea),
      );
      expandedWindowBounds = undefined;
    } else {
      setWindowBoundsIfChanged(window, fitWindowBoundsToWorkArea({
        ...window.getBounds(),
        width: desired.width,
        height: desired.height,
      }, workArea));
    }
  }
  appliedCompactMode = compactMode;
  updateWindowBackground();
}

function updateWindowMinimumSizeForDisplay(window: BrowserWindow): void {
  if (window.isDestroyed()) return;
  const layout = windowLayout(hostSettings.compactMode);
  const workArea = screen.getDisplayMatching(window.getBounds()).workArea;
  const minimum = fitWindowSizeToWorkArea(
    { width: layout.minimumWidth, height: layout.minimumHeight },
    workArea,
  );
  const [currentMinimumWidth, currentMinimumHeight] = window.getMinimumSize();
  if (currentMinimumWidth !== minimum.width || currentMinimumHeight !== minimum.height) {
    window.setMinimumSize(minimum.width, minimum.height);
  }
  if (!window.isMaximized() && !window.isFullScreen()) {
    const currentBounds = window.getBounds();
    const fittedBounds = fitWindowBoundsToWorkArea(currentBounds, workArea);
    setWindowBoundsIfChanged(window, fittedBounds);
  }
}

function setWindowBoundsIfChanged(window: BrowserWindow, bounds: Rectangle): void {
  if (!equalWindowBounds(window.getBounds(), bounds)) window.setBounds(bounds);
}

function updateWindowBackground(): void {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.setBackgroundColor(windowBackgroundColor());
}

function windowBackgroundColor(): string {
  return nativeTheme.shouldUseDarkColors ? '#111318' : '#edf3ff';
}

async function reconcileStartupRegistrationState(
  client: SidecarClient,
  settings: HostSettings,
  isCurrent?: () => boolean,
): Promise<HostSettings> {
  if (!shouldApplyStartupRegistration(process.platform, app.isPackaged, smokeTest)) {
    return settings;
  }
  if (isCurrent && !isCurrent()) return settings;

  let actual: boolean;
  try {
    actual = readWindowsStartupRegistration(
      app,
      createWindowsStartupIdentity(process.execPath),
    );
  } catch (reason) {
    runtimeLog('error', 'startup.registration', 'failed to read native state', reason);
    return settings;
  }

  if (actual === settings.startAtLogin) return settings;
  if (isCurrent && !isCurrent()) return settings;
  runtimeLog(
    'warn',
    'startup.registration',
    `persisted=${String(settings.startAtLogin)} actual=${String(actual)}; synchronizing backend`,
  );
  const synchronized = parseHostSettings(await client.request(
    'settings.reconcileStartupRegistration',
    {
      expected: settings.startAtLogin,
      actual,
    },
  ));
  if (synchronized.startAtLogin !== actual) {
    runtimeLog(
      'info',
      'startup.registration',
      'native-state reconciliation was superseded by a newer settings value',
    );
    return synchronized;
  }
  return synchronized;
}

async function refreshStartupRegistrationState(): Promise<void> {
  await startupRegistrationRefresh.run(async () => {
    const client = sidecar;
    if (!client || shutdownStarted || allowQuit
        || !shouldApplyStartupRegistration(process.platform, app.isPackaged, smokeTest)) {
      return;
    }

    try {
      const updateGeneration = settingsUpdateGeneration.snapshot();
      const isCurrent = () => sidecar === client
        && !shutdownStarted
        && !allowQuit
        && settingsUpdateGeneration.isCurrent(updateGeneration);
      const current = parseHostSettings(await client.request('settings.get', {}));
      if (!isCurrent()) return;
      const synchronized = await reconcileStartupRegistrationState(client, current, isCurrent);
      if (isCurrent()) hostSettings = synchronized;
    } catch (reason) {
      if (shutdownStarted || allowQuit || sidecar !== client) return;
      runtimeLog(
        'warn',
        'startup.registration',
        'failed to refresh the effective Windows startup state',
        reason,
      );
    }
  });
}

function applyStartupRegistrationVerified(enabled: boolean): boolean {
  if (!shouldApplyStartupRegistration(process.platform, app.isPackaged, smokeTest)) {
    throw new Error('Startup registration is unavailable in this Electron host.');
  }

  const actual = applyWindowsStartupRegistration(
    app,
    createWindowsStartupIdentity(process.execPath),
    enabled,
  );
  runtimeLog(
    actual === enabled ? 'info' : 'error',
    'startup.registration',
    `requested=${String(enabled)} actual=${String(actual)}`,
  );
  return actual;
}

function disposeNativeShell(): void {
  if (sidecarRecoveryTimer) clearTimeout(sidecarRecoveryTimer);
  if (rendererRecoveryTimer) clearTimeout(rendererRecoveryTimer);
  sidecarRecoveryTimer = undefined;
  rendererRecoveryTimer = undefined;
  clearWindowStateSaveTimer();
  flushAllSidecarStderr();
  nativeNotifications?.dispose();
  nativeNotifications = undefined;
  if (registeredGlobalHotKey) {
    globalShortcut.unregister(registeredGlobalHotKey);
    registeredGlobalHotKey = undefined;
  }
  isGlobalHotKeyRegistered = false;
  if (tray && !tray.isDestroyed()) tray.destroy();
  tray = undefined;
}

function handleSidecarExit(client: SidecarClient, exit: SidecarExit): void {
  if (sidecar !== client) return;
  sidecar = undefined;
  if (shutdownStarted || allowQuit) return;
  runtimeLog(
    'error',
    'sidecar.lifecycle',
    `unexpected exit code=${String(exit.code)} signal=${String(exit.signal)}`,
  );
  scheduleSidecarRecovery();
}

function scheduleSidecarRecovery(): void {
  if (shutdownStarted || allowQuit || sidecar || sidecarRecoveryTimer || sidecarRecoveryRunning) return;
  suspendRendererRecoveryForSidecar();
  if (smokeTest) {
    failAndQuit(new Error('The Sidecar exited during the Electron smoke test.'));
    return;
  }

  const decision = sidecarRecovery.recordFailure();
  if (decision.action === 'stop') {
    queueRecoveryFailure('sidecar', '后台服务', () => {
      sidecarRecovery.reset();
      scheduleSidecarRecovery();
    });
    return;
  }
  if (decision.action === 'wait') return;

  runtimeLog(
    'warn',
    'sidecar.recovery',
    `scheduled attempt=${decision.attempt} delayMs=${decision.delayMilliseconds}`,
  );
  sidecarRecoveryTimer = setTimeout(() => {
    sidecarRecoveryTimer = undefined;
    void recoverSidecar(decision.attempt);
  }, decision.delayMilliseconds);
  sidecarRecoveryTimer.unref();
}

async function recoverSidecar(attempt: number): Promise<void> {
  if (shutdownStarted || allowQuit || sidecarRecoveryRunning) return;
  sidecarRecoveryRunning = true;
  try {
    if (!sidecarRecovery.markRecoveryStarted(attempt)) {
      sidecarRecoveryRunning = false;
      return;
    }
    await retireInactiveSidecars();
    await startSidecar();
    sidecarRecovery.markRecovered();
    cancelRecoveryFailure('sidecar');
    runtimeLog('info', 'sidecar.recovery', `recovered attempt=${attempt}`);
  } catch (reason) {
    runtimeLog('error', 'sidecar.recovery', `attempt=${attempt} failed`, reason);
    sidecarRecoveryRunning = false;
    scheduleSidecarRecovery();
    return;
  }
  sidecarRecoveryRunning = false;

  const window = mainWindow;
  if (!window || window.isDestroyed()) return;
  try {
    await loadRendererForRecovery(window, true);
    markRendererRecoveredBySidecar();
    if (!smokeTest) window.show();
  } catch (reason) {
    runtimeLog('error', 'renderer.reload', 'reload after Sidecar recovery failed', reason);
    scheduleRendererRecovery(window);
  }
}

function scheduleRendererRecovery(window: BrowserWindow): void {
  if (shutdownStarted || allowQuit || window.isDestroyed()
      || rendererRecoveryTimer || rendererRecoveryRunning) return;
  // A Sidecar recovery always performs one renderer navigation after publishing
  // the fully initialized replacement. Let it own that reload instead of opening
  // a second retry circuit while the backend is unavailable.
  if (!sidecar || sidecarRecoveryTimer || sidecarRecoveryRunning) return;
  if (smokeTest) {
    failAndQuit(new Error('The renderer exited during the Electron smoke test.'));
    return;
  }

  const decision = rendererRecovery.recordFailure();
  if (decision.action === 'stop') {
    queueRecoveryFailure('renderer', '界面进程', () => {
      rendererRecovery.reset();
      scheduleRendererRecovery(window);
    });
    return;
  }
  if (decision.action === 'wait') return;

  runtimeLog(
    'warn',
    'renderer.recovery',
    `scheduled attempt=${decision.attempt} delayMs=${decision.delayMilliseconds}`,
  );
  rendererRecoveryTimer = setTimeout(() => {
    rendererRecoveryTimer = undefined;
    void recoverRenderer(window, decision.attempt);
  }, decision.delayMilliseconds);
  rendererRecoveryTimer.unref();
}

async function recoverRenderer(window: BrowserWindow, attempt: number): Promise<void> {
  if (shutdownStarted || allowQuit || window.isDestroyed() || rendererRecoveryRunning) return;
  rendererRecoveryRunning = true;
  try {
    if (!rendererRecovery.markRecoveryStarted(attempt)) {
      rendererRecoveryRunning = false;
      return;
    }
    await loadRendererForRecovery(window);
    rendererRecovery.markRecovered();
    cancelRecoveryFailure('renderer');
    window.show();
    runtimeLog('info', 'renderer.recovery', `recovered attempt=${attempt}`);
  } catch (reason) {
    runtimeLog('error', 'renderer.recovery', `attempt=${attempt} failed`, reason);
    rendererRecoveryRunning = false;
    scheduleRendererRecovery(window);
    return;
  }
  rendererRecoveryRunning = false;
}

async function loadRendererForRecovery(window: BrowserWindow, fresh = false): Promise<void> {
  let failureGeneration = rendererFailureGeneration.snapshot();
  if (fresh) {
    await rendererNavigation.runFresh(async () => {
      // The required Sidecar navigation may have waited for a renderer flight
      // that started against the previous backend. Fence the new flight itself,
      // not the time spent draining that obsolete navigation.
      failureGeneration = rendererFailureGeneration.snapshot();
      await navigateRenderer(window);
    });
  } else {
    await loadRenderer(window);
  }
  if (!rendererFailureGeneration.isCurrent(failureGeneration)) {
    throw new Error('Renderer failed again while its recovery navigation was completing.');
  }
}

function markRendererRecoveredBySidecar(): void {
  suspendRendererRecoveryForSidecar();
}

function suspendRendererRecoveryForSidecar(): void {
  if (rendererRecoveryTimer) clearTimeout(rendererRecoveryTimer);
  rendererRecoveryTimer = undefined;
  rendererRecovery.reset();
  cancelRecoveryFailure('renderer');
}

function cancelRecoveryFailure(key: string): void {
  const cancelled = recoveryPrompts.cancel(key);
  if (cancelled === 'active' && activeRecoveryDialog?.key === key) {
    activeRecoveryDialog.controller.abort();
  }
}

function queueRecoveryFailure(key: string, component: string, retry: () => void): void {
  if (shutdownStarted || allowQuit) return;
  if (!recoveryPrompts.enqueue(key, { component, retry })) return;
  void drainRecoveryFailureQueue();
}

async function drainRecoveryFailureQueue(): Promise<void> {
  if (recoveryDialogDraining || shutdownStarted || allowQuit) return;
  recoveryDialogDraining = true;
  try {
    while (!shutdownStarted && !allowQuit) {
      const queued = recoveryPrompts.take();
      if (!queued) break;
      let exitRequested = false;
      try {
        exitRequested = await presentRecoveryFailure(queued.key, queued.value);
      } catch (reason) {
        if (!recoveryPrompts.isCancelled(queued.key)) {
          runtimeLog('error', 'recovery.dialog', reason);
          requestQuit(1);
          exitRequested = true;
        }
      } finally {
        recoveryPrompts.complete(queued.key);
      }

      if (exitRequested) {
        recoveryPrompts.clearQueued();
        break;
      }
    }
  } finally {
    recoveryDialogDraining = false;
    if (recoveryPrompts.size > 0 && !shutdownStarted && !allowQuit) {
      void drainRecoveryFailureQueue();
    }
  }
}

async function presentRecoveryFailure(
  key: string,
  prompt: { component: string; retry: () => void },
): Promise<boolean> {
  while (!shutdownStarted && !allowQuit) {
    const controller = new AbortController();
    const options = {
      type: 'error' as const,
      title: 'codexU 自动恢复失败',
      message: `${prompt.component}连续恢复失败。`,
      detail: '可以重新尝试、打开本地运行日志，或安全退出应用。日志可能包含路径等运行信息，分享前请先检查。',
      buttons: ['重新尝试', '打开运行日志', '退出'],
      defaultId: 0,
      cancelId: 2,
      noLink: true,
      signal: controller.signal,
    };
    const owner = mainWindow && !mainWindow.isDestroyed() ? mainWindow : undefined;
    activeRecoveryDialog = { key, controller };
    let response = -1;
    try {
      const result = owner
        ? await dialog.showMessageBox(owner, options)
        : await dialog.showMessageBox(options);
      response = result.response;
    } finally {
      if (activeRecoveryDialog?.controller === controller) activeRecoveryDialog = undefined;
    }
    if (recoveryPrompts.isCancelled(key)) return false;
    if (response === 0) {
      prompt.retry();
      return false;
    }
    if (response === 1) {
      const logDirectory = persistentLog
        ? path.dirname(persistentLog.filePath)
        : path.join(app.getPath('userData'), 'logs');
      const openError = await shell.openPath(logDirectory);
      if (openError) runtimeLog('warn', 'diagnostics.logs', openError);
      continue;
    }
    requestQuit(1);
    return true;
  }
  return true;
}

function bufferSidecarStderr(client: SidecarClient, message: string): void {
  const buffer = sidecarStderrBuffers.get(client);
  if (!buffer) return;
  for (const line of buffer.push(message)) logSidecarStderrLine(line);
}

function flushSidecarStderr(client: SidecarClient): void {
  const buffer = sidecarStderrBuffers.get(client);
  if (!buffer) return;
  const line = buffer.flush();
  if (line !== undefined) logSidecarStderrLine(line);
  sidecarStderrBuffers.delete(client);
}

function flushAllSidecarStderr(): void {
  for (const client of [...sidecarStderrBuffers.keys()]) flushSidecarStderr(client);
}

function logSidecarStderrLine(line: string): void {
  if (line.length > 0) runtimeLog('error', 'sidecar.stderr', line);
}

function runtimeLog(
  level: RuntimeLogLevel,
  scope: string,
  message: unknown,
  details?: unknown,
): void {
  const prefix = `[${scope}]`;
  const consoleMethod = level === 'error' ? console.error : level === 'warn' ? console.warn : console.log;
  if (details === undefined) consoleMethod(prefix, message);
  else consoleMethod(prefix, message, details);
  persistentLog?.write(level, scope, message, details);
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
  if (nativeNotifications?.isAvailable) arguments_.push('--native-notifications');
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

async function retireInactiveSidecars(): Promise<void> {
  const clients = [...activeSidecars].filter((client) => client !== sidecar);
  const failures: unknown[] = [];
  await Promise.all(clients.map(async (client) => {
    try {
      await client.shutdown();
      flushSidecarStderr(client);
      activeSidecars.delete(client);
    } catch (reason) {
      failures.push(reason);
      runtimeLog('error', 'sidecar.retire', reason);
    }
  }));
  if (failures.length > 0) {
    throw new AggregateError(failures, 'A previous Sidecar is still active.');
  }
}

async function shutdownActiveSidecars(): Promise<void> {
  const clients = [...activeSidecars];
  const failures: unknown[] = [];
  await Promise.all(clients.map(async (client) => {
    try {
      await client.shutdown();
      flushSidecarStderr(client);
      activeSidecars.delete(client);
    } catch (reason) {
      failures.push(reason);
      runtimeLog('error', 'sidecar.shutdown', reason);
    }
  }));

  if (failures.length > 0) {
    throw new AggregateError(failures, 'One or more Sidecar processes could not stop safely.');
  }
}

function requestQuit(exitCode: number): void {
  const decision = decideQuitRequest(desiredExitCode, exitCode, allowQuit);
  desiredExitCode = decision.exitCode;
  if (decision.action === 'exit') {
    const window = mainWindow;
    if (window && !window.isDestroyed()) flushWindowStateSave(window);
    disposeNativeShell();
    app.exit(decision.exitCode);
    return;
  }
  app.quit();
}

function failAndQuit(reason: unknown): void {
  const message = errorMessage(reason);
  persistentLog?.write('error', 'electron.fatal', reason);
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
