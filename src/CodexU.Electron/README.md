# CodexU Electron host

This directory is the Windows-first Electron shell for the existing Vue renderer and
the self-contained .NET backend. It deliberately has no project reference to the WPF
or Avalonia hosts.

## Runtime layout

- Development renderer: `../CodexU.Web/dist`
- Development sidecar: `CODEXU_SIDECAR_PATH`, `--sidecar-path=...`, or
  `backend/CodexU.Sidecar.exe`
- Packaged renderer: `process.resourcesPath/dist`
- Packaged sidecar: `process.resourcesPath/backend/CodexU.Sidecar.exe`
- Packaged window/tray icons: `process.resourcesPath/Assets/AppIcon.ico` and
  `AppIcon.png`

The backend transport is a private stdin/stdout stream. Every message is UTF-8 JSON
preceded by a four-byte unsigned little-endian length. The maximum JSON payload is
1 MiB. Backend logs must go to stderr because stdout is reserved for protocol frames.

Normal UI calls travel from Electron to the sidecar as correlated `request` / `response`
messages. Native dialogs use the same private stream in the reverse direction:

```text
Sidecar -> Electron: { version: 1, id, type: "hostRequest", method, payload }
Electron -> Sidecar: { version: 1, id, type: "hostResponse", ok, payload | error }
```

The reverse-RPC allow-list contains `host.dialog.saveFile`,
`host.dialog.openFile`, `host.dialog.confirm`, and `host.startup.set`. Successful
file-dialog payloads are the selected path or `null`; confirmation and startup-state
payloads are booleans. The startup method writes the packaged Windows login item and
reads it back before the Sidecar commits settings, so a mismatch or native failure rolls
the setting back. A failed response
contains `{ code, message }` in `error` and no payload. Electron validates every field,
allows only one real native dialog at a time, and keeps host responses inside the main
process so the renderer cannot forge them.

After a handshake that advertises both `host.rpc.v1` and `host.state.v1`, Electron
fetches `settings.get` before creating the renderer. It consumes only the validated `closeToTray`,
`compactMode`, `theme`, `startAtLogin`, and `globalHotKey` fields, applies the native
shell state, and sends this exact one-way status message back to the sidecar:

```text
Electron -> Sidecar: { version: 1, type: "hostState", globalHotKeyRegistered: boolean }
```

The same native-settings projection runs for `settings.changed`. On Windows the host
owns the tray menu (open, refresh, compact mode, and graceful exit), close-to-tray
behavior, the configured global shortcut, and compact/native-theme state. Startup
registration is committed through the verified reverse RPC described above. The host
also restores window bounds against the saved display work area and presents quota
alerts through the packaged Windows notification identity. A second launch, a
notification activation, or the shortcut restores and focuses the existing window.

Unexpected Sidecar and renderer exits are supervised independently with bounded
exponential backoff and a circuit breaker. Successful Sidecar recovery reloads the Vue
renderer against the new private transport. Main-process, renderer, and Sidecar failures
are written to bounded rotating logs under Electron `userData/logs`; credentials, account
identities, and user-profile paths are redacted. The .NET diagnostics export includes a
sanitized tail of those logs.

The .NET sidecar owns the automatic refresh schedule. Its first refresh waits for the
configured interval, a settings change restarts that interval immediately, and only
one refresh can run at a time. `ApplicationSession.SnapshotChanged` is the single
projection point for `usage.snapshotChanged`, so manual refresh, automatic refresh,
runtime changes, imports, restores, and index rebuilds do not publish duplicate
snapshot events.

Graceful shutdown has one absolute deadline covering outstanding reverse-RPC
cancellation, transmission of the shutdown frame, acknowledgement, stdin closure,
close grace, and any wait after forced termination. No later than the deadline Electron
signals termination when needed and returns without adding a fixed cleanup delay, even
if the process never reports `close`. If a fatal exit arrives after graceful quit has
already been authorized, Electron disposes native shell resources before forcing the
higher non-zero exit code.

## Development

Use Node.js 22.23.2 for the Electron workspace. The `node` executable actually resolved
from `PATH` must report that exact version; invoking a version-specific `npm.cmd` by
absolute path is insufficient when its child scripts still resolve another Node version. Build
the Vue renderer, publish the self-contained sidecar, and install the locked Electron
dependencies from the repository root:

```powershell
cd src\CodexU.Web
npm.cmd ci
npm.cmd run build
cd ..\..
dotnet publish src\CodexU.Sidecar\CodexU.Sidecar.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishReadyToRun=false `
  -p:DebugSymbols=false `
  -p:DebugType=None `
  --output src\CodexU.Electron\backend
cd src\CodexU.Electron
npm.cmd ci
npm start
```

Dependencies are exact-version pinned. Do not commit `node_modules`, generated
`dist`/`out`, or staged backend binaries.

## Packaging

After staging the renderer and sidecar as shown above, run with Node.js 22.23.2:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ..\..\tools\Generate-ThirdPartyInventory.ps1
npm run package
```

The legal-payload gate reruns the inventory generator under Windows PowerShell into an
isolated temporary directory, compares all four generated files byte-for-byte, rejects
unresolved licenses, and verifies that package.json, package-lock and the installed
Electron runtime all name the same exact version. The package script then calls the
stable `@electron/packager@20.3.0` API directly with that verified Electron version. It packages
the host into ASAR, copies the Vue renderer and a PDB-free self-contained backend outside
ASAR, embeds Windows version metadata, and applies restrictive Electron fuses. It also
stages the project license, generated dependency inventory/license bundle, retained .NET
runtime notices and upstream licenses under `resources/`. Electron's own `LICENSE` and
complete `LICENSES.chromium.html` remain at the package root.

The output is `out/CodexU-win32-x64`. GitHub Release signs `CodexU.exe` and
`resources/backend/CodexU.Sidecar.exe`, smoke-tests that exact directory, builds and tests
the Inno Setup installer, then compresses the already-signed directory into the public ZIP.
There is no separate maker step that can silently recreate and discard signed binaries.

The packaging hook uses `@electron/fuses` directly and enables
`strictlyRequireAllFuses`. Every fuse in Electron's current V1 wire is explicit, so a
future Electron fuse addition fails packaging instead of silently inheriting an
unknown default. Node execution/option/inspector entry points and extra `file://`
privileges are disabled; cookie encryption and embedded ASAR integrity validation are
enabled; `WasmTrapHandlers` remains enabled. The embedded ASAR integrity fuse is not a
general integrity guarantee for the external Vue or Sidecar resources; Authenticode and
release checksums cover the Windows distribution boundary.

The complete Electron workspace, including build tooling, must pass
`npm audit --audit-level=high`. Forge 7 was removed because it pinned Electron Packager 18
and the vulnerable `extract-zip@2.0.1` build path. Packager 20 uses the hardened
`@electron-internal/extract-zip` implementation; CI inspects the locked package map and
fails if the legacy package is reintroduced or the hardened implementation disappears.

## Migration limitations

The v0.5.0 release remains the legacy WPF build, while v0.6.0-beta.1 is the first
Electron prerelease. This packaging readiness does not imply
complete product parity: the status strip and desktop mode still need native-host
implementations. Startup registration rollback, window work-area/DPI recovery, and
Windows notifications are implemented. The first Electron version should still be
validated as a prerelease. The shipped ASAR has no runtime npm dependency tree. Linux has
not yet been validated.

## Automated checks

```powershell
npm test
npm run smoke
cd ..\..
.\tools\Test-PackagedElectron.ps1 `
  -ApplicationDirectory src\CodexU.Electron\out\CodexU-win32-x64
```

`--smoke-test` keeps the window hidden, waits for the `app://codexu` page to load,
requires the handshake to advertise both `host.rpc.v1` and `host.state.v1`, and invokes `app.initialize` through
the sandbox bridge. It verifies the application capability list contains
`nativeDialogs`, then invokes `rates.export` end to end. The smoke-only host returns a
deterministic safe cancellation (`success: false`, with no output path), so this check
never opens a real dialog or writes an export file. Smoke mode also reports
`globalHotKeyRegistered: false` without creating a tray, registering a shortcut,
changing startup registration, or showing native notifications. Success prints
`CODEXU_ELECTRON_SMOKE_OK`; failures print `CODEXU_ELECTRON_SMOKE_FAILED` and exit
non-zero. Any unexpected `settings.changed` event, or `host.*` event other than the
`host.webReady` barrier, also fails smoke without executing the requested window, URL,
startup, theme, or other OS action. A second-instance signal fails smoke without
showing the window, while an incidental application activation is ignored.
