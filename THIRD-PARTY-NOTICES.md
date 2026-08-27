# Third-party notices

## codexU

This project references the interface and behavior of, and adapts the token-counter normalization and fork-session de-duplication approach introduced in version 1.1.5 of:

- Project: `shanggqm/codexU`
- Source: https://github.com/shanggqm/codexU
- Copyright (c) 2026 Guomeiqing
- License: MIT

The MIT copyright and permission notice from the upstream repository is retained in release output at:

- `LICENSES/shanggqm-codexU-MIT.txt`
- Source: https://github.com/shanggqm/codexU/blob/v1.1.5/LICENSE

## codexU-windows

This project also references the Windows feature set of:

- Project: `liu1198767931-bit/codexU-windows`
- Source: https://github.com/liu1198767931-bit/codexU-windows
- Copyright (c) 2026 codexU Windows contributors
- License: MIT

The corresponding MIT notice is retained at `LICENSES/liu-codexU-windows-MIT.txt` (source: https://github.com/liu1198767931-bit/codexU-windows/blob/f27427302390c408863c6b0c747f777bca9e3317/LICENSE).

This repository reimplements the desktop application as an Electron/Chromium host with a Vue renderer and a self-contained .NET 10 Sidecar. The upstream project remains acknowledged as a product and behavior reference.

## Runtime dependencies

The Electron runtime, the .NET Sidecar's NuGet dependencies and the Web renderer's production npm dependencies retain their respective licenses. `THIRD-PARTY-INVENTORY.md` records these shipped components and their declared license expressions. `THIRD-PARTY-LICENSES.txt` contains Electron's MIT license and the complete license and notice texts extracted from restored production packages; packages that publish only an SPDX expression receive the corresponding standard license text together with their package copyright metadata.

Electron retains its runtime license in `LICENSE` and the complete Chromium and bundled third-party notices in `LICENSES.chromium.html` at the packaged application root. The packaged project's own legal payload consists of `resources/LICENSE`, `resources/THIRD-PARTY-NOTICES.md`, `resources/THIRD-PARTY-INVENTORY.md`, `resources/THIRD-PARTY-LICENSES.txt` and `resources/LICENSES/`. The self-contained .NET runtime license and Microsoft's accompanying third-party notices are included there as `resources/LICENSES/dotnet-runtime-MIT.txt` and `resources/LICENSES/dotnet-runtime-ThirdPartyNotices.txt`.

## Inno Setup

The Windows Setup EXE is built with Inno Setup by Jordan Russell and Martijn Laan. Its license notice is retained at `LICENSES/Inno-Setup-license.txt`; Inno Setup is a build/distribution component and is not part of the NuGet/npm-generated inventory.
