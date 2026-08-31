import {
  appendFileSync,
  existsSync,
  mkdirSync,
  renameSync,
  statSync,
  unlinkSync,
} from 'node:fs';
import path from 'node:path';

export type RuntimeLogLevel = 'debug' | 'info' | 'warn' | 'error';

export interface PersistentLogOptions {
  directory: string;
  fileName?: string;
  maximumFileBytes?: number;
  maximumFiles?: number;
  now?: () => Date;
  onError?: (error: Error) => void;
}

const DEFAULT_FILE_NAME = 'codexu.log';
const DEFAULT_MAXIMUM_FILE_BYTES = 2 * 1024 * 1024;
const DEFAULT_MAXIMUM_FILES = 5;
const MINIMUM_FILE_BYTES = 128;
const MAXIMUM_FILE_COUNT = 100;
const REDACTED = '[REDACTED]';
const DEFAULT_MAXIMUM_BUFFERED_LINE_BYTES = 64 * 1024;
export const OVERSIZED_STDERR_LINE = '[oversized stderr line omitted]';

/**
 * Reassembles arbitrary stream chunks into complete lines. Sensitive-value
 * redaction must run after this boundary, otherwise a token split across two
 * stderr chunks can evade every pattern.
 */
export class TextLineBuffer {
  private pending = '';
  private pendingBytes = 0;
  private discardingUntilNewline = false;

  constructor(private readonly maximumLineBytes = DEFAULT_MAXIMUM_BUFFERED_LINE_BYTES) {
    if (!Number.isSafeInteger(maximumLineBytes) || maximumLineBytes <= 0) {
      throw new RangeError('maximumLineBytes must be a positive safe integer.');
    }
  }

  push(chunk: string): string[] {
    if (chunk.length === 0) return [];
    const lines: string[] = [];
    let offset = 0;
    let newlineIndex = chunk.indexOf('\n', offset);
    while (newlineIndex >= 0) {
      this.consume(chunk.slice(offset, newlineIndex), true, lines);
      offset = newlineIndex + 1;
      newlineIndex = chunk.indexOf('\n', offset);
    }
    this.consume(chunk.slice(offset), false, lines);
    return lines;
  }

  flush(): string | undefined {
    if (this.discardingUntilNewline) {
      this.reset();
      return OVERSIZED_STDERR_LINE;
    }
    if (this.pending.length === 0) return undefined;
    const line = this.pending.endsWith('\r') ? this.pending.slice(0, -1) : this.pending;
    this.reset();
    return line;
  }

  private consume(segment: string, terminated: boolean, lines: string[]): void {
    if (this.discardingUntilNewline) {
      if (terminated) {
        lines.push(OVERSIZED_STDERR_LINE);
        this.reset();
      }
      return;
    }

    const segmentBytes = Buffer.byteLength(segment, 'utf8');
    if (this.pendingBytes + segmentBytes > this.maximumLineBytes) {
      this.pending = '';
      this.pendingBytes = 0;
      this.discardingUntilNewline = true;
      if (terminated) {
        lines.push(OVERSIZED_STDERR_LINE);
        this.reset();
      }
      return;
    }

    this.pending += segment;
    this.pendingBytes += segmentBytes;
    if (!terminated) return;
    lines.push(this.pending.endsWith('\r') ? this.pending.slice(0, -1) : this.pending);
    this.reset();
  }

  private reset(): void {
    this.pending = '';
    this.pendingBytes = 0;
    this.discardingUntilNewline = false;
  }
}

/**
 * A synchronous, best-effort writer intended for Electron's main process.
 * Each active and archived file is bounded, and write failures are reported
 * without turning a diagnostics failure into an application failure.
 */
export class PersistentLog {
  readonly filePath: string;

  private readonly maximumFileBytes: number;
  private readonly maximumFiles: number;
  private readonly now: () => Date;
  private readonly onError: ((error: Error) => void) | undefined;

  constructor(options: PersistentLogOptions) {
    if (options.directory.trim().length === 0) {
      throw new TypeError('Persistent log directory must not be empty.');
    }

    const fileName = options.fileName ?? DEFAULT_FILE_NAME;
    if (
      fileName.length === 0
      || fileName === '.'
      || fileName === '..'
      || path.basename(fileName) !== fileName
    ) {
      throw new TypeError('Persistent log file name must be a plain file name.');
    }

    this.maximumFileBytes = validateInteger(
      options.maximumFileBytes ?? DEFAULT_MAXIMUM_FILE_BYTES,
      MINIMUM_FILE_BYTES,
      Number.MAX_SAFE_INTEGER,
      'maximumFileBytes',
    );
    this.maximumFiles = validateInteger(
      options.maximumFiles ?? DEFAULT_MAXIMUM_FILES,
      1,
      MAXIMUM_FILE_COUNT,
      'maximumFiles',
    );
    this.now = options.now ?? (() => new Date());
    this.onError = options.onError;

    const directory = path.resolve(options.directory);
    mkdirSync(directory, { recursive: true });
    this.filePath = path.join(directory, fileName);
  }

