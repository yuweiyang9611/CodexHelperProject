const assert = require('node:assert/strict');
const test = require('node:test');
const {
  SUPPORTED_GLOBAL_HOT_KEYS,
  nativeActivationAction,
  parseHostSettings,
  shouldHideWindowOnClose,
  shouldSuppressHostEventInSmoke,
  shouldApplyStartupRegistration,
  windowLayout,
} = require('../dist/hostSettings.js');

function validSettings(overrides = {}) {
  return {
    closeToTray: true,
    compactMode: false,
    theme: 'system',
    startAtLogin: false,
    globalHotKey: 'Ctrl+U',
    notificationsEnabled: true,
    ...overrides,
  };
}

test('hydrates only the five validated native-host settings', () => {
  assert.deepEqual(parseHostSettings(validSettings()), {
    closeToTray: true,
    compactMode: false,
    theme: 'system',
    startAtLogin: false,
    globalHotKey: 'Ctrl+U',
  });
});

test('accepts exactly the themes and five global shortcuts supported by Core', () => {
  assert.deepEqual(SUPPORTED_GLOBAL_HOT_KEYS, [
    'Ctrl+U',
    'Ctrl+Shift+U',
    'Ctrl+Alt+U',
    'Ctrl+Shift+C',
    'Ctrl+Alt+C',
  ]);

  for (const theme of ['light', 'dark', 'system']) {
    assert.equal(parseHostSettings(validSettings({ theme })).theme, theme);
  }
  for (const globalHotKey of SUPPORTED_GLOBAL_HOT_KEYS) {
    assert.equal(
      parseHostSettings(validSettings({ globalHotKey })).globalHotKey,
      globalHotKey,
    );
  }
});

test('rejects missing, mistyped, and unsupported native-host settings', () => {
  const invalidValues = [
    null,
    [],
    validSettings({ closeToTray: 'yes' }),
    validSettings({ compactMode: 1 }),
    validSettings({ theme: 'sepia' }),
    validSettings({ startAtLogin: null }),
    validSettings({ globalHotKey: 'Ctrl+Alt+Delete' }),
  ];

  for (const value of invalidValues) {
    assert.throws(() => parseHostSettings(value), TypeError);
  }

  const missing = validSettings();
  delete missing.globalHotKey;
  assert.throws(() => parseHostSettings(missing), /globalHotKey/u);
});

test('provides the expanded and compact window dimensions', () => {
  assert.deepEqual(windowLayout(false), {
    width: 1180,
    height: 780,
    minimumWidth: 900,
    minimumHeight: 620,
  });
  assert.deepEqual(windowLayout(true), {
    width: 940,
    height: 420,
    minimumWidth: 640,
    minimumHeight: 380,
  });
});

test('hides on close only when a usable tray exists and shutdown has not started', () => {
  const closeToTray = parseHostSettings(validSettings({ closeToTray: true }));
  const exitOnClose = parseHostSettings(validSettings({ closeToTray: false }));

  assert.equal(shouldHideWindowOnClose(closeToTray, false, true), true);
  assert.equal(shouldHideWindowOnClose(closeToTray, true, true), false);
  assert.equal(shouldHideWindowOnClose(closeToTray, false, false), false);
  assert.equal(shouldHideWindowOnClose(exitOnClose, false, true), false);
});

test('applies startup registration only for non-smoke packaged Windows hosts', () => {
  assert.equal(shouldApplyStartupRegistration('win32', true, false), true);
  assert.equal(shouldApplyStartupRegistration('win32', true, true), false);
  assert.equal(shouldApplyStartupRegistration('linux', true, false), false);
  assert.equal(shouldApplyStartupRegistration('win32', false, false), false);
});

test('smoke suppresses every native host event except its web-ready barrier', () => {
  for (const method of [
    'host.openExternal',
    'host.window.show',
    'host.window.activate',
    'host.window.hide',
    'host.window.setAlwaysOnTop',
    'host.startupRegistrationRequested',
    'host.notification.show',
    'settings.changed',
  ]) {
    assert.equal(shouldSuppressHostEventInSmoke(true, method), true, method);
    assert.equal(shouldSuppressHostEventInSmoke(false, method), false, method);
  }

  assert.equal(shouldSuppressHostEventInSmoke(true, 'host.webReady'), false);
  assert.equal(shouldSuppressHostEventInSmoke(true, 'usage.snapshotChanged'), false);
});

test('smoke never shows a window for lifecycle activation', () => {
  assert.equal(nativeActivationAction(true, 'second-instance'), 'fail');
  assert.equal(nativeActivationAction(true, 'activate'), 'ignore');
  assert.equal(nativeActivationAction(false, 'second-instance'), 'show');
  assert.equal(nativeActivationAction(false, 'activate'), 'show');
});
