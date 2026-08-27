import { readFile } from 'node:fs/promises';

const lockPath = new URL('../package-lock.json', import.meta.url);
const lock = JSON.parse(await readFile(lockPath, 'utf8'));

if (!lock.packages || typeof lock.packages !== 'object') {
  throw new Error('package-lock.json must expose the complete npm packages map.');
}

const packagePaths = Object.keys(lock.packages).map((entry) => entry.replaceAll('\\', '/'));
const legacyExtractZip = packagePaths.filter((entry) =>
  /(?:^|\/)node_modules\/extract-zip$/.test(entry),
);
const hardenedExtractZip = packagePaths.filter((entry) =>
  /(?:^|\/)node_modules\/@electron-internal\/extract-zip$/.test(entry),
);

if (legacyExtractZip.length > 0) {
  throw new Error(
    `Forbidden legacy extract-zip dependency found: ${legacyExtractZip.join(', ')}`,
  );
}

if (hardenedExtractZip.length === 0) {
  throw new Error('Expected @electron-internal/extract-zip dependency is missing.');
}

console.log(
  `Electron dependency policy OK: ${hardenedExtractZip.length} hardened extract-zip package(s), no legacy extract-zip package.`,
);
