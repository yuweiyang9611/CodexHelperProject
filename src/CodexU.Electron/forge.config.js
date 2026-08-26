const path = require('node:path');
const { statSync } = require('node:fs');
const { flipFuses, FuseVersion, FuseV1Options } = require('@electron/fuses');

const rendererDirectory = path.resolve(__dirname, '../CodexU.Web/dist');
const backendDirectory = path.resolve(__dirname, 'backend');
const appIconDirectory = path.resolve(__dirname, '../CodexU.App/Assets');

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

function requireRegularFile(filePath, description) {
  try {
    if (statSync(filePath).isFile()) return;
  } catch {
    // Report one stable, actionable error below.
  }
  throw new Error(`${description} was not found: ${filePath}`);
}

function resolveElectronExecutable(resourcesPath, platform) {
  const basePath = path.resolve(resourcesPath, '../..');
  if (platform === 'darwin' || platform === 'mas') {
    return path.join(basePath, 'MacOS', 'Electron');
  }

  return path.join(basePath, platform === 'win32' ? 'electron.exe' : 'electron');
}

/** @type {import('@electron-forge/shared-types').ForgeConfig} */
module.exports = {
  packagerConfig: {
    name: 'CodexU',
    executableName: 'CodexU',
    icon: path.join(appIconDirectory, 'AppIcon.ico'),
    asar: true,
    prune: true,
    extraResource: [
      rendererDirectory,
      backendDirectory,
      appIconDirectory,
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
      /^\/forge\.config\.js$/,
      /^\/package-lock\.json$/,
      /^\/tsconfig\.json$/,
      /\.map$/,
    ],
  },
  makers: [
    {
      name: '@electron-forge/maker-zip',
      platforms: ['win32'],
    },
  ],
  hooks: {
    packageAfterCopy: async (
      _forgeConfig,
      resourcesPath,
      _electronVersion,
      platform,
    ) => {
      await flipFuses(resolveElectronExecutable(resourcesPath, platform), fuseConfig);
    },
    prePackage: async (_forgeConfig, platform) => {
      requireRegularFile(
        path.join(rendererDirectory, 'index.html'),
        'Built Vue renderer entry point',
      );
      const sidecarName = platform === 'win32' ? 'CodexU.Sidecar.exe' : 'CodexU.Sidecar';
      requireRegularFile(
        path.join(backendDirectory, sidecarName),
        'Published self-contained .NET sidecar',
      );
      requireRegularFile(
        path.join(appIconDirectory, 'AppIcon.ico'),
        'Windows application icon',
      );
      requireRegularFile(
        path.join(appIconDirectory, 'AppIcon.png'),
        'Tray fallback icon',
      );
    },
  },
};
