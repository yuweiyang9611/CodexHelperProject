import { MAX_FRAME_BYTES, isRecord, type JsonObject } from '../protocol';
import { TextDecoder } from 'node:util';

const HEADER_BYTES = 4;
const UTF8_DECODER = new TextDecoder('utf-8', { fatal: true });

export class SidecarProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SidecarProtocolError';
  }
}

export function encodeFrame(
  message: object,
  maxFrameBytes: number = MAX_FRAME_BYTES,
): Buffer {
  const json = JSON.stringify(message);
  if (json === undefined) {
    throw new SidecarProtocolError('Sidecar frame must contain a JSON object.');
  }
  const payload = Buffer.from(json, 'utf8');
  if (payload.length === 0) {
    throw new SidecarProtocolError('Sidecar frames cannot be empty.');
  }
  if (payload.length > maxFrameBytes) {
    throw new SidecarProtocolError(
      `Sidecar frame is ${payload.length} bytes; maximum is ${maxFrameBytes}.`,
    );
  }

  const header = Buffer.allocUnsafe(HEADER_BYTES);
  header.writeUInt32LE(payload.length, 0);
  return Buffer.concat([header, payload], HEADER_BYTES + payload.length);
}

export class LengthPrefixedJsonDecoder {
  private buffer = Buffer.alloc(0);
  private expectedPayloadBytes: number | undefined;

  constructor(private readonly maxFrameBytes: number = MAX_FRAME_BYTES) {}

  push(chunk: Buffer): JsonObject[] {
    if (chunk.length === 0) return [];

    this.buffer = this.buffer.length === 0
      ? Buffer.from(chunk)
      : Buffer.concat([this.buffer, chunk]);

    const messages: JsonObject[] = [];
    while (true) {
      if (this.expectedPayloadBytes === undefined) {
        if (this.buffer.length < HEADER_BYTES) break;

        const payloadBytes = this.buffer.readUInt32LE(0);
        this.buffer = this.buffer.subarray(HEADER_BYTES);
        if (payloadBytes === 0) {
          throw new SidecarProtocolError('Sidecar sent a zero-length frame.');
        }
        if (payloadBytes > this.maxFrameBytes) {
          throw new SidecarProtocolError(
            `Sidecar declared a ${payloadBytes}-byte frame; maximum is ${this.maxFrameBytes}.`,
          );
        }
        this.expectedPayloadBytes = payloadBytes;
      }

      if (this.buffer.length < this.expectedPayloadBytes) break;

      const payload = this.buffer.subarray(0, this.expectedPayloadBytes);
      this.buffer = this.buffer.subarray(this.expectedPayloadBytes);
      this.expectedPayloadBytes = undefined;

      let json: string;
      try {
        json = UTF8_DECODER.decode(payload);
      } catch {
        throw new SidecarProtocolError('Sidecar sent invalid UTF-8.');
      }

      let parsed: unknown;
      try {
        parsed = JSON.parse(json);
      } catch {
        throw new SidecarProtocolError('Sidecar sent malformed JSON.');
      }

      if (!isRecord(parsed)) {
        throw new SidecarProtocolError('Sidecar frame must contain a JSON object.');
      }
      messages.push(parsed);
    }

    return messages;
  }
}
