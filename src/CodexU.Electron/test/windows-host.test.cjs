const assert = require('node:assert/strict');
const test = require('node:test');
const {
  WINDOWS_APP_USER_MODEL_ID,
  WINDOWS_LOGIN_ITEM_NAME,
  WINDOWS_TOAST_ACTIVATOR_CLSID,
  applyWindowsStartupRegistration,
  configureWindowsDesktopIdentity,
  createWindowsStartupIdentity,
  ensureWindowsNotificationShortcut,
  nativeNotificationsAvailable,
  readWindowsStartupRegistration,
  windowsNotificationShortcutPath,
} = require('../dist/windowsHost.js');

test('configures the stable Windows notification identity only on Windows', () => {
  const calls = [];
  const api = {
    setAppUserModelId: (id) => calls.push(['aumid', id]),
    setToastActivatorCLSID: (id) => calls.push(['clsid', id]),
  };

  assert.equal(configureWindowsDesktopIdentity('linux', api), false);
  assert.deepEqual(calls, []);
  assert.equal(configureWindowsDesktopIdentity('win32', api), true);
  assert.deepEqual(calls, [
    ['aumid', WINDOWS_APP_USER_MODEL_ID],
    ['clsid', WINDOWS_TOAST_ACTIVATOR_CLSID],
  ]);
});

test('advertises notifications only when Windows has packaged shortcut identity', () => {
  assert.equal(nativeNotificationsAvailable('win32', true, false, true, () => true), true);
  assert.equal(nativeNotificationsAvailable('win32', false, false, true, () => true), false);
  assert.equal(nativeNotificationsAvailable('win32', true, false, false, () => true), false);
  assert.equal(nativeNotificationsAvailable('win32', true, true, true, () => true), false);
  assert.equal(nativeNotificationsAvailable('linux', true, false, false, () => true), true);
  assert.equal(nativeNotificationsAvailable('linux', true, false, false, () => false), false);
});

test('resolves the per-user Start Menu notification shortcut', () => {
  assert.equal(
    windowsNotificationShortcutPath('C:\\Users\\Ada\\AppData\\Roaming'),
    'C:\\Users\\Ada\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu'
      + '\\Programs\\codexU\\codexU.lnk',
  );
  assert.throws(() => windowsNotificationShortcutPath('  '), TypeError);
});

test('accepts an existing shortcut only after target and notification identity read back', () => {
  const executablePath = 'C:\\Apps\\codexU\\CodexU.exe';
  let writes = 0;
  const api = {
    readShortcutLink: () => ({
      target: 'c:\\apps\\CODEXU\\CodexU.exe',
      args: '',
      appUserModelId: WINDOWS_APP_USER_MODEL_ID,
      toastActivatorClsid: WINDOWS_TOAST_ACTIVATOR_CLSID.toLowerCase(),
    }),
    writeShortcutLink: () => { writes += 1; return true; },
  };

  assert.equal(
    ensureWindowsNotificationShortcut(
      'win32', true, api, 'C:\\Start Menu\\codexU.lnk', executablePath,
    ),
    true,
  );
  assert.equal(writes, 0);
  assert.equal(
    ensureWindowsNotificationShortcut(
      'linux', true, api, 'C:\\Start Menu\\codexU.lnk', executablePath,
    ),
    false,
  );
  assert.equal(
    ensureWindowsNotificationShortcut(
      'win32', false, api, 'C:\\Start Menu\\codexU.lnk', executablePath,
    ),
    false,
  );
});

test('replaces and verifies a missing or mismatched notification shortcut', () => {
  const executablePath = 'C:\\Apps\\codexU\\CodexU.exe';
  let stored;
  const writes = [];
  const api = {
    readShortcutLink() {
      if (!stored) throw new Error('missing');
      return stored;
    },
    writeShortcutLink(shortcutPath, operation, options) {
      writes.push({ shortcutPath, operation, options });
      stored = options;
      return true;
    },
  };

  assert.equal(
    ensureWindowsNotificationShortcut(
      'win32', true, api, 'C:\\Start Menu\\codexU.lnk', executablePath,
    ),
    true,
  );
  assert.equal(writes.length, 1);
  assert.equal(writes[0].operation, 'create');
  assert.deepEqual(writes[0].options, {
    target: executablePath,
    cwd: 'C:\\Apps\\codexU',
    args: '',
    description: 'codexU',
    icon: executablePath,
    iconIndex: 0,
    appUserModelId: WINDOWS_APP_USER_MODEL_ID,
    toastActivatorClsid: WINDOWS_TOAST_ACTIVATOR_CLSID,
  });
});

