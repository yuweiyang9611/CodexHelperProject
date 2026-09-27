import { CompletionQueue } from './runtimeCoordination';

export type QuitRequestAction = 'quit' | 'exit';

export interface QuitRequestDecision {
  exitCode: number;
  action: QuitRequestAction;
}

export function decideQuitRequest(
  currentExitCode: number,
  requestedExitCode: number,
  allowQuit: boolean,
): QuitRequestDecision {
  const exitCode = Math.max(currentExitCode, requestedExitCode);
  return {
    exitCode,
    action: allowQuit && exitCode !== 0 ? 'exit' : 'quit',
  };
}

export type ShutdownOutcome = { success: true } | { success: false; reason: unknown };

export interface ShutdownActions {
  stopUpdates(): void;
  closeSidecars(): Promise<void>;
  installUpdate(): Promise<unknown>;
  acknowledge(marker: string, outcome: ShutdownOutcome): void;
  quit(decision: QuitRequestDecision): void;
  reportFailure(reason: unknown): void;
}

/** Owns the quit barrier, update ordering and maintenance acknowledgements together. */
export class ShutdownCoordinator {
  private readonly requests = new CompletionQueue<string, ShutdownOutcome>();
  private started = false;
  private allowed = false;
  private maintenance = false;
  private exitCode = 0;

  constructor(private readonly actions: ShutdownActions) {}

  get isShuttingDown(): boolean { return this.started; }
  get canQuit(): boolean { return this.allowed; }

  request(exitCode: number): void {
    const decision = decideQuitRequest(this.exitCode, exitCode, this.allowed);
    this.exitCode = decision.exitCode;
    this.actions.quit(decision);
  }

  /** Returns true while Electron must prevent the before-quit event. */
  begin(): boolean {
    if (this.allowed) return false;
    if (!this.started) {
      this.started = true;
      void this.finish();
    }
    return true;
  }

  requestMaintenance(marker: string): void {
    this.maintenance = true;
    const registration = this.requests.register(marker);
    if (registration.completed) {
      if (registration.outcome) this.acknowledge(marker, registration.outcome);
      this.request(this.exitCode);
    } else if (!this.started) this.request(0);
  }

  private fail(reason: unknown): ShutdownOutcome {
    this.exitCode = Math.max(1, this.exitCode);
    this.actions.reportFailure(reason);
    return { success: false, reason };
  }

  private acknowledge(marker: string, outcome: ShutdownOutcome): void {
    try { this.actions.acknowledge(marker, outcome); }
    catch (reason) { this.fail(reason); }
  }

  private async finish(): Promise<void> {
    let outcome: ShutdownOutcome = { success: true };
    try { this.actions.stopUpdates(); }
    catch (reason) { outcome = this.fail(reason); }
    try { await this.actions.closeSidecars(); }
    catch (reason) { outcome = this.fail(reason); }
    if (outcome.success && !this.maintenance && this.exitCode === 0) {
      try { await this.actions.installUpdate(); }
      catch (reason) { outcome = this.fail(reason); }
    }
    for (const marker of this.requests.complete(outcome)) this.acknowledge(marker, outcome);
    this.allowed = true;
    this.request(this.exitCode);
  }
}
