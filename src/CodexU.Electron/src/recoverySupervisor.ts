export type RecoveryState = 'healthy' | 'backoff' | 'recovering' | 'circuit-open';

export interface RecoverySupervisorOptions {
  maximumAttempts?: number;
  initialDelayMilliseconds?: number;
  maximumDelayMilliseconds?: number;
  stablePeriodMilliseconds?: number;
}

export interface RecoveryRetryDecision {
  action: 'retry';
  attempt: number;
  delayMilliseconds: number;
}

export interface RecoveryStopDecision {
  action: 'stop';
  attempts: number;
  reason: 'circuit-open';
}

export interface RecoveryWaitDecision {
  action: 'wait';
  attempt: number;
  reason: 'retry-pending';
}

export type RecoveryDecision =
  | RecoveryRetryDecision
  | RecoveryStopDecision
  | RecoveryWaitDecision;

export interface RecoverySnapshot {
  state: RecoveryState;
  attempts: number;
  maximumAttempts: number;
  pendingAttempt?: number;
  recoveredAtMilliseconds?: number;
  circuitOpenedAtMilliseconds?: number;
}

const DEFAULT_MAXIMUM_ATTEMPTS = 3;
const DEFAULT_INITIAL_DELAY_MILLISECONDS = 500;
const DEFAULT_MAXIMUM_DELAY_MILLISECONDS = 30_000;
const DEFAULT_STABLE_PERIOD_MILLISECONDS = 60_000;

/**
 * Timer-free recovery state machine shared by renderer and Sidecar supervision.
 * Callers own the actual timer/process work, so every transition is deterministic
 * and stale or duplicated failure events cannot schedule parallel restarts.
 */
export class RecoverySupervisor {
  private readonly maximumAttempts: number;
  private readonly initialDelayMilliseconds: number;
  private readonly maximumDelayMilliseconds: number;
  private readonly stablePeriodMilliseconds: number;

  private stateValue: RecoveryState = 'healthy';
  private attemptsValue = 0;
  private pendingAttemptValue: number | undefined;
  private recoveredAtMillisecondsValue: number | undefined;
  private circuitOpenedAtMillisecondsValue: number | undefined;

  constructor(options: RecoverySupervisorOptions = {}) {
    this.maximumAttempts = validateInteger(
      options.maximumAttempts ?? DEFAULT_MAXIMUM_ATTEMPTS,
      0,
      32,
      'maximumAttempts',
    );
    this.initialDelayMilliseconds = validateInteger(
      options.initialDelayMilliseconds ?? DEFAULT_INITIAL_DELAY_MILLISECONDS,
      0,
      Number.MAX_SAFE_INTEGER,
      'initialDelayMilliseconds',
    );
    this.maximumDelayMilliseconds = validateInteger(
      options.maximumDelayMilliseconds ?? DEFAULT_MAXIMUM_DELAY_MILLISECONDS,
      this.initialDelayMilliseconds,
      Number.MAX_SAFE_INTEGER,
      'maximumDelayMilliseconds',
    );
    this.stablePeriodMilliseconds = validateInteger(
      options.stablePeriodMilliseconds ?? DEFAULT_STABLE_PERIOD_MILLISECONDS,
      0,
      Number.MAX_SAFE_INTEGER,
      'stablePeriodMilliseconds',
    );
  }

  snapshot(): RecoverySnapshot {
    return {
      state: this.stateValue,
      attempts: this.attemptsValue,
      maximumAttempts: this.maximumAttempts,
      ...(this.pendingAttemptValue === undefined
        ? {}
        : { pendingAttempt: this.pendingAttemptValue }),
      ...(this.recoveredAtMillisecondsValue === undefined
        ? {}
        : { recoveredAtMilliseconds: this.recoveredAtMillisecondsValue }),
      ...(this.circuitOpenedAtMillisecondsValue === undefined
        ? {}
        : { circuitOpenedAtMilliseconds: this.circuitOpenedAtMillisecondsValue }),
    };
  }

  recordFailure(nowMilliseconds = Date.now()): RecoveryDecision {
    validateTimestamp(nowMilliseconds);

    if (this.stateValue === 'circuit-open') return this.stopDecision();
    if (this.stateValue === 'backoff' && this.pendingAttemptValue !== undefined) {
      return {
        action: 'wait',
        attempt: this.pendingAttemptValue,
        reason: 'retry-pending',
      };
    }

    this.resetAttemptsAfterStablePeriod(nowMilliseconds);
    this.recoveredAtMillisecondsValue = undefined;

    if (this.attemptsValue >= this.maximumAttempts) {
      this.stateValue = 'circuit-open';
      this.pendingAttemptValue = undefined;
      this.circuitOpenedAtMillisecondsValue = nowMilliseconds;
      return this.stopDecision();
    }

    this.attemptsValue += 1;
    this.pendingAttemptValue = this.attemptsValue;
    this.stateValue = 'backoff';
    return this.retryDecision(this.attemptsValue);
  }

  markRecoveryStarted(attempt: number): boolean {
    if (
      this.stateValue !== 'backoff'
      || this.pendingAttemptValue === undefined
      || attempt !== this.pendingAttemptValue
    ) {
      return false;
    }
    this.pendingAttemptValue = undefined;
    this.stateValue = 'recovering';
    return true;
  }

  markRecovered(nowMilliseconds = Date.now()): void {
    validateTimestamp(nowMilliseconds);
    if (this.stateValue === 'circuit-open') {
      throw new Error('An open recovery circuit must be reset before it can recover.');
    }
    this.pendingAttemptValue = undefined;
    this.stateValue = 'healthy';
    this.recoveredAtMillisecondsValue = nowMilliseconds;
  }

  markStable(nowMilliseconds = Date.now()): boolean {
    validateTimestamp(nowMilliseconds);
    if (
      this.stateValue !== 'healthy'
      || this.recoveredAtMillisecondsValue === undefined
      || nowMilliseconds - this.recoveredAtMillisecondsValue < this.stablePeriodMilliseconds
    ) {
      return false;
    }
    this.reset();
    return true;
  }

  reset(): void {
    this.stateValue = 'healthy';
    this.attemptsValue = 0;
    this.pendingAttemptValue = undefined;
    this.recoveredAtMillisecondsValue = undefined;
    this.circuitOpenedAtMillisecondsValue = undefined;
  }

  private resetAttemptsAfterStablePeriod(nowMilliseconds: number): void {
    if (
      this.stateValue === 'healthy'
      && this.recoveredAtMillisecondsValue !== undefined
      && nowMilliseconds - this.recoveredAtMillisecondsValue >= this.stablePeriodMilliseconds
    ) {
      this.reset();
    }
  }

  private retryDecision(attempt: number): RecoveryRetryDecision {
    const exponentialDelay = this.initialDelayMilliseconds * (2 ** (attempt - 1));
    return {
      action: 'retry',
      attempt,
      delayMilliseconds: Math.min(exponentialDelay, this.maximumDelayMilliseconds),
    };
  }

  private stopDecision(): RecoveryStopDecision {
    return {
      action: 'stop',
      attempts: this.attemptsValue,
      reason: 'circuit-open',
    };
  }
}

function validateInteger(
  value: number,
  minimum: number,
  maximum: number,
  name: string,
): number {
  if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
    throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}.`);
  }
  return value;
}

function validateTimestamp(value: number): void {
  if (!Number.isFinite(value)) throw new TypeError('Recovery timestamp must be finite.');
}
