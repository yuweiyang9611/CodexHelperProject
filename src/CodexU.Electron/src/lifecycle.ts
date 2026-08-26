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
