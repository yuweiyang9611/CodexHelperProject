import { randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import {
  IPC_VERSION,
  MAX_FRAME_BYTES,
  SIDECAR_PROTOCOL_VERSION,
  isHostRequestMethod,
  isRecord,
  type JsonObject,
  type SidecarEvent,
  type SidecarHandshake,
  type SidecarHostRequest,
  type SidecarHostResponse,
  type SidecarHostState,
  type SidecarRequest,
} from '../protocol';
import { encodeFrame, LengthPrefixedJsonDecoder, SidecarProtocolError } from './framing';

const DEFAULT_HANDSHAKE_TIMEOUT_MS = 10_000;
const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;
const DEFAULT_SHUTDOWN_TIMEOUT_MS = 5_000;

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: Error) => void;
}

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: NodeJS.Timeout;
}

interface ActiveHostRequest {
  request: SidecarHostRequest;
  completed: boolean;
}

export interface SidecarClientOptions {
  executablePath: string;
  arguments?: string[];
  cwd?: string;
  environment?: NodeJS.ProcessEnv;
  handshakeTimeoutMs?: number;
  requestTimeoutMs?: number;
  maxFrameBytes?: number;
  hostRequestHandler?: (request: SidecarHostRequest) => Promise<unknown> | unknown;
}

export interface SidecarExit {
  code: number | null;
  signal: NodeJS.Signals | null;
  expected: boolean;
}

export class SidecarRequestError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'SidecarRequestError';
  }
}

export class SidecarClient extends EventEmitter {
  private readonly decoder: LengthPrefixedJsonDecoder;
  private readonly pending = new Map<string, PendingRequest>();
  private readonly activeHostRequests = new Map<string, ActiveHostRequest>();
  private readonly handshakeTimeoutMs: number;
  private readonly requestTimeoutMs: number;
  private readonly maxFrameBytes: number;
  private child: ChildProcessWithoutNullStreams | undefined;
  private handshake: Deferred<SidecarHandshake> | undefined;
  private closeSignal: Deferred<void> | undefined;
  private shutdownAck: Deferred<void> | undefined;
  private handshakeTimer: NodeJS.Timeout | undefined;
  private shutdownOperation: Promise<void> | undefined;
  private state: 'idle' | 'starting' | 'ready' | 'stopping' | 'closed' = 'idle';
  private receivedHandshake = false;

  constructor(private readonly options: SidecarClientOptions) {
    super();
    this.handshakeTimeoutMs = options.handshakeTimeoutMs ?? DEFAULT_HANDSHAKE_TIMEOUT_MS;
    this.requestTimeoutMs = options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
    this.maxFrameBytes = options.maxFrameBytes ?? MAX_FRAME_BYTES;
    this.decoder = new LengthPrefixedJsonDecoder(this.maxFrameBytes);
  }

  async start(): Promise<SidecarHandshake> {
    if (this.state !== 'idle') {
      throw new Error(`Sidecar cannot start while in state '${this.state}'.`);
    }

    this.state = 'starting';
    this.handshake = createDeferred<SidecarHandshake>();
    this.closeSignal = createDeferred<void>();

    try {
      this.child = spawn(this.options.executablePath, this.options.arguments ?? [], {
        cwd: this.options.cwd,
        env: this.options.environment ?? process.env,
        shell: false,
        detached: false,
        windowsHide: true,
        stdio: ['pipe', 'pipe', 'pipe'],
      });
    } catch (reason) {
      const error = toError(reason, 'Failed to spawn sidecar.');
      this.state = 'closed';
      throw error;
    }

    this.child.stdout.on('data', (chunk: Buffer) => this.handleStdout(chunk));
    this.child.stderr.setEncoding('utf8');
    this.child.stderr.on('data', (chunk: string) => this.emit('stderr', chunk));
    this.child.on('error', (reason) => this.handleProcessError(reason));
    this.child.on('close', (code, signal) => this.handleProcessClose(code, signal));

    this.handshakeTimer = setTimeout(() => {
      this.failProtocol(new SidecarProtocolError(
        `Sidecar handshake timed out after ${this.handshakeTimeoutMs} ms.`,
      ));
    }, this.handshakeTimeoutMs);

    return this.handshake.promise;
  }

