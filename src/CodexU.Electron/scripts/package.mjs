import { cpSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { FuseV1Options, FuseVersion, flipFuses } from '@electron/fuses';
import { packager } from '@electron/packager';

import { resolvePinnedElectronVersion } from './release-integrity.mjs';

const electronRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const projectRoot = path.resolve(electronRoot, '../..');
const rendererDirectory = path.join(projectRoot, 'src/CodexU.Web/dist');
const backendDirectory = path.join(electronRoot, 'backend');
const appIconDirectory = path.join(projectRoot, 'src/CodexU.App/Assets');
const outputDirectory = path.join(electronRoot, 'out');
const packageManifest = JSON.parse(readFileSync(path.join(electronRoot, 'package.json'), 'utf8'));
const buildProps = readFileSync(path.join(projectRoot, 'Directory.Build.props'), 'utf8');
const electronVersion = resolvePinnedElectronVersion(electronRoot);

function readBuildProperty(name) {
  const match = buildProps.match(new RegExp(`<${name}>\\s*([^<]+?)\\s*</${name}>`));
  if (!match) throw new Error(`Directory.Build.props does not define ${name}.`);
  return match[1];
}

function requireFile(filePath, description) {
  if (!existsSync(filePath) || !statSync(filePath).isFile()) {
    throw new Error(`${description} was not found: ${filePath}`);
  }
}

function requireDirectory(directoryPath, description) {
  if (!existsSync(directoryPath) || !statSync(directoryPath).isDirectory()) {
    throw new Error(`${description} was not found: ${directoryPath}`);
  }
}

function copyDirectory(source, destination, filter) {
  cpSync(source, destination, {
    recursive: true,
    filter: (candidate) => candidate === source || filter(candidate),
  });
}

const productVersion = readBuildProperty('Version');
const fileVersion = readBuildProperty('FileVersion');
const copyright = readBuildProperty('Copyright');
if (packageManifest.version !== productVersion) {
  throw new Error(`Version mismatch: .NET=${productVersion}, Electron=${packageManifest.version}`);
}
if (!/^\d+\.\d+\.\d+\.\d+$/.test(fileVersion)) {
  throw new Error(`FileVersion must contain four numeric parts: ${fileVersion}`);
}

const requiredFiles = [
  [path.join(rendererDirectory, 'index.html'), 'Built Vue renderer entry point'],
  [path.join(backendDirectory, 'CodexU.Sidecar.exe'), 'Published self-contained .NET sidecar'],
  [path.join(appIconDirectory, 'AppIcon.ico'), 'Windows application icon'],
  [path.join(appIconDirectory, 'AppIcon.png'), 'Tray fallback icon'],
  [path.join(projectRoot, 'LICENSE'), 'Project license'],
  [path.join(projectRoot, 'THIRD-PARTY-NOTICES.md'), 'Third-party notices'],
  [path.join(projectRoot, 'THIRD-PARTY-INVENTORY.md'), 'Third-party inventory'],
  [path.join(projectRoot, 'THIRD-PARTY-LICENSES.txt'), 'Third-party license bundle'],
];
for (const [filePath, description] of requiredFiles) requireFile(filePath, description);
requireDirectory(path.join(projectRoot, 'LICENSES'), 'Retained license directory');

const stagingRoot = mkdtempSync(path.join(tmpdir(), 'codexu-electron-resources-'));
try {
  copyDirectory(rendererDirectory, path.join(stagingRoot, 'dist'), candidate => !candidate.endsWith('.map'));
  copyDirectory(backendDirectory, path.join(stagingRoot, 'backend'), candidate => {
    const name = path.basename(candidate).toLowerCase();
    return name !== '.gitignore' && name !== '.gitkeep' && !name.endsWith('.pdb');
  });
  mkdirSync(path.join(stagingRoot, 'Assets'));
  copyFileSync(path.join(appIconDirectory, 'AppIcon.ico'), path.join(stagingRoot, 'Assets/AppIcon.ico'));
  copyFileSync(path.join(appIconDirectory, 'AppIcon.png'), path.join(stagingRoot, 'Assets/AppIcon.png'));

  for (const legalFile of ['LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-INVENTORY.md', 'THIRD-PARTY-LICENSES.txt']) {
    copyFileSync(path.join(projectRoot, legalFile), path.join(stagingRoot, legalFile));
  }
  copyDirectory(path.join(projectRoot, 'LICENSES'), path.join(stagingRoot, 'LICENSES'), () => true);

  const fuseConfig = {
    version: FuseVersion.V1,
    strictlyRequireAllFuses: true,
    [FuseV1Options.RunAsNode]: false,
    [FuseV1Options.EnableCookieEncryption]: true,
    [FuseV1Options.EnableNodeOptionsEnvironmentVariable]: false,
    [FuseV1Options.EnableNodeCliInspectArguments]: false,
    [FuseV1Options.EnableEmbeddedAsarIntegrityValidation]: true,
    [FuseV1Options.OnlyLoadAppFromAsar]: true,
    [FuseV1Options.LoadBrowserProcessSpecificV8Snapshot]: false,
    [FuseV1Options.GrantFileProtocolExtraPrivileges]: false,
    [FuseV1Options.WasmTrapHandlers]: true,
  };

  const packagePaths = await packager({
    dir: electronRoot,
    name: 'CodexU',
    executableName: 'CodexU',
    platform: 'win32',
    arch: 'x64',
    electronVersion,
    out: outputDirectory,
    overwrite: true,
    asar: true,
    prune: true,
    icon: path.join(appIconDirectory, 'AppIcon.ico'),
    appVersion: productVersion,
    buildVersion: fileVersion,
    appCopyright: copyright,
    win32metadata: {
      CompanyName: 'codexU contributors',
      FileDescription: 'codexU desktop application',
      InternalName: 'CodexU',
      OriginalFilename: 'CodexU.exe',
      ProductName: 'codexU',
      'requested-execution-level': 'asInvoker',
    },
    extraResource: [
      path.join(stagingRoot, 'dist'),
      path.join(stagingRoot, 'backend'),
      path.join(stagingRoot, 'Assets'),
      path.join(stagingRoot, 'LICENSE'),
      path.join(stagingRoot, 'THIRD-PARTY-NOTICES.md'),
      path.join(stagingRoot, 'THIRD-PARTY-INVENTORY.md'),
      path.join(stagingRoot, 'THIRD-PARTY-LICENSES.txt'),
      path.join(stagingRoot, 'LICENSES'),
    ],
    ignore: [
      /^\/src(?:\/|$)/,
      /^\/scripts(?:\/|$)/,
      /^\/test(?:\/|$)/,
      // The host has no runtime npm dependencies. Revisit this rule before adding any.
      /^\/node_modules(?:\/|$)/,
      /^\/backend(?:\/|$)/,
      /^\/out(?:\/|$)/,
      /^\/\.gitignore$/,
      /^\/README\.md$/,
      /^\/package-lock\.json$/,
      /^\/tsconfig\.json$/,
      /\.map$/,
    ],
    afterComplete: [async ({ buildPath, platform }) => {
      const executable = path.join(buildPath, platform === 'win32' ? 'CodexU.exe' : 'CodexU');
      await flipFuses(executable, fuseConfig);
    }],
  });

  if (packagePaths.length !== 1) {
    throw new Error(`Expected one packaged application, received ${packagePaths.length}.`);
  }
  const expectedPath = path.join(outputDirectory, 'CodexU-win32-x64');
  if (path.resolve(packagePaths[0]) !== path.resolve(expectedPath)) {
    throw new Error(`Unexpected package output: ${packagePaths[0]}`);
  }
  console.log(`Packaged Electron application: ${packagePaths[0]}`);
} finally {
  rmSync(stagingRoot, { recursive: true, force: true });
}
