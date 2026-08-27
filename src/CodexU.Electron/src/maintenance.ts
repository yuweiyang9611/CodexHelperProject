import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const SHUTDOWN_FLAG = '--maintenance-shutdown';
const MARKER_PREFIX = '--maintenance-shutdown-marker=';
const MARKER_NAME = 'codexu-maintenance-shutdown.marker';
const SHUTDOWN_FAILURE = 'failure';

export function resolveMaintenanceShutdownMarker(
  commandLine: readonly string[],
  temporaryDirectory: string,
): string | undefined {
  if (!commandLine.includes(SHUTDOWN_FLAG)) return undefined;

  const markerArguments = commandLine.filter((argument) => argument.startsWith(MARKER_PREFIX));
  if (markerArguments.length !== 1) {
    throw new Error('Maintenance shutdown requires exactly one marker path.');
  }

  const markerValue = markerArguments[0].slice(MARKER_PREFIX.length).trim();
  if (markerValue.length === 0) {
    throw new Error('Maintenance shutdown marker path is empty.');
  }

  const markerPath = path.resolve(markerValue);
  const tempRoot = path.resolve(temporaryDirectory);
  const markerParentRelative = path.relative(tempRoot, path.dirname(markerPath));
  if (
    markerParentRelative === '..'
    || markerParentRelative.startsWith(`..${path.sep}`)
    || path.isAbsolute(markerParentRelative)
    || path.basename(markerPath).toLocaleLowerCase('en-US') !== MARKER_NAME
  ) {
    throw new Error('Maintenance shutdown marker must be the dedicated file within the system temp directory.');
  }

  return markerPath;
}

export function resetMaintenanceShutdownMarker(markerPath: string): void {
  if (existsSync(markerPath)) unlinkSync(markerPath);
}

export function writeMaintenanceShutdownMarker(
  markerPath: string,
  processId = process.pid,
): void {
  writeFileSync(markerPath, String(processId), { encoding: 'ascii', flag: 'wx' });
}

export function writeMaintenanceShutdownFailureMarker(markerPath: string): void {
  writeFileSync(markerPath, SHUTDOWN_FAILURE, { encoding: 'ascii', flag: 'wx' });
}

export async function waitForMaintenanceShutdown(
  markerPath: string,
  timeoutMilliseconds = 30_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  let targetProcessId: number | undefined;

  while (Date.now() < deadline) {
    if (targetProcessId === undefined && existsSync(markerPath)) {
      const marker = readFileSync(markerPath, 'ascii').trim();
      if (marker === SHUTDOWN_FAILURE) {
        resetMaintenanceShutdownMarker(markerPath);
        throw new Error('The resident CodexU process could not stop its Sidecar safely.');
      }
      if (!/^[1-9]\d*$/.test(marker)) {
        throw new Error('Maintenance shutdown marker contains an invalid process id.');
      }
      targetProcessId = Number(marker);
    }

    if (targetProcessId !== undefined && !isProcessAlive(targetProcessId)) {
      resetMaintenanceShutdownMarker(markerPath);
      return;
    }

    await delay(100);
  }

  throw new Error('Timed out waiting for the resident CodexU process to exit.');
}

function isProcessAlive(processId: number): boolean {
  try {
    process.kill(processId, 0);
    return true;
  } catch (reason) {
    return !(reason instanceof Error && 'code' in reason && reason.code === 'ESRCH');
  }
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
