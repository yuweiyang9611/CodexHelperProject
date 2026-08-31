import path from 'node:path';

export const WINDOWS_APP_USER_MODEL_ID = 'io.github.yuweiyang9611.CodexU';
export const WINDOWS_TOAST_ACTIVATOR_CLSID = '{073466E0-6E09-49FC-A4D3-900BED0DBD46}';
export const WINDOWS_LOGIN_ITEM_NAME = 'codexU';

export interface WindowsDesktopIdentityApi {
  setAppUserModelId(id: string): void;
  setToastActivatorCLSID(id: string): void;
}

export interface WindowsNotificationShortcutDetails {
  target: string;
  cwd?: string;
  args?: string;
  description?: string;
  icon?: string;
  iconIndex?: number;
  appUserModelId?: string;
  toastActivatorClsid?: string;
}

export interface WindowsNotificationShortcutApi {
  readShortcutLink(shortcutPath: string): WindowsNotificationShortcutDetails;
  writeShortcutLink(
    shortcutPath: string,
    operation: 'create' | 'update' | 'replace',
    options: WindowsNotificationShortcutDetails,
  ): boolean;
}

export interface WindowsLoginItem {
  name: string;
  path: string;
  args: string[];
  enabled: boolean;
}

export interface WindowsLoginItemState {
  openAtLogin: boolean;
  executableWillLaunchAtLogin: boolean;
  launchItems: WindowsLoginItem[];
}

export interface WindowsStartupRegistrationApi {
  setLoginItemSettings(options: {
    openAtLogin: boolean;
    enabled: boolean;
    name: string;
    path: string;
    args: string[];
  }): void;
  getLoginItemSettings(options: {
    path: string;
    args: string[];
  }): WindowsLoginItemState;
}

export interface WindowsStartupIdentity {
  name: string;
  path: string;
  args: string[];
}

/**
 * Installs the identity that Electron and the installer shortcut share for
 * Windows Action Center activation. This must run before a notification is
 * created so Electron does not fall back to a process-local random CLSID.
 */
export function configureWindowsDesktopIdentity(
  platform: NodeJS.Platform,
  api: WindowsDesktopIdentityApi,
): boolean {
  if (platform !== 'win32') return false;
  api.setAppUserModelId(WINDOWS_APP_USER_MODEL_ID);
  api.setToastActivatorCLSID(WINDOWS_TOAST_ACTIVATOR_CLSID);
  return true;
}

export function nativeNotificationsAvailable(
  platform: NodeJS.Platform,
  isPackaged: boolean,
  smokeTest: boolean,
  windowsIdentityConfigured: boolean,
  isSupported: () => boolean,
): boolean {
  if (smokeTest) return false;
  if (platform === 'win32' && (!isPackaged || !windowsIdentityConfigured)) return false;
  return isSupported();
}

export function windowsNotificationShortcutPath(appDataDirectory: string): string {
  if (appDataDirectory.trim().length === 0) {
    throw new TypeError('Windows app-data directory must not be empty.');
  }
  return path.win32.join(
    appDataDirectory,
    'Microsoft',
    'Windows',
    'Start Menu',
    'Programs',
    'codexU',
    'codexU.lnk',
  );
}

/**
 * Ensures the current executable has the per-user Start Menu identity Windows
 * requires for Action Center and cold notification activation. The read-back is
 * intentional: writeShortcutLink returning true does not prove that the shell
 * persisted the reviewed AUMID and activator CLSID.
 */
export function ensureWindowsNotificationShortcut(
  platform: NodeJS.Platform,
  isPackaged: boolean,
  api: WindowsNotificationShortcutApi,
  shortcutPath: string,
  executablePath: string,
): boolean {
  if (platform !== 'win32' || !isPackaged) return false;
  if (shortcutPath.trim().length === 0 || executablePath.trim().length === 0) {
    throw new TypeError('Windows notification shortcut and executable paths must not be empty.');
  }

  try {
    if (notificationShortcutMatches(api.readShortcutLink(shortcutPath), executablePath)) {
      return true;
    }
  } catch {
    // Missing and unreadable shortcuts both take the fail-closed replacement path.
  }

  const details: WindowsNotificationShortcutDetails = {
    target: executablePath,
    cwd: path.win32.dirname(executablePath),
    args: '',
    description: 'codexU',
    icon: executablePath,
    iconIndex: 0,
    appUserModelId: WINDOWS_APP_USER_MODEL_ID,
    toastActivatorClsid: WINDOWS_TOAST_ACTIVATOR_CLSID,
  };
  // Electron's "create" operation creates or overwrites, while "replace"
  // fails when a portable ZIP has no pre-existing installer shortcut.
  if (!api.writeShortcutLink(shortcutPath, 'create', details)) return false;
  return notificationShortcutMatches(api.readShortcutLink(shortcutPath), executablePath);
}

export function createWindowsStartupIdentity(executablePath: string): WindowsStartupIdentity {
  return {
    name: WINDOWS_LOGIN_ITEM_NAME,
    path: executablePath,
    args: [],
  };
}

/**
 * Reads the effective Windows state, including Startup Apps approval. The Run
 * key alone is insufficient because Windows can leave it present while marking
 * the entry disabled in Task Manager/Settings.
 */
export function readWindowsStartupRegistration(
  api: WindowsStartupRegistrationApi,
  identity: Readonly<WindowsStartupIdentity>,
): boolean {
  const state = api.getLoginItemSettings({
    path: identity.path,
    args: [...identity.args],
  });
  const matchingEnabledItem = state.launchItems.some((item) =>
    equalWindowsName(item.name, identity.name)
      && equalWindowsPath(item.path, identity.path)
      && equalArguments(item.args, identity.args)
      && item.enabled);

  return state.openAtLogin
    && state.executableWillLaunchAtLogin
    && matchingEnabledItem;
}

export function applyWindowsStartupRegistration(
  api: WindowsStartupRegistrationApi,
  identity: Readonly<WindowsStartupIdentity>,
  enabled: boolean,
): boolean {
  api.setLoginItemSettings({
    openAtLogin: enabled,
    enabled,
    name: identity.name,
    path: identity.path,
    args: [...identity.args],
  });
  return readWindowsStartupRegistration(api, identity);
}

function equalWindowsName(left: string, right: string): boolean {
  return left.localeCompare(right, 'en-US', { sensitivity: 'accent' }) === 0;
}

function equalWindowsPath(left: string, right: string): boolean {
  return path.win32.normalize(left).toLocaleLowerCase('en-US')
    === path.win32.normalize(right).toLocaleLowerCase('en-US');
}

function notificationShortcutMatches(
  details: Readonly<WindowsNotificationShortcutDetails>,
  executablePath: string,
): boolean {
  return equalWindowsPath(details.target, executablePath)
    && (details.args ?? '').trim().length === 0
    && details.appUserModelId === WINDOWS_APP_USER_MODEL_ID
    && normalizeClsid(details.toastActivatorClsid) === normalizeClsid(WINDOWS_TOAST_ACTIVATOR_CLSID);
}

function normalizeClsid(value: string | undefined): string {
  return (value ?? '').trim().replace(/^\{(.*)\}$/u, '$1').toLocaleLowerCase('en-US');
}

function equalArguments(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length
    && left.every((value, index) => value === right[index]);
}
