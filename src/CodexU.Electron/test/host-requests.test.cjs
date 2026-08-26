const assert = require('node:assert/strict');
const test = require('node:test');
const {
  HostRequestHandlerError,
  createHostRequestHandler,
} = require('../dist/hostRequests.js');

function filePayload(overrides = {}) {
  return {
    title: 'Export data',
    suggestedFileName: 'codexu-export',
    defaultExtension: '.json',
    fileTypes: [{ name: 'JSON files', patterns: ['*.json'] }],
    checkFileExists: false,
    overwritePrompt: true,
    ...overrides,
  };
}

function confirmationPayload(overrides = {}) {
  return {
    title: 'Confirm action',
    message: 'Do you want to continue?',
    isWarning: true,
    ...overrides,
  };
}

function hostRequest(method, payload) {
  return { version: 1, id: 'host-test', type: 'hostRequest', method, payload };
}

function createDialog(overrides = {}) {
  return {
    showSaveDialog: async () => ({ canceled: true, filePath: undefined }),
    showOpenDialog: async () => ({ canceled: true, filePaths: [] }),
    showMessageBox: async () => ({ response: 0, checkboxChecked: false }),
    ...overrides,
  };
}

const owner = { isDestroyed: () => false };

test('maps a validated save request to Electron filters and returns the selected path directly', async () => {
  let receivedOptions;
  const dialog = createDialog({
    showSaveDialog: async (_owner, options) => {
      receivedOptions = options;
      return { canceled: false, filePath: 'C:\\exports\\codexu-export.json' };
    },
  });
  const handler = createHostRequestHandler(dialog, () => owner, { platform: 'win32' });

  const result = await handler(hostRequest('host.dialog.saveFile', filePayload()));

  assert.equal(result, 'C:\\exports\\codexu-export.json');
  assert.deepEqual(receivedOptions, {
    title: 'Export data',
    defaultPath: 'codexu-export.json',
    filters: [{ name: 'JSON files', extensions: ['json'] }],
    properties: [],
  });
});

test('returns safe direct cancellation values without showing dialogs in smoke mode', async () => {
  let callCount = 0;
  const dialog = createDialog({
    showSaveDialog: async () => {
      callCount += 1;
      return { canceled: false, filePath: 'unexpected' };
    },
    showMessageBox: async () => {
      callCount += 1;
      return { response: 1, checkboxChecked: false };
    },
  });
  const handler = createHostRequestHandler(dialog, () => owner, {
    forceSafeCancellation: true,
  });

  assert.equal(
    await handler(hostRequest('host.dialog.saveFile', filePayload())),
    null,
  );
  assert.equal(
    await handler(hostRequest('host.dialog.confirm', confirmationPayload())),
    false,
  );
  assert.equal(callCount, 0);
});

test('uses cancel as the default confirmation and confirms only the explicit second button', async () => {
  const optionsSeen = [];
  let response = 0;
  const dialog = createDialog({
    showMessageBox: async (_owner, options) => {
      optionsSeen.push(options);
      return { response, checkboxChecked: false };
    },
  });
  const handler = createHostRequestHandler(dialog, () => owner);
  const request = hostRequest('host.dialog.confirm', confirmationPayload());

  assert.equal(await handler(request), false);
  response = 1;
  assert.equal(await handler(request), true);
  assert.equal(optionsSeen[0].defaultId, 0);
  assert.equal(optionsSeen[0].cancelId, 0);
  assert.deepEqual(optionsSeen[0].buttons, ['取消', '确定']);
  assert.equal(optionsSeen[0].type, 'warning');
});

test('returns null for open cancellation, missing owners, and ambiguous selections', async () => {
  let result = { canceled: true, filePaths: [] };
  const dialog = createDialog({ showOpenDialog: async () => result });
  const request = hostRequest(
    'host.dialog.openFile',
    filePayload({ suggestedFileName: '', checkFileExists: true, overwritePrompt: false }),
  );
  const handler = createHostRequestHandler(dialog, () => owner);

  assert.equal(await handler(request), null);
  result = { canceled: false, filePaths: ['C:\\one.json', 'C:\\two.json'] };
  assert.equal(await handler(request), null);

  const noOwnerHandler = createHostRequestHandler(dialog, () => undefined);
  result = { canceled: false, filePaths: ['C:\\one.json'] };
  assert.equal(await noOwnerHandler(request), null);
});

test('strictly rejects unsafe or oversized dialog payloads', async () => {
  const handler = createHostRequestHandler(createDialog(), () => owner);
  const invalidPayloads = [
    filePayload({ title: '' }),
    filePayload({ title: 'x'.repeat(201) }),
    filePayload({ suggestedFileName: '..\\secret.json' }),
    filePayload({ defaultExtension: '../exe' }),
    filePayload({ fileTypes: [{ name: 'Executables', patterns: ['*.exe'] }] }),
    filePayload({ unexpected: true }),
  ];

  for (const payload of invalidPayloads) {
    await assert.rejects(
      () => handler(hostRequest('host.dialog.saveFile', payload)),
      (error) => error instanceof HostRequestHandlerError
        && error.code === 'invalid_host_request',
    );
  }

  await assert.rejects(
    () => handler(hostRequest(
      'host.dialog.confirm',
      confirmationPayload({ message: 'x'.repeat(4_097) }),
    )),
    (error) => error instanceof HostRequestHandlerError
      && error.code === 'invalid_host_request',
  );
});

test('allows only one real native dialog at a time and reports host_busy', async () => {
  let releaseFirst;
  const firstDialog = new Promise((resolve) => {
    releaseFirst = resolve;
  });
  const dialog = createDialog({
    showMessageBox: async () => firstDialog,
  });
  const handler = createHostRequestHandler(dialog, () => owner);
  const request = hostRequest('host.dialog.confirm', confirmationPayload());

  const first = handler(request);
  await assert.rejects(
    () => handler({ ...request, id: 'host-second' }),
    (error) => error instanceof HostRequestHandlerError && error.code === 'host_busy',
  );
  releaseFirst({ response: 0, checkboxChecked: false });
  assert.equal(await first, false);
});

test('uses Electron\'s Linux overwrite property only on Linux', async () => {
  const propertiesSeen = [];
  const dialog = createDialog({
    showSaveDialog: async (_owner, options) => {
      propertiesSeen.push(options.properties);
      return { canceled: true, filePath: undefined };
    },
  });
  const windowsHandler = createHostRequestHandler(dialog, () => owner, { platform: 'win32' });
  const linuxHandler = createHostRequestHandler(dialog, () => owner, { platform: 'linux' });
  const request = hostRequest('host.dialog.saveFile', filePayload());

  await windowsHandler(request);
  await linuxHandler(request);
  assert.deepEqual(propertiesSeen, [[], ['showOverwriteConfirmation']]);
});
