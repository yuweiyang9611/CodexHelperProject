import { readFileSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

export const generatedLegalPayloadPaths = Object.freeze([
  'THIRD-PARTY-INVENTORY.md',
  'THIRD-PARTY-LICENSES.txt',
  path.join('LICENSES', 'dotnet-runtime-MIT.txt'),
  path.join('LICENSES', 'dotnet-runtime-ThirdPartyNotices.txt'),
]);

function readJson(filePath, description) {
  let text;
  try {
    text = readFileSync(filePath, 'utf8');
  } catch (error) {
    throw new Error(`${description} was not found or could not be read: ${filePath}`, { cause: error });
  }

  try {
    return JSON.parse(text);
  } catch (error) {
    throw new Error(`${description} is not valid JSON: ${filePath}`, { cause: error });
  }
}

export function resolvePinnedElectronVersion(electronRoot) {
  const manifestPath = path.join(electronRoot, 'package.json');
  const lockPath = path.join(electronRoot, 'package-lock.json');
  const installedManifestPath = path.join(electronRoot, 'node_modules', 'electron', 'package.json');
  const manifest = readJson(manifestPath, 'Electron host package manifest');
  const lock = readJson(lockPath, 'Electron host package lock');
  const installedManifest = readJson(installedManifestPath, 'Installed Electron package manifest');

  const declaredVersion = manifest.devDependencies?.electron;
  if (typeof declaredVersion !== 'string' ||
      !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$/.test(declaredVersion)) {
    throw new Error(`Electron must be declared as an exact version; found '${declaredVersion ?? ''}'.`);
  }

  const versions = [
    ['package-lock root declaration', lock.packages?.['']?.devDependencies?.electron],
    ['package-lock installed entry', lock.packages?.['node_modules/electron']?.version],
    ['installed Electron package', installedManifest.version],
  ];
  for (const [source, version] of versions) {
    if (version !== declaredVersion) {
      throw new Error(
        `Electron version mismatch: package.json declares '${declaredVersion}', but ${source} reports '${version ?? ''}'.`,
      );
    }
  }

  return declaredVersion;
}

async function readRequiredPayloadFile(root, relativePath, description) {
  const filePath = path.join(root, relativePath);
  let contents;
  try {
    contents = await readFile(filePath);
  } catch (error) {
    throw new Error(`${description} is missing or unreadable: ${filePath}`, { cause: error });
  }
  if (contents.length === 0) {
    throw new Error(`${description} is empty: ${filePath}`);
  }
  return contents;
}

export async function assertLegalPayloadIsCurrent(projectRoot, generatedRoot) {
  for (const relativePath of generatedLegalPayloadPaths) {
    const tracked = await readRequiredPayloadFile(projectRoot, relativePath, 'Tracked legal payload file');
    const generated = await readRequiredPayloadFile(generatedRoot, relativePath, 'Regenerated legal payload file');
    if (!tracked.equals(generated)) {
      throw new Error(
        `${relativePath} is stale or incomplete; regenerate the third-party inventory before packaging.`,
      );
    }
  }

  const inventory = await readFile(path.join(projectRoot, 'THIRD-PARTY-INVENTORY.md'), 'utf8');
  if (inventory.split(/\r?\n/).some((line) => line.includes('| UNKNOWN - review required |'))) {
    throw new Error('Third-party inventory contains UNKNOWN - review required and cannot be packaged.');
  }
}
