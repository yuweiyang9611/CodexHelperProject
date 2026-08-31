import { isRecord } from './protocol';

const MAXIMUM_NOTIFICATION_ID_LENGTH = 64;
const MAXIMUM_NOTIFICATION_TITLE_LENGTH = 256;
const MAXIMUM_NOTIFICATION_BODY_LENGTH = 2_048;
const DEFAULT_REMEMBERED_ID_LIMIT = 256;
const DEFAULT_RETRY_DELAYS_MS = [1_000, 5_000, 15_000] as const;
const MAXIMUM_RETRY_COUNT = 8;
const MAXIMUM_RETRY_DELAY_MS = 5 * 60_000;

export interface NativeNotificationPayload {
  id: string;
  title: string;
  body: string;
}

export interface NativeNotificationOptions {
  id: string;
  title: string;
  body: string;
}

export interface NativeNotificationHandle {
  once(event: 'click', listener: () => void): this;
  once(event: 'close', listener: () => void): this;
  once(event: 'failed', listener: (...arguments_: unknown[]) => void): this;
  show(): void;
}

export type NativeNotificationFailureStage =
  | 'payload'
  | 'availability'
  | 'show'
  | 'native'
  | 'activate';

export interface NativeNotificationFailure {
  stage: NativeNotificationFailureStage;
  error: Error;
  notification?: NativeNotificationPayload;
}

export interface NativeNotificationAdapterOptions {
  isSupported: () => boolean;
  create: (options: Readonly<NativeNotificationOptions>) => NativeNotificationHandle;
  activateWindow: () => void;
  onFailure?: (failure: NativeNotificationFailure) => void;
  rememberedIdLimit?: number;
  /**
   * Delay before each retry. An empty array disables retries. Tests can inject
   * zero/short delays without replacing the process timer implementation.
   */
  retryDelaysMs?: readonly number[];
}

export type NativeNotificationResult = 'shown' | 'duplicate' | 'unsupported' | 'failed';

interface NotificationDelivery {
  notification: NativeNotificationPayload;
  nextRetryIndex: number;
  timer?: ReturnType<typeof setTimeout>;
  handle?: NativeNotificationHandle;
  delivered: boolean;
}

/**
 * Presents trusted Sidecar notification events through Electron's Notification
 * primitive. Electron itself is injected so the policy remains unit-testable in a
 * plain Node process and the main process retains ownership of BrowserWindow.
 */
export class NativeNotificationAdapter {
  private readonly rememberedIds = new Set<string>();
  private readonly retainedOrder: string[] = [];
  private readonly active = new Map<string, NativeNotificationHandle>();
  private readonly deliveries = new Map<string, NotificationDelivery>();
  private readonly rememberedIdLimit: number;
  private readonly retryDelaysMs: readonly number[];
  private disposed = false;

  constructor(private readonly options: NativeNotificationAdapterOptions) {
    if (!Number.isSafeInteger(options.rememberedIdLimit ?? DEFAULT_REMEMBERED_ID_LIMIT)
        || (options.rememberedIdLimit ?? DEFAULT_REMEMBERED_ID_LIMIT) <= 0) {
      throw new RangeError('rememberedIdLimit must be a positive safe integer.');
    }
    this.rememberedIdLimit = options.rememberedIdLimit ?? DEFAULT_REMEMBERED_ID_LIMIT;

    const retryDelaysMs = options.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS;
    if (retryDelaysMs.length > MAXIMUM_RETRY_COUNT
        || retryDelaysMs.some((delay) => !Number.isSafeInteger(delay)
          || delay < 0
          || delay > MAXIMUM_RETRY_DELAY_MS)) {
      throw new RangeError(
        `retryDelaysMs must contain at most ${MAXIMUM_RETRY_COUNT} non-negative safe integers no greater than ${MAXIMUM_RETRY_DELAY_MS}.`,
      );
    }
    this.retryDelaysMs = [...retryDelaysMs];
  }

  get isAvailable(): boolean {
    try {
      return this.options.isSupported();
    } catch (reason) {
      this.report('availability', toError(reason, 'Native notification availability check failed.'));
      return false;
    }
  }

