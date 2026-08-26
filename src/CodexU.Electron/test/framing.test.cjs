const assert = require('node:assert/strict');
const test = require('node:test');
const {
  LengthPrefixedJsonDecoder,
  SidecarProtocolError,
  encodeFrame,
} = require('../dist/sidecar/framing.js');

test('round-trips a frame split across arbitrary chunks', () => {
  const frame = encodeFrame({ version: 1, type: 'handshake', backendVersion: 'test' });
  const decoder = new LengthPrefixedJsonDecoder();
  assert.deepEqual(decoder.push(frame.subarray(0, 2)), []);
  assert.deepEqual(decoder.push(frame.subarray(2, 9)), []);
  assert.deepEqual(decoder.push(frame.subarray(9)), [
    { version: 1, type: 'handshake', backendVersion: 'test' },
  ]);
});

test('decodes multiple frames from one chunk', () => {
  const decoder = new LengthPrefixedJsonDecoder();
  const frames = Buffer.concat([
    encodeFrame({ version: 1, type: 'event', method: 'first' }),
    encodeFrame({ version: 1, type: 'event', method: 'second' }),
  ]);
  assert.equal(decoder.push(frames).length, 2);
});

test('rejects a zero-length frame', () => {
  const decoder = new LengthPrefixedJsonDecoder();
  assert.throws(() => decoder.push(Buffer.alloc(4)), SidecarProtocolError);
});

test('rejects a declared frame above the configured UTF-8 byte limit', () => {
  const decoder = new LengthPrefixedJsonDecoder(16);
  const header = Buffer.alloc(4);
  header.writeUInt32LE(17);
  assert.throws(() => decoder.push(header), SidecarProtocolError);
});

test('rejects malformed JSON', () => {
  const decoder = new LengthPrefixedJsonDecoder();
  const payload = Buffer.from('{nope', 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(payload.length);
  assert.throws(() => decoder.push(Buffer.concat([header, payload])), SidecarProtocolError);
});

test('rejects invalid UTF-8 rather than replacing invalid bytes', () => {
  const decoder = new LengthPrefixedJsonDecoder();
  const payload = Buffer.from([0xc3, 0x28]);
  const header = Buffer.alloc(4);
  header.writeUInt32LE(payload.length);
  assert.throws(() => decoder.push(Buffer.concat([header, payload])), SidecarProtocolError);
});
