import type {
  BrowserWindow,
  MessageBoxOptions,
  MessageBoxReturnValue,
  OpenDialogOptions,
  OpenDialogReturnValue,
  SaveDialogOptions,
  SaveDialogReturnValue,
} from 'electron';
import {
  isRecord,
  type HostRequestMethod,
  type JsonObject,
  type SidecarHostRequest,
} from './protocol';

const MAX_TITLE_LENGTH = 200;
const MAX_MESSAGE_LENGTH = 4_096;
const MAX_FILE_NAME_LENGTH = 255;
const MAX_EXTENSION_LENGTH = 16;
const MAX_FILE_TYPE_NAME_LENGTH = 100;
const MAX_FILE_TYPES = 16;
const MAX_PATTERNS_PER_FILE_TYPE = 16;
const INVALID_SINGLE_LINE_TEXT = /[\u0000-\u001f\u007f]/u;
const INVALID_FILE_NAME = /[<>:"/\\|?*\u0000-\u001f\u007f]/u;
const WINDOWS_RESERVED_FILE_NAME = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/iu;
const EXTENSION_PATTERN = /^\.[A-Za-z0-9][A-Za-z0-9_-]{0,14}$/u;
const FILTER_PATTERN = /^\*\.[A-Za-z0-9][A-Za-z0-9_-]{0,14}$/u;

interface HostFileType {
  name: string;
  patterns: string[];
}

interface HostFileDialogRequest {
  title: string;
  suggestedFileName: string;
  defaultExtension: string;
  fileTypes: HostFileType[];
  checkFileExists: boolean;
  overwritePrompt: boolean;
}

interface HostConfirmationRequest {
  title: string;
  message: string;
  isWarning: boolean;
}

export interface HostDialogApi {
  showSaveDialog(
    browserWindow: BrowserWindow,
    options: SaveDialogOptions,
  ): Promise<SaveDialogReturnValue>;
  showOpenDialog(
    browserWindow: BrowserWindow,
    options: OpenDialogOptions,
  ): Promise<OpenDialogReturnValue>;
  showMessageBox(
    browserWindow: BrowserWindow,
    options: MessageBoxOptions,
  ): Promise<MessageBoxReturnValue>;
}

export interface HostDialogHandlerOptions {
  forceSafeCancellation?: boolean;
  platform?: NodeJS.Platform;
  startupRegistration?: HostStartupRegistrationApi;
}

export interface HostStartupRegistrationApi {
  setEnabled(enabled: boolean): boolean;
}

export class HostRequestHandlerError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'HostRequestHandlerError';
  }
}

