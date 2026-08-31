import {
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';

const WINDOW_STATE_SCHEMA_VERSION = 1;

export interface WindowBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface WindowDisplay {
  id: string;
  scaleFactor: number;
  workArea: WindowBounds;
}

export interface PersistedWindowState {
  schemaVersion: typeof WINDOW_STATE_SCHEMA_VERSION;
  bounds: WindowBounds;
  display: WindowDisplay;
  maximized: boolean;
}

export interface RestoredWindowState {
  bounds: WindowBounds;
  maximized: boolean;
}

export function loadWindowState(filePath: string): PersistedWindowState | undefined {
  try {
    const parsed: unknown = JSON.parse(readFileSync(filePath, 'utf8'));
    return parseWindowState(parsed);
  } catch {
    return undefined;
  }
}

export function saveWindowState(filePath: string, state: PersistedWindowState): void {
  const parsed = parseWindowState(state);
  if (!parsed) throw new TypeError('Window state is invalid.');

  const directory = path.dirname(filePath);
  const temporaryPath = `${filePath}.${process.pid}.tmp`;
  mkdirSync(directory, { recursive: true });
  try {
    writeFileSync(temporaryPath, `${JSON.stringify(parsed, undefined, 2)}\n`, {
      encoding: 'utf8',
      mode: 0o600,
    });
    renameSync(temporaryPath, filePath);
  } finally {
    rmSync(temporaryPath, { force: true });
  }
}

export function createWindowState(
  bounds: WindowBounds,
  display: WindowDisplay,
  maximized: boolean,
): PersistedWindowState {
  const state: PersistedWindowState = {
    schemaVersion: WINDOW_STATE_SCHEMA_VERSION,
    bounds,
    display,
    maximized,
  };
  const parsed = parseWindowState(state);
  if (!parsed) throw new TypeError('Window state is invalid.');
  return parsed;
}

export function fitWindowSizeToWorkArea(
  size: Pick<WindowBounds, 'width' | 'height'>,
  workArea: WindowBounds,
): Pick<WindowBounds, 'width' | 'height'> {
  if (!isFinitePositive(size.width) || !isFinitePositive(size.height)) {
    throw new TypeError('Window size is invalid.');
  }
  const parsedWorkArea = parseBounds(workArea);
  if (!parsedWorkArea) throw new TypeError('Window work area is invalid.');
  return {
    width: Math.min(Math.round(size.width), parsedWorkArea.width),
    height: Math.min(Math.round(size.height), parsedWorkArea.height),
  };
}

export function fitWindowBoundsToWorkArea(
  bounds: WindowBounds,
  workArea: WindowBounds,
): WindowBounds {
  const parsedBounds = parseBounds(bounds);
  const parsedWorkArea = parseBounds(workArea);
  if (!parsedBounds || !parsedWorkArea) throw new TypeError('Window bounds are invalid.');
  return clampToWorkArea(parsedBounds, parsedWorkArea);
}

export function equalWindowBounds(left: WindowBounds, right: WindowBounds): boolean {
  return left.x === right.x
    && left.y === right.y
    && left.width === right.width
    && left.height === right.height;
}

export function parseWindowState(value: unknown): PersistedWindowState | undefined {
  if (!isRecord(value) || value.schemaVersion !== WINDOW_STATE_SCHEMA_VERSION) return undefined;
  const bounds = parseBounds(value.bounds);
  const display = parseDisplay(value.display);
  if (!bounds || !display || typeof value.maximized !== 'boolean') return undefined;
  return {
    schemaVersion: WINDOW_STATE_SCHEMA_VERSION,
    bounds,
    display,
    maximized: value.maximized,
  };
}

export function restoreWindowState(
  state: PersistedWindowState | undefined,
  displays: readonly WindowDisplay[],
  fallbackBounds: WindowBounds,
  minimumSize: Pick<WindowBounds, 'width' | 'height'>,
): RestoredWindowState {
  if (displays.length === 0) {
    return { bounds: normalizeSize(fallbackBounds, minimumSize), maximized: false };
  }

  const parsedState = state ? parseWindowState(state) : undefined;
  if (!parsedState) {
    return {
      bounds: centerAndClamp(normalizeSize(fallbackBounds, minimumSize), displays[0].workArea),
      maximized: false,
    };
  }

  const targetDisplay = displays.find((display) => display.id === parsedState.display.id)
    ?? displayWithLargestOverlap(parsedState.bounds, displays)
    ?? displays[0];
  const normalizedBounds = normalizeSize(parsedState.bounds, minimumSize);
  return {
    bounds: clampToWorkArea(normalizedBounds, targetDisplay.workArea),
    maximized: parsedState.maximized,
  };
}

function parseDisplay(value: unknown): WindowDisplay | undefined {
  if (!isRecord(value) || (typeof value.id !== 'string' && typeof value.id !== 'number')) {
    return undefined;
  }
  if (!isFinitePositive(value.scaleFactor)) return undefined;
  const workArea = parseBounds(value.workArea);
  if (!workArea) return undefined;
  return { id: String(value.id), scaleFactor: value.scaleFactor, workArea };
}

function parseBounds(value: unknown): WindowBounds | undefined {
  if (!isRecord(value)) return undefined;
  if (!isFiniteNumber(value.x) || !isFiniteNumber(value.y)
      || !isFinitePositive(value.width) || !isFinitePositive(value.height)) {
    return undefined;
  }
  return {
    x: Math.round(value.x),
    y: Math.round(value.y),
    width: Math.round(value.width),
    height: Math.round(value.height),
  };
}

function normalizeSize(
  bounds: WindowBounds,
  minimumSize: Pick<WindowBounds, 'width' | 'height'>,
): WindowBounds {
  return {
    x: bounds.x,
    y: bounds.y,
    width: Math.max(Math.round(minimumSize.width), Math.round(bounds.width)),
    height: Math.max(Math.round(minimumSize.height), Math.round(bounds.height)),
  };
}

function centerAndClamp(bounds: WindowBounds, workArea: WindowBounds): WindowBounds {
  return clampToWorkArea({
    ...bounds,
    x: workArea.x + Math.round((workArea.width - bounds.width) / 2),
    y: workArea.y + Math.round((workArea.height - bounds.height) / 2),
  }, workArea);
}

function clampToWorkArea(bounds: WindowBounds, workArea: WindowBounds): WindowBounds {
  const width = Math.min(bounds.width, workArea.width);
  const height = Math.min(bounds.height, workArea.height);
  const minimumX = workArea.x;
  const maximumX = workArea.x + workArea.width - width;
  const minimumY = workArea.y;
  const maximumY = workArea.y + workArea.height - height;
  return {
    x: clamp(bounds.x, minimumX, maximumX),
    y: clamp(bounds.y, minimumY, maximumY),
    width,
    height,
  };
}

function displayWithLargestOverlap(
  bounds: WindowBounds,
  displays: readonly WindowDisplay[],
): WindowDisplay | undefined {
  let best: WindowDisplay | undefined;
  let bestArea = 0;
  for (const display of displays) {
    const area = overlapArea(bounds, display.workArea);
    if (area > bestArea) {
      best = display;
      bestArea = area;
    }
  }
  return best;
}

function overlapArea(left: WindowBounds, right: WindowBounds): number {
  const width = Math.max(0, Math.min(left.x + left.width, right.x + right.width)
    - Math.max(left.x, right.x));
  const height = Math.max(0, Math.min(left.y + left.height, right.y + right.height)
    - Math.max(left.y, right.y));
  return width * height;
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value));
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isFinitePositive(value: unknown): value is number {
  return isFiniteNumber(value) && value > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