  write(
    level: RuntimeLogLevel,
    scope: string,
    message: unknown,
    details?: unknown,
  ): boolean {
    try {
      const entry = this.createEntry(level, scope, message, details);
      this.rotateIfNecessary(Buffer.byteLength(entry, 'utf8'));
      appendFileSync(this.filePath, entry, { encoding: 'utf8', mode: 0o600 });
      return true;
    } catch (reason) {
      this.reportError(toError(reason));
      return false;
    }
  }

  private createEntry(
    level: RuntimeLogLevel,
    scope: string,
    message: unknown,
    details: unknown,
  ): string {
    const safeScope = redactSensitiveText(normalizeSingleLine(scope));
    const values = details === undefined ? [message] : [message, details];
    const safeMessage = redactSensitiveText(values.map(renderLogValue).join(' '));
    const normalizedMessage = safeMessage.replace(/\r?\n/gu, '\\n');
    const entry = `${this.now().toISOString()} [${level}] [${safeScope}] ${normalizedMessage}\n`;
    return truncateUtf8(entry, this.maximumFileBytes);
  }

  private rotateIfNecessary(incomingBytes: number): void {
    const currentBytes = existsSync(this.filePath) ? statSync(this.filePath).size : 0;
    if (currentBytes === 0 || currentBytes + incomingBytes <= this.maximumFileBytes) return;

    if (this.maximumFiles === 1) {
      unlinkSync(this.filePath);
      return;
    }

    const oldestPath = this.rotatedFilePath(this.maximumFiles - 1);
    if (existsSync(oldestPath)) unlinkSync(oldestPath);

    for (let index = this.maximumFiles - 2; index >= 1; index -= 1) {
      const sourcePath = this.rotatedFilePath(index);
      if (!existsSync(sourcePath)) continue;
      const destinationPath = this.rotatedFilePath(index + 1);
      if (existsSync(destinationPath)) unlinkSync(destinationPath);
      renameSync(sourcePath, destinationPath);
    }

    const firstArchive = this.rotatedFilePath(1);
    if (existsSync(firstArchive)) unlinkSync(firstArchive);
    renameSync(this.filePath, firstArchive);
  }

  private rotatedFilePath(index: number): string {
    const extension = path.extname(this.filePath);
    const stem = extension.length > 0 ? this.filePath.slice(0, -extension.length) : this.filePath;
    return `${stem}.${index}${extension}`;
  }

  private reportError(error: Error): void {
    if (!this.onError) return;
    try {
      this.onError(error);
    } catch {
      // Logging must remain best-effort even when its observer fails.
    }
  }
}

export function redactSensitiveText(value: string): string {
  return value
    .replace(
      /((?:["']?)(?:authorization|proxy-authorization)(?:["']?)\s*[:=]\s*)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|(?:(?:bearer|basic)\s+)?[^\s,;&}\]]+)/giu,
      `$1${REDACTED}`,
    )
    .replace(
      /(--(?:api[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret|password|passwd|secret|token))(?:=|\s+)(?:"[^"]*"|'[^']*'|[^\s,;]+)/giu,
      `$1=${REDACTED}`,
    )
    .replace(
      /((?:["']?)(?:api[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret|password|passwd|secret|token)(?:["']?)\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;&,}\]]+)/giu,
      `$1${REDACTED}`,
    )
    .replace(
      /\b([a-z][a-z0-9+.-]*:\/\/)[^\s/@:]+:[^\s/@]+@/giu,
      `$1${REDACTED}@`,
    )
    .replace(/\b(?:gh[pousr]_[a-z0-9_]{20,}|sk-[a-z0-9_-]{20,})\b/giu, REDACTED)
    .replace(/\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\b/giu, REDACTED)
    .replace(/\b([a-z]:\\Users\\)[^\\\r\n]+/giu, '$1[USER]')
    .replace(/(\/(?:Users|home)\/)[^/\s]+/gu, '$1[USER]')
    .replace(/\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b/giu, '[EMAIL]');
}

function normalizeSingleLine(value: string): string {
  return value.replace(/[\r\n\0]+/gu, ' ').trim() || 'runtime';
}

function renderLogValue(value: unknown): string {
  if (value instanceof Error) return value.stack ?? value.message;
  if (typeof value === 'string') return value;
  try {
    const rendered = JSON.stringify(value);
    return rendered ?? String(value);
  } catch {
    return String(value);
  }
}

function truncateUtf8(value: string, maximumBytes: number): string {
  const bytes = Buffer.from(value, 'utf8');
  if (bytes.length <= maximumBytes) return value;

  const marker = ' [truncated]\n';
  const markerBytes = Buffer.byteLength(marker, 'utf8');
  let end = maximumBytes - markerBytes;
  while (end > 0 && (bytes[end] & 0xc0) === 0x80) end -= 1;
  return `${bytes.subarray(0, Math.max(0, end)).toString('utf8').trimEnd()}${marker}`;
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

function toError(reason: unknown): Error {
  return reason instanceof Error ? reason : new Error(String(reason));
}