export function createHostRequestHandler(
  dialog: HostDialogApi,
  getOwnerWindow: () => BrowserWindow | undefined,
  options: HostDialogHandlerOptions = {},
): (request: SidecarHostRequest) => Promise<unknown> {
  let dialogActive = false;

  const runExclusive = async <T>(operation: () => Promise<T>): Promise<T> => {
    if (dialogActive) {
      throw new HostRequestHandlerError(
        'host_busy',
        'Another native dialog is already active.',
      );
    }
    dialogActive = true;
    try {
      return await operation();
    } finally {
      dialogActive = false;
    }
  };

  return async (request) => {
    switch (request.method) {
      case 'host.dialog.saveFile': {
        const payload = parseFileDialogRequest(request.payload, request.method);
        if (options.forceSafeCancellation) return null;
        const owner = getUsableOwner(getOwnerWindow());
        if (!owner) return null;

        const result = await runExclusive(() => dialog.showSaveDialog(owner, {
          title: payload.title,
          defaultPath: withDefaultExtension(
            payload.suggestedFileName,
            payload.defaultExtension,
          ),
          filters: toElectronFilters(payload.fileTypes),
          // Windows provides its overwrite confirmation natively. Electron's
          // explicit flag is documented for Linux only.
          properties: payload.overwritePrompt && (options.platform ?? process.platform) === 'linux'
            ? ['showOverwriteConfirmation']
            : [],
        }));
        return result.canceled || !isUsableSelectedPath(result.filePath)
          ? null
          : result.filePath;
      }

      case 'host.dialog.openFile': {
        const payload = parseFileDialogRequest(request.payload, request.method);
        if (options.forceSafeCancellation) return null;
        const owner = getUsableOwner(getOwnerWindow());
        if (!owner) return null;

        const result = await runExclusive(() => dialog.showOpenDialog(owner, {
          title: payload.title,
          defaultPath: payload.suggestedFileName || undefined,
          filters: toElectronFilters(payload.fileTypes),
          properties: ['openFile'],
        }));
        const selectedPath = result.filePaths[0];
        return result.canceled || result.filePaths.length !== 1 || !isUsableSelectedPath(selectedPath)
          ? null
          : selectedPath;
      }

      case 'host.dialog.confirm': {
        const payload = parseConfirmationRequest(request.payload);
        if (options.forceSafeCancellation) return false;
        const owner = getUsableOwner(getOwnerWindow());
        if (!owner) return false;

        const result = await runExclusive(() => dialog.showMessageBox(owner, {
          type: payload.isWarning ? 'warning' : 'question',
          title: payload.title,
          message: payload.message,
          buttons: ['取消', '确定'],
          defaultId: 0,
          cancelId: 0,
          noLink: true,
        }));
        return result.response === 1;
      }

      case 'host.startup.set': {
        assertExactProperties(request.payload, ['enabled'], request.method);
        if (typeof request.payload.enabled !== 'boolean') {
          invalid('enabled must be a boolean value.');
        }
        if (options.forceSafeCancellation || !options.startupRegistration) {
          throw new HostRequestHandlerError(
            'host_unsupported',
            'Startup registration is unavailable in this Electron host.',
          );
        }

        const actual = options.startupRegistration.setEnabled(request.payload.enabled);
        if (actual !== request.payload.enabled) {
          throw new HostRequestHandlerError(
            'startup_registration_mismatch',
            'Windows did not apply the requested startup registration state.',
          );
        }
        return actual;
      }
    }
  };
}

function parseFileDialogRequest(
  value: JsonObject,
  method: Extract<HostRequestMethod, 'host.dialog.saveFile' | 'host.dialog.openFile'>,
): HostFileDialogRequest {
  assertExactProperties(value, [
    'title',
    'suggestedFileName',
    'defaultExtension',
    'fileTypes',
    'checkFileExists',
    'overwritePrompt',
  ], method);

  const title = readSingleLineText(value.title, 'title', MAX_TITLE_LENGTH, false);
  const suggestedFileName = readFileName(value.suggestedFileName, method === 'host.dialog.openFile');
  const defaultExtension = readExtension(value.defaultExtension);
  if (typeof value.checkFileExists !== 'boolean' || typeof value.overwritePrompt !== 'boolean') {
    invalid('File-dialog flags must be boolean values.');
  }
  if (!Array.isArray(value.fileTypes)
      || value.fileTypes.length === 0
      || value.fileTypes.length > MAX_FILE_TYPES) {
    invalid(`fileTypes must contain between 1 and ${MAX_FILE_TYPES} entries.`);
  }

  const fileTypes = value.fileTypes.map((candidate, index) => parseFileType(candidate, index));
  const expectedPattern = `*${defaultExtension}`.toLowerCase();
  if (!fileTypes.some((fileType) => fileType.patterns.some(
    (pattern) => pattern === '*' || pattern.toLowerCase() === expectedPattern,
  ))) {
    invalid('defaultExtension must be represented by one of the file filters.');
  }

  return {
    title,
    suggestedFileName,
    defaultExtension,
    fileTypes,
    checkFileExists: value.checkFileExists,
    overwritePrompt: value.overwritePrompt,
  };
}

