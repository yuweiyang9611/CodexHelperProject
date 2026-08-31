const assert = require('node:assert/strict');
const test = require('node:test');
const {
  SUPPORTED_GLOBAL_HOT_KEYS,
  nativeActivationAction,
  parseHostSettings,
  shouldEnableNativeDesktopFeatures,
  shouldHideWindowOnClose,
  shouldSuppressHostEventInSmoke,
  shouldApplyStartupRegistration,
  windowLayout,
} = require('../dist/hostSettings.js');
const {
  createWindowState,
  equalWindowBounds,
  fitWindowBoundsToWorkArea,
  fitWindowSizeToWorkArea,
  parseWindowState,
  restoreWindowState,
} = require('../dist/windowState.js');

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

test('enables tray and global hotkey only for non-smoke Windows hosts', () => {
  assert.equal(shouldEnableNativeDesktopFeatures('win32', false), true);
  assert.equal(shouldEnableNativeDesktopFeatures('win32', true), false);
  assert.equal(shouldEnableNativeDesktopFeatures('linux', false), false);
  assert.equal(shouldEnableNativeDesktopFeatures('darwin', false), false);
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

test('restores a saved window on its display and clamps it into the work area', () => {
  const primary = {
    id: '1',
    scaleFactor: 1,
    workArea: { x: 0, y: 0, width: 1920, height: 1040 },
  };
  const secondary = {
    id: '2',
    scaleFactor: 1.5,
    workArea: { x: 1920, y: 0, width: 1280, height: 960 },
  };
  const saved = createWindowState(
    { x: 3100, y: -200, width: 1180, height: 780 },
    secondary,
    true,
  );

  assert.deepEqual(restoreWindowState(
    saved,
    [primary, secondary],
    { x: 0, y: 0, width: 1180, height: 780 },
    { width: 900, height: 620 },
  ), {
    bounds: { x: 2020, y: 0, width: 1180, height: 780 },
    maximized: true,
  });
});

test('recovers an off-screen window to the primary display when a monitor is removed', () => {
  const oldDisplay = {
    id: 'removed',
    scaleFactor: 2,
    workArea: { x: -2560, y: 0, width: 2560, height: 1400 },
  };
  const primary = {
    id: 'primary',
    scaleFactor: 1.25,
    workArea: { x: 0, y: 0, width: 1536, height: 824 },
  };
  const saved = createWindowState(
    { x: -2200, y: 100, width: 1180, height: 780 },
    oldDisplay,
    false,
  );

  assert.deepEqual(restoreWindowState(
    saved,
    [primary],
    { x: 0, y: 0, width: 1180, height: 780 },
    { width: 900, height: 620 },
  ), {
    bounds: { x: 0, y: 44, width: 1180, height: 780 },
    maximized: false,
  });
});

test('fits minimum and desired sizes into a high-DPI display work area', () => {
  const workArea = { x: 0, y: 0, width: 960, height: 540 };
  assert.deepEqual(
    fitWindowSizeToWorkArea({ width: 900, height: 620 }, workArea),
    { width: 900, height: 540 },
  );
  assert.deepEqual(
    fitWindowSizeToWorkArea({ width: 1180, height: 780 }, workArea),
    { width: 960, height: 540 },
  );
  assert.deepEqual(
    fitWindowBoundsToWorkArea({ x: 800, y: 400, width: 1180, height: 780 }, workArea),
    { x: 0, y: 0, width: 960, height: 540 },
  );
  assert.equal(
    equalWindowBounds(
      { x: 0, y: 0, width: 960, height: 540 },
      { x: 0, y: 0, width: 960, height: 540 },
    ),
    true,
  );
});

test('keeps the first compact-to-expanded resize inside the current work area', () => {
  const workArea = { x: 0, y: 0, width: 1200, height: 800 };
  const compactBounds = { x: 760, y: 360, width: 440, height: 440 };
  assert.deepEqual(
    fitWindowBoundsToWorkArea({ ...compactBounds, width: 1000, height: 700 }, workArea),
    { x: 200, y: 100, width: 1000, height: 700 },
  );
});

test('rejects malformed or unsupported window-state files', () => {
  assert.equal(parseWindowState(null), undefined);
  assert.equal(parseWindowState({ schemaVersion: 2 }), undefined);
  assert.equal(parseWindowState({
    schemaVersion: 1,
    bounds: { x: 0, y: 0, width: Number.NaN, height: 780 },
    display: {
      id: '1',
      scaleFactor: 1,
      workArea: { x: 0, y: 0, width: 1920, height: 1040 },
    },
    maximized: false,
  }), undefined);
});
