'use strict';

const supportedMajor = 22;
const actualVersion = process.versions.node;
const actualMajor = Number.parseInt(actualVersion.split('.')[0], 10);

if (actualMajor !== supportedMajor) {
  console.error(
    `CodexU Electron requires Node.js ${supportedMajor}.x; the npm script resolved Node.js ${actualVersion}. `
    + 'Switch the active PATH to Node.js 22 before running Electron commands.',
  );
  process.exitCode = 1;
} else {
  console.log(`Node.js ${actualVersion} verified for CodexU Electron.`);
}
