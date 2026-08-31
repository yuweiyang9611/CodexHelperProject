export class SingleFlightOperation {
  private current: Promise<void> | undefined;

  get running(): boolean {
    return this.current !== undefined;
  }

  run(operation: () => Promise<void>): Promise<void> {
    if (this.current) return this.current;

    return this.track(Promise.resolve().then(operation));
  }

  /**
   * Queues an operation after the flight that was active at the time of this
   * call. Unlike run(), the supplied operation is always invoked, so callers
   * can require a navigation that observes state published after an older
   * navigation began.
   */
  runFresh(operation: () => Promise<void>): Promise<void> {
    const preceding = this.current;
    const started = (preceding ? preceding.catch(() => undefined) : Promise.resolve())
      .then(operation);
    return this.track(started);
  }

  private track(started: Promise<void>): Promise<void> {
    const tracked = started.finally(() => {
      if (this.current === tracked) this.current = undefined;
    });
    this.current = tracked;
    return tracked;
  }
}

/**
 * Monotonic failure marker for work whose success can race a separate failure
 * event. Capture the generation when an attempt starts and reject that attempt
 * if a newer event was recorded before it publishes success.
 */
export class GenerationFence {
  private value = 0;

  snapshot(): number {
    return this.value;
  }

  advance(): number {
    this.value += 1;
    return this.value;
  }

  isCurrent(snapshot: number): boolean {
    if (!Number.isSafeInteger(snapshot) || snapshot < 0) {
      throw new RangeError('Generation snapshot must be a non-negative safe integer.');
    }
    return snapshot === this.value;
  }
}

export interface QueuedRecoveryPrompt<T> {
  key: string;
  value: T;
}

/**
 * FIFO queue that retains a key until its active prompt is completed. Repeated
 * circuit-open signals for one component are coalesced without hiding failures
 * from a different component.
 */
export class RecoveryPromptQueue<T> {
  private readonly queuedKeys = new Set<string>();
  private readonly cancelledKeys = new Set<string>();
  private readonly queued: Array<QueuedRecoveryPrompt<T>> = [];
  private active: QueuedRecoveryPrompt<T> | undefined;

  get size(): number {
    return this.queued.length + (this.active ? 1 : 0);
  }

  enqueue(key: string, value: T): boolean {
    if (key.trim().length === 0) throw new TypeError('Recovery prompt key must not be empty.');
    if (this.queuedKeys.has(key)) return false;
    this.queuedKeys.add(key);
    this.queued.push({ key, value });
    return true;
  }

  take(): QueuedRecoveryPrompt<T> | undefined {
    if (this.active) return undefined;
    this.active = this.queued.shift();
    return this.active;
  }

  complete(key: string): void {
    if (!this.active || this.active.key !== key) {
      throw new Error(`Recovery prompt '${key}' is not active.`);
    }
    this.active = undefined;
    this.queuedKeys.delete(key);
    this.cancelledKeys.delete(key);
  }

  cancel(key: string): 'active' | 'queued' | undefined {
    if (this.active?.key === key) {
      this.cancelledKeys.add(key);
      return 'active';
    }

    const index = this.queued.findIndex((prompt) => prompt.key === key);
    if (index < 0) return undefined;
    this.queued.splice(index, 1);
    this.queuedKeys.delete(key);
    this.cancelledKeys.delete(key);
    return 'queued';
  }

  isCancelled(key: string): boolean {
    return this.cancelledKeys.has(key);
  }

  clearQueued(): void {
    for (const prompt of this.queued) {
      this.queuedKeys.delete(prompt.key);
      this.cancelledKeys.delete(prompt.key);
    }
    this.queued.length = 0;
  }
}

export interface CompletionRegistration<T> {
  completed: boolean;
  outcome?: T;
}

/**
 * Collects values until an outcome is known, then returns that outcome to every
 * late registrant. This closes the gap where maintenance requests arrive while
 * an already-started shutdown is between before-quit and process exit.
 */
export class CompletionQueue<TValue, TOutcome> {
  private readonly pending: TValue[] = [];
  private completion: TOutcome | undefined;
  private completed = false;

  register(value: TValue): CompletionRegistration<TOutcome> {
    if (this.completed) return { completed: true, outcome: this.completion };
    this.pending.push(value);
    return { completed: false };
  }

  complete(outcome: TOutcome): TValue[] {
    if (this.completed) throw new Error('Completion queue has already completed.');
    this.completed = true;
    this.completion = outcome;
    return this.pending.splice(0);
  }
}
