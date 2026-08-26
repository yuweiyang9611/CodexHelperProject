const noHandshake = process.argv.includes('--no-handshake');
let buffered = Buffer.alloc(0);
let hostSequence = 0;
const pendingHostRequests = new Map();

function encode(message) {
  const payload = Buffer.from(JSON.stringify(message), 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(payload.length);
  return Buffer.concat([header, payload]);
}

function send(message, callback) {
  process.stdout.write(encode(message), callback);
}

if (!noHandshake) {
  send({
    version: 1,
    type: 'handshake',
    protocolVersion: 1,
    backendVersion: 'fake-1.0.0',
    capabilities: ['ipc.request.v1', 'host.rpc.v1', 'host.state.v1', 'gracefulShutdown'],
  });
  process.stderr.write('fake sidecar ready\n');
}

process.stdin.on('data', (chunk) => {
  buffered = buffered.length === 0 ? Buffer.from(chunk) : Buffer.concat([buffered, chunk]);
  while (buffered.length >= 4) {
    const payloadLength = buffered.readUInt32LE(0);
    if (buffered.length < payloadLength + 4) return;
    const payload = buffered.subarray(4, payloadLength + 4);
    buffered = buffered.subarray(payloadLength + 4);
    const message = JSON.parse(payload.toString('utf8'));

    if (message.type === 'hostResponse') {
      const rendererRequestId = pendingHostRequests.get(message.id);
      if (!rendererRequestId) continue;
      pendingHostRequests.delete(message.id);
      process.stderr.write(`host response ${JSON.stringify(message)}\n`);
      send({
        version: 1,
        id: rendererRequestId,
        type: 'response',
        ok: true,
        payload: message,
      });
      continue;
    }

    if (message.type === 'hostState') {
      send({
        version: 1,
        type: 'event',
        method: 'test.hostStateReceived',
        payload: message,
      });
      continue;
    }

    if (message.type === 'control' && message.method === 'shutdown') {
      send({ version: 1, type: 'control', method: 'shutdownAck' }, () => process.exit(0));
      return;
    }

    if (message.type !== 'request' || message.method === 'never') continue;
    if (message.method.startsWith('host-')) {
      const hostRequestId = `host-${++hostSequence}`;
      pendingHostRequests.set(hostRequestId, message.id);
      if (message.method === 'host-malformed') {
        send({
          version: 1,
          id: hostRequestId,
          type: 'hostRequest',
          method: 'host.dialog.unknown',
          payload: {},
        });
        continue;
      }

      const isFileDialog = message.method === 'host-save' || message.method === 'host-cancel';
      send({
        version: 1,
        id: hostRequestId,
        type: 'hostRequest',
        method: isFileDialog ? 'host.dialog.saveFile' : 'host.dialog.confirm',
        payload: isFileDialog
          ? {
              title: 'Save test data',
              suggestedFileName: 'test.json',
              defaultExtension: '.json',
              fileTypes: [{ name: 'JSON', patterns: ['*.json'] }],
              checkFileExists: false,
              overwritePrompt: true,
            }
          : { title: 'Confirm test', message: 'Continue?', isWarning: true },
      });
      continue;
    }
    if (message.method === 'emit') {
      send({
        version: 1,
        type: 'event',
        method: 'usage.snapshotChanged',
        payload: { sequence: 1 },
      });
    }
    send({
      version: 1,
      id: message.id,
      type: 'response',
      ok: true,
      payload: { method: message.method, received: message.payload },
    });
  }
});

process.stdin.resume();
