'use strict';

const supportedVersion = '22.23.2';
const actualVersion = process.versions.node;

if (actualVersion !== supportedVersion) {
  console.error(
    `CodexU Electron requires Node.js ${supportedVersion}; the npm script resolved Node.js ${actualVersion}. `
    + `Switch the active PATH to Node.js ${supportedVersion} before running Electron commands.`,
  );
  process.exitCode = 1;
} else {
  console.log(`Node.js ${actualVersion} verified for CodexU Electron.`);
}
