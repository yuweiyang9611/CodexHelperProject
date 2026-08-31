export const IPC_VERSION = 1 as const;
export const SIDECAR_PROTOCOL_VERSION = 1 as const;
export const MAX_FRAME_BYTES = 1024 * 1024;

export const REQUEST_CHANNEL = 'codexu:request';
export const EVENT_CHANNEL = 'codexu:event';

export const REQUIRED_SIDECAR_CAPABILITIES = ['host.rpc.v1', 'host.state.v1'] as const;

export const HOST_REQUEST_METHODS = [
  'host.dialog.saveFile',
  'host.dialog.openFile',
  'host.dialog.confirm',
  'host.startup.set',
] as const;

export type HostRequestMethod = typeof HOST_REQUEST_METHODS[number];

export type JsonObject = Record<string, unknown>;

export interface SidecarHandshake {
  version: typeof IPC_VERSION;
  type: 'handshake';
  protocolVersion: typeof SIDECAR_PROTOCOL_VERSION;
  backendVersion: string;
  capabilities: string[];
  protocol?: string;
  framing?: string;
  maxFrameBytes?: number;
  platform?: string;
  pid?: number;
}

export interface SidecarRequest {
  version: typeof IPC_VERSION;
  id: string;
  type: 'request';
  method: string;
  payload: JsonObject;
}

export interface SidecarError {
  code: string;
  message: string;
}

export interface SidecarResponse {
  version: typeof IPC_VERSION;
  id: string;
  type: 'response';
  ok: boolean;
  payload: unknown;
  error?: SidecarError | null;
}

export interface SidecarEvent {
  version: typeof IPC_VERSION;
  type: 'event';
  method: string;
  payload: unknown;
}

export interface SidecarHostRequest {
  version: typeof IPC_VERSION;
  id: string;
  type: 'hostRequest';
  method: HostRequestMethod;
  payload: JsonObject;
}

export interface SidecarHostResponse {
  version: typeof IPC_VERSION;
  id: string;
  type: 'hostResponse';
  ok: boolean;
  payload?: unknown;
  error?: SidecarError;
}

export interface SidecarHostState {
  version: typeof IPC_VERSION;
  type: 'hostState';
  globalHotKeyRegistered: boolean;
}

export interface SidecarControl {
  version: typeof IPC_VERSION;
  type: 'control';
  method: 'shutdown' | 'shutdownAck';
}

export type SidecarIncomingMessage =
  | SidecarHandshake
  | SidecarResponse
  | SidecarEvent
  | SidecarHostRequest
  | SidecarControl;

export function isHostRequestMethod(value: unknown): value is HostRequestMethod {
  return typeof value === 'string'
    && (HOST_REQUEST_METHODS as readonly string[]).includes(value);
}

export function assertRequiredSidecarCapabilities(capabilities: readonly string[]): void {
  for (const capability of REQUIRED_SIDECAR_CAPABILITIES) {
    if (!capabilities.includes(capability)) {
      throw new Error(`Sidecar handshake does not advertise required ${capability} support.`);
    }
  }
}

export function isRecord(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
