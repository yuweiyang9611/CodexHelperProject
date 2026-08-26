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

The reverse-RPC allow-list contains only `host.dialog.saveFile`,
`host.dialog.openFile`, and `host.dialog.confirm`. Successful file-dialog payloads are
the selected path or `null`; confirmation payloads are booleans. A failed response
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

The same reconciliation runs for `settings.changed`. On Windows this owns the tray
menu (open, refresh, compact mode, and graceful exit), close-to-tray behavior, the
configured global shortcut, compact window sizing, native light/dark/system theme,
and packaged startup registration. A second launch or the shortcut restores and
focuses the existing window.

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

Use Node.js 22.x for the Electron workspace. The `node` executable actually resolved
from `PATH` must report version 22; invoking a version-specific `npm.cmd` by absolute
path is insufficient when its child scripts still resolve another Node version. Build
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
  --output src\CodexU.Electron\backend
cd src\CodexU.Electron
npm.cmd ci
npm start
```

Dependencies are exact-version pinned. Do not commit `node_modules`, generated
`dist`, Forge `out`, or staged backend binaries.

## Packaging

After staging the renderer and sidecar as shown above, run with Node.js 22.x:

```powershell
npm run package
npm run make
```

Forge packages the Electron application into ASAR, copies Vue assets and the backend
outside ASAR, and applies restrictive Electron fuses. The current maker emits a
Windows ZIP suitable for the existing installer pipeline to consume later.

The packaging hook uses `@electron/fuses` directly and enables
`strictlyRequireAllFuses`. Every fuse in Electron's current V1 wire is explicit, so a
future Electron fuse addition fails packaging instead of silently inheriting an
unknown default. Node execution/option/inspector entry points and extra `file://`
privileges are disabled; cookie encryption and ASAR integrity enforcement are enabled;
`WasmTrapHandlers` remains enabled.

Node.js 24.16 has an upstream child-process regression that can make Electron Forge
stop silently during finalization ([Electron Forge #4282](https://github.com/electron/forge/issues/4282),
[Node.js #63581](https://github.com/nodejs/node/issues/63581)), so packaging is
intentionally pinned to Node 22.x.

## Migration limitations

The WPF build remains the official release. Before Electron replaces it, startup
registration needs a correlated result so backend settings can roll back when the OS
operation fails; window bounds need work-area/DPI clamping; Windows notifications,
the status strip, and desktop mode still need native-host implementations; and the
installer plus Electron/Chromium license inventory must be completed. The shipped ASAR
has no runtime npm dependency tree. The build-only dependency graph pins patched
`tar@7.5.22` and `tmp@0.2.7` releases through npm overrides; a clean install, package,
and packaged smoke test cover those overrides. The remaining audit report is one
upstream [`extract-zip@2.0.1` symlink-traversal advisory](https://github.com/advisories/GHSA-jmr9-qjv8-65gv) propagated through Electron
Packager/Forge. No patched `extract-zip` release exists yet, so Electron stays blocked
from public release until Forge replaces it or upstream ships a fix. Packaging must use
the exact lockfile and trusted Electron artifacts in the meantime.
Linux has not yet been validated.

## Automated checks

```powershell
npm test
npm run smoke
.\out\CodexU-win32-x64\CodexU.exe --smoke-test
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