  request(
    method: string,
    payload: JsonObject = {},
    timeoutMs: number = this.requestTimeoutMs,
  ): Promise<unknown> {
    if (this.state !== 'ready' || !this.child) {
      return Promise.reject(new Error('Sidecar is not ready.'));
    }
    if (timeoutMs <= 0 || !Number.isFinite(timeoutMs)) {
      return Promise.reject(new RangeError('Sidecar request timeout must be a positive number.'));
    }

    const id = randomUUID();
    const request: SidecarRequest = {
      version: IPC_VERSION,
      id,
      type: 'request',
      method,
      payload,
    };

    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Sidecar request timed out: ${method}`));
        this.emitIdleIfNeeded();
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer });
      void this.write(request).catch((reason) => {
        const pending = this.pending.get(id);
        if (!pending) return;
        clearTimeout(pending.timer);
        this.pending.delete(id);
        pending.reject(toError(reason, `Failed to send sidecar request: ${method}`));
        this.emitIdleIfNeeded();
      });
    });
  }

  sendHostState(globalHotKeyRegistered: boolean): Promise<void> {
    if (this.state !== 'ready' || !this.child) {
      return Promise.reject(new Error('Sidecar is not ready.'));
    }
    if (typeof globalHotKeyRegistered !== 'boolean') {
      return Promise.reject(new TypeError('globalHotKeyRegistered must be a boolean.'));
    }

    const state: SidecarHostState = {
      version: IPC_VERSION,
      type: 'hostState',
      globalHotKeyRegistered,
    };
    return this.write(state);
  }

  waitForIdle(timeoutMs: number = this.requestTimeoutMs): Promise<void> {
    if (this.isIdle()) return Promise.resolve();
    if (timeoutMs <= 0 || !Number.isFinite(timeoutMs)) {
      return Promise.reject(new RangeError('Sidecar idle timeout must be a positive number.'));
    }

    return new Promise<void>((resolve, reject) => {
      const onIdle = (): void => {
        clearTimeout(timer);
        this.off('idle', onIdle);
        resolve();
      };
      const timer = setTimeout(() => {
        this.off('idle', onIdle);
        reject(new Error(`Sidecar still has pending requests after ${timeoutMs} ms.`));
      }, timeoutMs);
      this.on('idle', onIdle);
      if (this.isIdle()) onIdle();
    });
  }

  shutdown(timeoutMs: number = DEFAULT_SHUTDOWN_TIMEOUT_MS): Promise<void> {
    if (timeoutMs <= 0 || !Number.isFinite(timeoutMs)) {
      return Promise.reject(new RangeError('Sidecar shutdown timeout must be a positive number.'));
    }
    if (this.state === 'idle' || this.state === 'closed' || !this.child) {
      return Promise.resolve();
    }
    if (this.shutdownOperation) return this.shutdownOperation;

    const deadlineMs = Date.now() + timeoutMs;
    const operation = this.shutdownCore(deadlineMs, timeoutMs);
    const tracked = operation.then(
      () => {
        if (this.shutdownOperation === tracked) this.shutdownOperation = undefined;
      },
      (reason: unknown) => {
        if (this.shutdownOperation === tracked) this.shutdownOperation = undefined;
        throw reason;
      },
    );
    this.shutdownOperation = tracked;
    return tracked;
  }

  private async shutdownCore(deadlineMs: number, timeoutMs: number): Promise<void> {
    if (this.state === 'idle' || this.state === 'closed' || !this.child) return;
    const child = this.child;

    if (this.state !== 'stopping') {
      this.state = 'stopping';
      this.clearHandshakeTimer();
      this.rejectPending(new Error('Sidecar is shutting down.'));
      this.shutdownAck = createDeferred<void>();

      try {
        await settleBeforeDeadline(
          this.sendGracefulShutdownSequence(),
          deadlineMs,
          Math.max(0, timeoutMs - 1_000),
        );
      } catch {
        // The process may already have closed. The close path below is authoritative.
      }
    }

    if (!child.stdin.destroyed) {
      try {
        child.stdin.end();
      } catch {
        // Cleanup must still reach forced termination when stream teardown throws.
      }
    }

    if (!this.isClosed()) {
      await settleBeforeDeadline(
        this.closeSignal?.promise ?? Promise.resolve(),
        deadlineMs,
        250,
      );
    }

    if (!this.isClosed()) {
      try {
        // SIGKILL is also accepted by Node on Windows and gives repeated shutdown
        // attempts a real termination action even after child.killed became true.
        child.kill('SIGKILL');
      } catch {
        // A failed termination signal must not extend the caller's deadline.
      }
    }

    if (!this.isClosed()) {
      await settleBeforeDeadline(
        this.closeSignal?.promise ?? Promise.resolve(),
        deadlineMs,
      );
    }

    if (!this.isClosed()) {
      throw new Error(`Sidecar did not exit within the ${timeoutMs} ms shutdown deadline.`);
    }
  }

  private async sendGracefulShutdownSequence(): Promise<void> {
    await this.cancelActiveHostRequests();
    await this.write({ version: IPC_VERSION, type: 'control', method: 'shutdown' });
    await Promise.race([
      this.shutdownAck?.promise ?? Promise.resolve(),
      this.closeSignal?.promise ?? Promise.resolve(),
    ]);
  }

  private async write(message: object): Promise<void> {
    const child = this.child;
    if (!child || child.stdin.destroyed || this.state === 'closed') {
      throw new Error('Sidecar input is not available.');
    }

    const frame = encodeFrame(message, this.maxFrameBytes);
    await new Promise<void>((resolve, reject) => {
      child.stdin.write(frame, (reason) => {
        if (reason) reject(reason);
        else resolve();
      });
    });
  }

  private handleStdout(chunk: Buffer): void {
    try {
      for (const message of this.decoder.push(chunk)) {
        this.handleMessage(message);
      }
    } catch (reason) {
      this.failProtocol(toError(reason, 'Sidecar protocol failure.'));
    }
  }

  private handleMessage(message: JsonObject): void {
    if (message.version !== IPC_VERSION || typeof message.type !== 'string') {
      throw new SidecarProtocolError('Sidecar sent an unsupported message envelope.');
    }

    if (!this.receivedHandshake) {
      const handshake = parseHandshake(message);
      this.receivedHandshake = true;
      this.state = 'ready';
      this.clearHandshakeTimer();
      this.handshake?.resolve(handshake);
      this.emit('handshake', handshake);
      return;
    }

    switch (message.type) {
      case 'response':
        this.handleResponse(message);
        return;
      case 'event':
        this.handleEvent(message);
        return;
      case 'hostRequest':
        this.handleHostRequest(message);
        return;
      case 'control':
        if (message.method !== 'shutdownAck') {
          throw new SidecarProtocolError('Sidecar sent an unknown control message.');
        }
        this.shutdownAck?.resolve(undefined);
        return;
      default:
        throw new SidecarProtocolError(`Unexpected sidecar message type: ${message.type}`);
    }
  }

  private handleResponse(message: JsonObject): void {
    if (typeof message.id !== 'string' || message.id.length === 0 || typeof message.ok !== 'boolean') {
      throw new SidecarProtocolError('Sidecar sent an invalid response.');
    }

    const pending = this.pending.get(message.id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending.delete(message.id);

    if (message.ok) {
      pending.resolve(message.payload);
      this.emitIdleIfNeeded();
      return;
    }

    const error = isRecord(message.error) ? message.error : undefined;
    const code = typeof error?.code === 'string' ? error.code : 'sidecar_error';
    const detail = typeof error?.message === 'string' ? error.message : 'Sidecar request failed.';
    pending.reject(new SidecarRequestError(code, detail));
    this.emitIdleIfNeeded();
  }

  private handleEvent(message: JsonObject): void {
    if (typeof message.method !== 'string' || message.method.length === 0) {
      throw new SidecarProtocolError('Sidecar sent an invalid event.');
    }

    const event: SidecarEvent = {
      version: IPC_VERSION,
      type: 'event',
      method: message.method,
      payload: message.payload,
    };
    this.emit('event', event);
  }

  private handleHostRequest(message: JsonObject): void {
    const request = parseHostRequest(message);
    if (this.activeHostRequests.has(request.id)) {
      throw new SidecarProtocolError(`Sidecar reused an active host request ID: ${request.id}`);
    }

    const active: ActiveHostRequest = { request, completed: false };
    this.activeHostRequests.set(request.id, active);
    void this.processHostRequest(active);
  }

  private async processHostRequest(active: ActiveHostRequest): Promise<void> {
    let response: SidecarHostResponse;
    try {
      if (this.state !== 'ready') {
        response = hostFailureResponse(
          active.request.id,
          'host_shutting_down',
          'The Electron host is shutting down.',
        );
      } else if (!this.options.hostRequestHandler) {
        response = hostFailureResponse(
          active.request.id,
          'host_unavailable',
          'The Electron host request handler is unavailable.',
        );
      } else {
        const payload = await this.options.hostRequestHandler(active.request);
        validateHostResponsePayload(active.request.method, payload);
        response = {
          version: IPC_VERSION,
          id: active.request.id,
          type: 'hostResponse',
          ok: true,
          payload,
        };
      }
    } catch (reason) {
      const error = toHostResponseError(reason);
      response = hostFailureResponse(active.request.id, error.code, error.message);
    }

    try {
      await this.completeHostRequest(active, response);
    } catch (reason) {
      this.failProtocol(toError(reason, 'Failed to send a host response to the sidecar.'));
    }
  }

  private async completeHostRequest(
    active: ActiveHostRequest,
    response: SidecarHostResponse,
  ): Promise<void> {
    if (active.completed) return;
    active.completed = true;
    this.activeHostRequests.delete(active.request.id);
    this.emitIdleIfNeeded();
    await this.write(response);
  }

  private async cancelActiveHostRequests(): Promise<void> {
    const responses = [...this.activeHostRequests.values()].map((active) =>
      this.completeHostRequest(
        active,
        hostFailureResponse(
          active.request.id,
          'host_shutting_down',
          'The Electron host is shutting down.',
        ),
      ));
    await Promise.all(responses);
  }

  private handleProcessError(reason: Error): void {
    const error = toError(reason, 'Sidecar process error.');
    this.handshake?.reject(error);
    this.rejectPending(error);
    this.abandonHostRequests();
    this.emit('processError', error);
  }

  private handleProcessClose(code: number | null, signal: NodeJS.Signals | null): void {
    const expected = this.state === 'stopping';
    this.state = 'closed';
    this.clearHandshakeTimer();
    const error = new Error(`Sidecar exited (code=${String(code)}, signal=${String(signal)}).`);
    this.handshake?.reject(error);
    this.rejectPending(error);
    this.abandonHostRequests();
    this.shutdownAck?.resolve(undefined);
    this.closeSignal?.resolve(undefined);
    this.emit('exit', { code, signal, expected } satisfies SidecarExit);
  }

  private failProtocol(error: Error): void {
    if (this.state === 'closed') return;
    this.state = 'stopping';
    this.clearHandshakeTimer();
    this.handshake?.reject(error);
    this.rejectPending(error);
    this.abandonHostRequests();
    if (this.child && !this.child.killed) this.child.kill();
    this.emit('protocolError', error);
  }

  private rejectPending(error: Error): void {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
    this.emitIdleIfNeeded();
  }

  private emitIdleIfNeeded(): void {
    if (this.isIdle()) this.emit('idle');
  }

  private abandonHostRequests(): void {
    for (const active of this.activeHostRequests.values()) active.completed = true;
    this.activeHostRequests.clear();
    this.emitIdleIfNeeded();
  }

  private isIdle(): boolean {
    return this.pending.size === 0 && this.activeHostRequests.size === 0;
  }

  private clearHandshakeTimer(): void {
    if (!this.handshakeTimer) return;
    clearTimeout(this.handshakeTimer);
    this.handshakeTimer = undefined;
  }

  private isClosed(): boolean {
    return this.state === 'closed';
  }
}

function parseHandshake(message: JsonObject): SidecarHandshake {
  if (message.type !== 'handshake'
      || message.protocolVersion !== SIDECAR_PROTOCOL_VERSION
      || typeof message.backendVersion !== 'string'
      || !Array.isArray(message.capabilities)
      || !message.capabilities.every((value) => typeof value === 'string')) {
    throw new SidecarProtocolError('The first sidecar frame must be a compatible handshake.');
  }

  return message as unknown as SidecarHandshake;
}

function parseHostRequest(message: JsonObject): SidecarHostRequest {
  const expectedProperties = ['id', 'method', 'payload', 'type', 'version'];
  const actualProperties = Object.keys(message).sort();
  if (actualProperties.length !== expectedProperties.length
      || actualProperties.some((property, index) => property !== expectedProperties[index])
      || typeof message.id !== 'string'
      || message.id.length === 0
      || message.id.length > 128
      || !/^[A-Za-z0-9._:-]+$/u.test(message.id)
      || !isHostRequestMethod(message.method)
      || !isRecord(message.payload)) {
    throw new SidecarProtocolError('Sidecar sent an invalid host request.');
  }

  return message as unknown as SidecarHostRequest;
}

function validateHostResponsePayload(method: SidecarHostRequest['method'], payload: unknown): void {
  if (method === 'host.dialog.confirm' || method === 'host.startup.set') {
    if (typeof payload !== 'boolean') {
      throw new TypeError(`The ${method} host handler must return a boolean.`);
    }
    return;
  }

  if (payload !== null && (typeof payload !== 'string' || payload.length === 0)) {
    throw new TypeError('The file-dialog host handler must return a non-empty path or null.');
  }
}

function hostFailureResponse(id: string, code: string, message: string): SidecarHostResponse {
  return {
    version: IPC_VERSION,
    id,
    type: 'hostResponse',
    ok: false,
    error: { code, message },
  };
}

function toHostResponseError(reason: unknown): { code: string; message: string } {
  const candidate = isRecord(reason) ? reason : undefined;
  const rawCode = typeof candidate?.code === 'string' ? candidate.code : 'host_request_failed';
  const code = /^[a-z][a-z0-9_]{0,63}$/u.test(rawCode) ? rawCode : 'host_request_failed';
  const rawMessage = reason instanceof Error
    ? reason.message
    : typeof candidate?.message === 'string'
      ? candidate.message
      : 'The Electron host request failed.';
  const message = rawMessage.replaceAll('\0', '').slice(0, 512)
    || 'The Electron host request failed.';
  return { code, message };
}

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, milliseconds);
    timer.unref();
  });
}

async function settleBeforeDeadline(
  promise: Promise<unknown>,
  deadlineMs: number,
  maximumWaitMs: number = Number.POSITIVE_INFINITY,
): Promise<void> {
  const remainingMs = deadlineMs - Date.now();
  if (remainingMs <= 0) return;
  await Promise.race([
    promise,
    delay(Math.min(remainingMs, maximumWaitMs)),
  ]);
}

function toError(reason: unknown, fallbackMessage: string): Error {
  return reason instanceof Error ? reason : new Error(reason ? String(reason) : fallbackMessage);
}
