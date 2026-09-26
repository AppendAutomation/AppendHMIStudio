# Building Append HMI Studio

Everything builds on Linux, including the Windows installer. There is no wine
and no Windows machine involved. Windows and macOS hosts can run the same npm
scripts; only Windows x64 and Linux x64 packages are produced.

## Requirements

- Node.js 22.12 or later (CI uses 24) and npm
- The .NET 8 SDK or later, for `hmi-comms` (the PLC comms server, see `comms/README.md`)
- git with submodules: `git clone --recursive …`, or `git submodule update --init --recursive` after a plain clone

The `drawio` submodule is Append Automation's fork of the draw.io editor,
branch `hmi`. `comms/lib` holds the cslogix and cscomm3_slc libraries.

## Run from source

```
npm install
npm run build-comms        # hmi-comms for this machine, into comms/publish/
npm start
```

- `HMI_ENV=dev npm start` opens DevTools and loads the editor's unminified sources.
- `urlParams.json` with `{"hmitest": 1}` next to `package.json` runs the in-app self test (results logged as `HMITEST …`).

Tests:

```
npm test                   # Node unit tests (main process, packaging, Publish)
npm run test-comms         # hmi-comms xUnit suites
```

## Packages

| Command | Output (in `dist/`) |
|---|---|
| `npm run dist-win` | `Append-HMI-Studio-<version>-Setup.exe`, Windows x64, per-machine |
| `npm run dist-win -- --scope user` | `…-user-Setup.exe`, per-user, no administrator rights |
| `npm run dist-linux` | `Append-HMI-Studio-<version>-x86_64.AppImage` and `…-amd64.deb` |

The version comes from `package.json`.

### `dist-win`

1. `npm run fetch-nsis` fetches the NSIS compiler (the bundle electron-builder uses, checksum-verified) into `build/nsis/`.
2. `npm run build-win-runtime` builds `hmi-comms.exe` and the unpacked Windows app into `dist-win-runtime/win-unpacked`. This is plain electron-builder with no NSIS step, so no wine is needed.
3. `NsisScript.js` (editor mode) generates the installer script and the bundled makensis compiles it. The installer:
   - creates Start menu and desktop shortcuts;
   - registers `.ahmi`;
   - adds an Add/Remove Programs entry;
   - asks for a running copy to be closed, and stops it only in a silent install;
   - supports silent `/S`.

Options:
- `--compression fast` builds in under a minute but produces a larger installer.
- `--skip-build` reuses an existing `dist-win-runtime`.

### `dist-linux`

It fetches NSIS, builds `hmi-comms` for linux-x64, builds the Windows runtime template (skip it with `--skip-runtime` when `dist-win` has just built it), then runs electron-builder with `electron-builder-linux.json`.

The Linux packages carry the Windows app (`resources/runtime/win-x64`) and NSIS, so HMI > Publish works from Linux. That adds about 180 MB to the packages.

### Icons

`build/icon.svg` (and `build/icon-small.svg` for 16–32 px) are the sources. `npm run make-icons` regenerates the PNGs, `icon.png`, `icon.ico` and the editor's logo; it needs inkscape and ImageMagick. The outputs are committed.

## Code signing

Builds are unsigned by default, so Windows SmartScreen warns on first run.
`build/sign-trusted.mjs` signs through Azure Trusted Signing when these are all set:
- `HMI_SIGNING_ACCOUNT`
- `HMI_SIGNING_PROFILE`
- `HMI_SIGNING_ENDPOINT`
- `TRUSTED_SIGNING_DLIB_PATH`
- `SIGNTOOL_PATH`
- the Azure credentials: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET`

`signtool` is Windows-only. Signing from Linux would need osslsigncode or jsign instead, which is not set up.

## Continuous integration

`.github/workflows/release.yml` runs on a `v*` tag or by hand, on ubuntu-latest:
1. `npm test`;
2. `dist-win`;
3. `dist-linux`.

The packages become workflow artifacts; a tag also attaches them to a draft GitHub release. See `doc/RELEASE_PROCESS.md`.