function parseFileType(value: unknown, index: number): HostFileType {
  if (!isRecord(value)) invalid(`fileTypes[${index}] must be an object.`);
  assertExactProperties(value, ['name', 'patterns'], `fileTypes[${index}]`);
  const name = readSingleLineText(value.name, `fileTypes[${index}].name`, MAX_FILE_TYPE_NAME_LENGTH, false);
  if (!Array.isArray(value.patterns)
      || value.patterns.length === 0
      || value.patterns.length > MAX_PATTERNS_PER_FILE_TYPE) {
    invalid(
      `fileTypes[${index}].patterns must contain between 1 and ${MAX_PATTERNS_PER_FILE_TYPE} entries.`,
    );
  }

  const patterns = value.patterns.map((candidate, patternIndex) => {
    if (typeof candidate !== 'string'
        || (candidate !== '*' && !FILTER_PATTERN.test(candidate))) {
      invalid(`fileTypes[${index}].patterns[${patternIndex}] is invalid.`);
    }
    return candidate;
  });
  return { name, patterns };
}

function parseConfirmationRequest(value: JsonObject): HostConfirmationRequest {
  assertExactProperties(value, ['title', 'message', 'isWarning'], 'host.dialog.confirm');
  const title = readSingleLineText(value.title, 'title', MAX_TITLE_LENGTH, false);
  const message = readMessage(value.message);
  if (typeof value.isWarning !== 'boolean') invalid('isWarning must be a boolean value.');
  return { title, message, isWarning: value.isWarning };
}

function readSingleLineText(
  value: unknown,
  field: string,
  maximumLength: number,
  allowEmpty: boolean,
): string {
  if (typeof value !== 'string'
      || (!allowEmpty && value.trim().length === 0)
      || value.length > maximumLength
      || INVALID_SINGLE_LINE_TEXT.test(value)) {
    invalid(`${field} must be ${allowEmpty ? '' : 'non-empty, '}single-line text up to ${maximumLength} characters.`);
  }
  return value;
}

function readMessage(value: unknown): string {
  if (typeof value !== 'string'
      || value.trim().length === 0
      || value.length > MAX_MESSAGE_LENGTH
      || value.includes('\0')) {
    invalid(`message must be non-empty text up to ${MAX_MESSAGE_LENGTH} characters.`);
  }
  return value;
}

function readFileName(value: unknown, allowEmpty: boolean): string {
  if (typeof value !== 'string') invalid('suggestedFileName must be text.');
  if (allowEmpty && value.length === 0) return value;
  if (value.length === 0
      || value.length > MAX_FILE_NAME_LENGTH
      || INVALID_FILE_NAME.test(value)
      || value === '.'
      || value === '..'
      || value.endsWith('.')
      || value.endsWith(' ')
      || WINDOWS_RESERVED_FILE_NAME.test(value)) {
    invalid('suggestedFileName must be a safe base file name.');
  }
  return value;
}

function readExtension(value: unknown): string {
  if (typeof value !== 'string'
      || value.length > MAX_EXTENSION_LENGTH
      || !EXTENSION_PATTERN.test(value)) {
    invalid('defaultExtension must be a dot-prefixed file extension.');
  }
  return value;
}

function assertExactProperties(value: JsonObject, expected: readonly string[], field: string): void {
  const actual = Object.keys(value).sort();
  const required = [...expected].sort();
  if (actual.length !== required.length
      || actual.some((property, index) => property !== required[index])) {
    invalid(`${field} contains missing or unsupported properties.`);
  }
}

function toElectronFilters(fileTypes: HostFileType[]): Array<{ name: string; extensions: string[] }> {
  return fileTypes.map((fileType) => ({
    name: fileType.name,
    extensions: fileType.patterns.map((pattern) => pattern === '*' ? '*' : pattern.slice(2)),
  }));
}

function withDefaultExtension(fileName: string, extension: string): string {
  if (fileName.length === 0 || fileName.toLowerCase().endsWith(extension.toLowerCase())) {
    return fileName;
  }
  return `${fileName}${extension}`;
}

function getUsableOwner(owner: BrowserWindow | undefined): BrowserWindow | undefined {
  return owner && !owner.isDestroyed() ? owner : undefined;
}

function isUsableSelectedPath(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && !value.includes('\0');
}

function invalid(message: string): never {
  throw new HostRequestHandlerError('invalid_host_request', message);
}
