import { spawnSync } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  assertLegalPayloadIsCurrent,
  resolvePinnedElectronVersion,
} from './release-integrity.mjs';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const electronRoot = path.dirname(scriptDirectory);
const projectRoot = path.resolve(electronRoot, '../..');
const generatorPath = path.join(projectRoot, 'tools', 'Generate-ThirdPartyInventory.ps1');
const electronVersion = resolvePinnedElectronVersion(electronRoot);
const generatedRoot = await mkdtemp(path.join(tmpdir(), 'codexu-legal-verify-'));

try {
  const powershell = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
  const generation = spawnSync(powershell, [
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    ...(process.platform === 'win32' ? ['-ExecutionPolicy', 'Bypass'] : []),
    '-File', generatorPath,
    '-ProjectRoot', projectRoot,
    '-OutputRoot', generatedRoot,
  ], {
    cwd: projectRoot,
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
    windowsHide: true,
  });

  if (generation.error) {
    throw new Error(`Unable to run the legal payload generator with ${powershell}.`, {
      cause: generation.error,
    });
  }
  if (generation.status !== 0) {
    const diagnostics = [generation.stdout, generation.stderr]
      .filter((value) => value?.trim())
      .join('\n')
      .trim();
    throw new Error(
      `Legal payload regeneration failed with exit code ${generation.status}.`
      + (diagnostics ? `\n${diagnostics}` : ''),
    );
  }

  await assertLegalPayloadIsCurrent(projectRoot, generatedRoot);
  console.log(`Complete legal payload and Electron ${electronVersion} version lock verified.`);
} finally {
  await rm(generatedRoot, { recursive: true, force: true });
}