test('fails closed when the shell does not persist the reviewed shortcut identity', () => {
  const api = {
    readShortcutLink: () => ({
      target: 'C:\\Apps\\codexU\\CodexU.exe',
      args: '',
      appUserModelId: 'another.application',
      toastActivatorClsid: WINDOWS_TOAST_ACTIVATOR_CLSID,
    }),
    writeShortcutLink: () => true,
  };

  assert.equal(
    ensureWindowsNotificationShortcut(
      'win32',
      true,
      api,
      'C:\\Start Menu\\codexU.lnk',
      'C:\\Apps\\codexU\\CodexU.exe',
    ),
    false,
  );
});

test('reads the effective matching and StartupApproved login-item state', () => {
  const identity = createWindowsStartupIdentity('C:\\Program Files\\codexU\\CodexU.exe');
  const state = {
    openAtLogin: true,
    executableWillLaunchAtLogin: true,
    launchItems: [{
      name: WINDOWS_LOGIN_ITEM_NAME.toUpperCase(),
      path: 'c:\\program files\\CODEXU\\CodexU.exe',
      args: [],
      enabled: true,
    }],
  };
  const api = {
    setLoginItemSettings() {},
    getLoginItemSettings: () => state,
  };

  assert.equal(readWindowsStartupRegistration(api, identity), true);
  state.launchItems[0].enabled = false;
  assert.equal(readWindowsStartupRegistration(api, identity), false);
  state.launchItems[0].enabled = true;
  state.executableWillLaunchAtLogin = false;
  assert.equal(readWindowsStartupRegistration(api, identity), false);
});

test('accepts any enabled duplicate launch item instead of trusting array order', () => {
  const identity = createWindowsStartupIdentity('C:\\Apps\\CodexU.exe');
  const api = {
    setLoginItemSettings() {},
    getLoginItemSettings: () => ({
      openAtLogin: true,
      executableWillLaunchAtLogin: true,
      launchItems: [
        { ...identity, enabled: false },
        { ...identity, enabled: true },
      ],
    }),
  };

  assert.equal(readWindowsStartupRegistration(api, identity), true);
});

test('rejects another registry name, executable, or argument list', () => {
  const identity = createWindowsStartupIdentity('C:\\Apps\\CodexU.exe');
  const state = {
    openAtLogin: true,
    executableWillLaunchAtLogin: true,
    launchItems: [{ name: 'another-app', path: identity.path, args: [], enabled: true }],
  };
  const api = {
    setLoginItemSettings() {},
    getLoginItemSettings: () => state,
  };

  assert.equal(readWindowsStartupRegistration(api, identity), false);
  state.launchItems[0] = { name: identity.name, path: 'C:\\Apps\\Other.exe', args: [], enabled: true };
  assert.equal(readWindowsStartupRegistration(api, identity), false);
  state.launchItems[0] = { name: identity.name, path: identity.path, args: ['--hidden'], enabled: true };
  assert.equal(readWindowsStartupRegistration(api, identity), false);
});

test('writes the exact identity then returns the effective state read back', () => {
  const identity = createWindowsStartupIdentity('C:\\Apps\\CodexU.exe');
  const writes = [];
  let enabled = false;
  const api = {
    setLoginItemSettings(options) {
      writes.push(options);
      enabled = options.enabled;
    },
    getLoginItemSettings() {
      return {
        openAtLogin: enabled,
        executableWillLaunchAtLogin: enabled,
        launchItems: [{ ...identity, enabled }],
      };
    },
  };

  assert.equal(applyWindowsStartupRegistration(api, identity, true), true);
  assert.deepEqual(writes, [{
    openAtLogin: true,
    enabled: true,
    name: WINDOWS_LOGIN_ITEM_NAME,
    path: identity.path,
    args: [],
  }]);
});