  show(value: unknown): NativeNotificationResult {
    let notification: NativeNotificationPayload;
    try {
      notification = parseNativeNotificationPayload(value);
    } catch (reason) {
      this.report('payload', toError(reason, 'Native notification payload is invalid.'));
      return 'failed';
    }

    if (this.disposed) return 'failed';

    const existing = this.deliveries.get(notification.id);
    if (this.rememberedIds.has(notification.id)) {
      // Retain the freshest body in case the platform asynchronously rejects the
      // current handle and the delivery has to be retried.
      if (existing !== undefined) existing.notification = notification;
      return 'duplicate';
    }

    // A Sidecar replay while an availability/show retry is already pending is a
    // payload refresh, not another retry chain or timer.
    if (existing !== undefined && !existing.delivered) {
      existing.notification = notification;
      return 'duplicate';
    }

    const delivery: NotificationDelivery = {
      notification,
      nextRetryIndex: 0,
      delivered: false,
    };
    this.deliveries.set(notification.id, delivery);
    this.retain(notification.id);
    return this.attempt(delivery);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const delivery of this.deliveries.values()) {
      if (delivery.timer !== undefined) {
        clearTimeout(delivery.timer);
        delivery.timer = undefined;
      }
      delivery.handle = undefined;
    }
    this.deliveries.clear();
    this.active.clear();
    this.rememberedIds.clear();
    this.retainedOrder.length = 0;
  }

  private attempt(delivery: NotificationDelivery): NativeNotificationResult {
    const notification = delivery.notification;
    if (this.disposed || this.deliveries.get(notification.id) !== delivery) {
      return 'failed';
    }

    if (!this.checkAvailability(notification)) {
      this.scheduleRetry(delivery);
      return 'unsupported';
    }

    let attemptedHandle: NativeNotificationHandle | undefined;
    try {
      const handle = this.options.create({
        id: notification.id,
        title: notification.title,
        body: notification.body,
      });
      attemptedHandle = handle;
      let nativeFailureObserved = false;
      delivery.handle = handle;
      this.active.set(notification.id, handle);
      handle.once('click', () => {
        if (!this.ownsHandle(notification.id, delivery, handle)) return;
        this.activate(notification);
      });
      handle.once('failed', (...arguments_: unknown[]) => {
        if (!this.ownsHandle(notification.id, delivery, handle)) return;
        nativeFailureObserved = true;
        this.active.delete(notification.id);
        delivery.handle = undefined;
        delivery.delivered = false;
        this.rememberedIds.delete(notification.id);
        this.scheduleRetry(delivery);
        this.report(
          'native',
          failureFromArguments(arguments_),
          notification,
        );
      });
      handle.once('close', () => {
        if (!this.ownsHandle(notification.id, delivery, handle)) return;
        this.active.delete(notification.id);
        delivery.handle = undefined;
        if (delivery.delivered) this.deliveries.delete(notification.id);
      });
      handle.show();
      if (!nativeFailureObserved
          && !this.disposed
          && this.deliveries.get(notification.id) === delivery) {
        delivery.delivered = true;
        this.remember(notification.id);
      }
      return nativeFailureObserved ? 'failed' : 'shown';
    } catch (reason) {
      // A synchronous failure can race with a re-entrant replacement using the
      // same stable ID. Never release a newer handle owned by that replacement.
      if (attemptedHandle !== undefined
          && this.active.get(notification.id) === attemptedHandle) {
        this.active.delete(notification.id);
      }
      if (attemptedHandle !== undefined && delivery.handle === attemptedHandle) {
        delivery.handle = undefined;
      }
      delivery.delivered = false;
      if (this.disposed || this.deliveries.get(notification.id) !== delivery) {
        return 'failed';
      }
      this.report(
        'show',
        toError(reason, 'Native notification could not be shown.'),
        notification,
      );
      this.scheduleRetry(delivery);
      return 'failed';
    }
  }

  private checkAvailability(notification: NativeNotificationPayload): boolean {
    try {
      if (this.options.isSupported()) return true;
      this.report(
        'availability',
        new Error('Native notifications are unavailable on this desktop.'),
        notification,
      );
    } catch (reason) {
      this.report(
        'availability',
        toError(reason, 'Native notification availability check failed.'),
        notification,
      );
    }
    return false;
  }

  private scheduleRetry(delivery: NotificationDelivery): void {
    if (this.disposed
        || this.deliveries.get(delivery.notification.id) !== delivery
        || delivery.timer !== undefined) {
      return;
    }

    const delay = this.retryDelaysMs[delivery.nextRetryIndex];
    if (delay === undefined) {
      if (delivery.handle === undefined) {
        this.release(delivery.notification.id, delivery);
      }
      return;
    }

    delivery.nextRetryIndex += 1;
    delivery.timer = setTimeout(() => {
      delivery.timer = undefined;
      if (!this.disposed
          && this.deliveries.get(delivery.notification.id) === delivery) {
        this.attempt(delivery);
      }
    }, delay);
    delivery.timer.unref();
  }

  private activate(notification: NativeNotificationPayload): void {
    try {
      this.options.activateWindow();
    } catch (reason) {
      this.report(
        'activate',
        toError(reason, 'Notification click could not activate the window.'),
        notification,
      );
    }
  }

  private remember(id: string): void {
    this.rememberedIds.add(id);
    this.retain(id);
  }

  private retain(id: string): void {
    if (!this.retainedOrder.includes(id)) this.retainedOrder.push(id);
    while (this.retainedOrder.length > this.rememberedIdLimit) {
      const oldest = this.retainedOrder[0];
      if (oldest !== undefined) this.release(oldest);
    }
  }

  private release(id: string, expectedDelivery?: NotificationDelivery): void {
    const delivery = this.deliveries.get(id);
    if (expectedDelivery !== undefined && delivery !== expectedDelivery) return;

    if (delivery !== undefined) {
      if (delivery.timer !== undefined) {
        clearTimeout(delivery.timer);
        delivery.timer = undefined;
      }
      if (delivery.handle !== undefined && this.active.get(id) === delivery.handle) {
        this.active.delete(id);
      }
      delivery.handle = undefined;
      this.deliveries.delete(id);
    } else if (expectedDelivery === undefined) {
      this.active.delete(id);
    }

    this.rememberedIds.delete(id);
    const index = this.retainedOrder.indexOf(id);
    if (index >= 0) this.retainedOrder.splice(index, 1);
  }

  private ownsHandle(
    id: string,
    delivery: NotificationDelivery,
    handle: NativeNotificationHandle,
  ): boolean {
    return !this.disposed
      && this.deliveries.get(id) === delivery
      && delivery.handle === handle
      && this.active.get(id) === handle;
  }

  private report(
    stage: NativeNotificationFailureStage,
    error: Error,
    notification?: NativeNotificationPayload,
  ): void {
    try {
      this.options.onFailure?.({ stage, error, notification });
    } catch {
      // Diagnostics must never turn a recoverable notification failure into a
      // main-process crash.
    }
  }
}

export function parseNativeNotificationPayload(value: unknown): NativeNotificationPayload {
  if (!isRecord(value)) throw new TypeError('Native notification payload must be an object.');
  if (value.activateWindowOnClick !== undefined && value.activateWindowOnClick !== true) {
    throw new TypeError('Native notification activateWindowOnClick can only be true.');
  }

  return {
    id: requiredString(value.id, 'id', MAXIMUM_NOTIFICATION_ID_LENGTH),
    title: requiredString(value.title, 'title', MAXIMUM_NOTIFICATION_TITLE_LENGTH),
    body: requiredString(value.body, 'body', MAXIMUM_NOTIFICATION_BODY_LENGTH),
  };
}

function requiredString(value: unknown, field: string, maximumLength: number): string {
  if (typeof value !== 'string' || value.trim().length === 0 || value.length > maximumLength) {
    throw new TypeError(`Native notification ${field} is missing or invalid.`);
  }
  return value;
}

function failureFromArguments(arguments_: readonly unknown[]): Error {
  for (let index = arguments_.length - 1; index >= 0; index -= 1) {
    const candidate = arguments_[index];
    if (candidate instanceof Error) return candidate;
    if (typeof candidate === 'string' && candidate.trim().length > 0) {
      return new Error(candidate);
    }
  }
  return new Error('The operating system rejected the native notification.');
}

function toError(reason: unknown, fallback: string): Error {
  if (reason instanceof Error) return reason;
  if (typeof reason === 'string' && reason.trim().length > 0) return new Error(reason);
  return new Error(fallback);
}
