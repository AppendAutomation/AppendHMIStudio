# CLAUDE.md - AI Assistant Guide for Append HMI Studio

## Project Overview

Append HMI Studio (Append Automation) is an Electron desktop app for designing, running and publishing HMI (operator screen) applications for PLCs. It is built on the draw.io editor, which is Append Automation's fork, included as the `drawio` git submodule (branch `hmi`), and on drawio-desktop. Both are by JGraph Ltd under the Apache License 2.0.

- **Branding:** no draw.io branding may be shown to users (Apache 2.0 grants no trademark rights). The license, NOTICE and the attribution in Help > About must stay.
- **Repository:** https://github.com/AppendAutomation/AppendHMIStudio
- **License:** Apache 2.0 (`LICENSE`, `NOTICE`)
- **Version:** `package.json` `version` (the product's own; the editor core version is `drawio/VERSION`, shown in About)

## Quick Reference

```bash
git clone --recursive https://github.com/AppendAutomation/AppendHMIStudio.git
npm install
npm run build-comms        # hmi-comms PLC server (needs the .NET 8 SDK); --rid <rid> or --all
npm start                  # HMI_ENV=dev npm start for DevTools and unminified editor sources
npm test                   # Node unit tests
npm run test-comms         # hmi-comms xUnit suites
npm run dist-win           # Windows x64 installer (on Linux too; no wine) -> dist/
npm run dist-linux         # AppImage + deb -> dist/
npm run make-icons         # regenerate icons from build/icon.svg (inkscape + ImageMagick)
```

Build details: `doc/BUILDING.md`. Releases: `doc/RELEASE_PROCESS.md`.

## Project Structure

```
append-hmi-studio/
├── src/main/
│   ├── electron.js           # Main process: windows, IPC handlers, menus, export, runtime mode, publish IPC
│   ├── electron-preload.js   # IPC bridge with contextBridge
│   ├── brand.js              # Product identity (name, publisher, appId, exe names, URLs)
│   ├── args.js               # CLI argument definitions and parser
│   ├── window-bounds.js      # Saved window placement
│   ├── comms/                # hmi-comms supervisor (spawn/restart) and per-window WebSocket relay
│   ├── runtime/RuntimeMode.js # Run-only mode for published HMI packages
│   └── publish/              # HMI > Publish: Publisher.js, NsisScript.js (also the editor installer)
├── src/test/                 # npm test (Node's test runner; files listed in package.json)
├── drawio/                   # Submodule: the editor (fork). HMI code in src/main/webapp/js/hmi/
├── comms/                    # hmi-comms: .NET 8 PLC communications server (see comms/README.md)
│   ├── lib/                  # Submodules: cslogix, cscomm3_slc (AppendAutomation)
│   ├── scripts/publish.mjs   # npm run build-comms
│   └── publish/              # Build output (git-ignored), shipped as extraResources
├── scripts/
│   ├── fetch-nsis.mjs        # NSIS for installers -> build/nsis (git-ignored)
│   ├── build-win-runtime.mjs # Unpacked Windows app -> dist-win-runtime (git-ignored)
│   ├── dist-win.mjs          # Windows installer
│   ├── dist-linux.mjs        # Linux packages
│   └── make-icons.mjs        # Icons from build/icon.svg
├── build/                    # icon.svg/icon-small.svg and generated icons, fuses.mjs, sign-trusted.mjs
├── doc/                      # BUILDING.md, RELEASE_PROCESS.md, HMI_PUBLISH.md
├── electron-builder-win.json   # Windows x64 (unpacked; our NSIS makes the installer)
├── electron-builder-linux.json # Linux x64 AppImage + deb
└── package.json
```

## Code Style

- **ES6 modules** in the main process; the webapp (drawio, js/hmi) is ES5-style globals
- **Tab indentation**, **Allman brace style**, camelCase / PascalCase
- No ESLint/Prettier - match the surrounding code
- Sparse comments; code clarity preferred

## Branding

- **Identity sources:**
  - main process: `src/main/brand.js`;
  - packaging: package.json (`name` `append-hmi-studio`, `productName` "Append HMI Studio") and the two builder configs;
  - `src/test/brand.test.js` keeps them in step and fails on upstream names in `src/main` code.
- **Editor branding** lives in fork-owned `js/hmi/HmiBrand.js`. It patches prototypes (all names survive in the minified bundles):
  - app name and logo;
  - Help menu, About and licenses (`HmiDialogs.showAbout`, fed by the `hmiApp.info` IPC);
  - hidden help icons, the refused upstream links and the tab-bar repository link;
  - resource strings, re-parsed after the language bundle loads.
- **Upstream files edited directly** (small merge surface):
  - `index.html`;
  - the desktop check in `js/bootstrap.js`, `js/export.js` and `js/vsdxImporter.js`: `window.electron`, not the app name in the user agent;
  - one filter name in `ElectronApp.js`.
- **Kept on purpose** (format identifiers or functional endpoints):
  - `application/vnd.jgraph.mxfile`, the `.drawio` format and the `.drawio-config` storage key;
  - the HTML-export viewer URL;
  - upstream issue references in code comments.
- **Projects** save as `.ahmi`; legacy `.drawio-hmi` files still open (`HmiFile.LEGACY_EXTENSION`).

## Build and Packaging

- **Packaged file set:** the `files` arrays leave out what the packaged app never loads:
  - `stencils/**/*.xml` and `shapes/**` (compiled into min.js);
  - the unminified editor sources `js/diagramly/**`, `js/grapheditor/**`, `mxgraph/src/**` and `mxgraph/mxClient.js`, except `ElectronApp.js` and `DesktopLibrary.js`, which `bootstrap.js` loads uncompiled;
  - the pdf-lib `cjs`/`dist`/`src`/`ts3.4` trees, `*.map`, `comms/`, `scripts/`, `dist-win-runtime/`, `src/test/`, `doc/` and `.github/`.
  - Keep both configs in sync.
- **Extra resources:**
  - `comms/publish/<platform>-x64` → `resources/comms`;
  - `build/nsis/share` → `nsis` and `build/nsis/<platform>` → `nsis/bin`;
  - `build/icon.png`/`icon.ico` → resources, the window icon and the default installer icon;
  - Linux only: `dist-win-runtime/win-unpacked` → `runtime/win-x64` (the Publish template).
  - `npm test` pins these.
- **No updater and no publish target:** `publish: null` in both configs, so no `app-update.yml`. There is no auto-update code; the renderer gets `disableUpdate=1`.
- **Windows installer:** electron-builder's NSIS target needs wine and its MSI target needs WiX, so electron-builder only builds `--dir`. `scripts/dist-win.mjs` compiles the installer with `NsisScript.js` in editor mode:
  - per-machine by default, `--scope user` for per-user;
  - `.ahmi` association and shortcuts;
  - asks for a running editor to be closed.
  - Installers (editor and Publish) stop the app with `taskkill /T` and wait for it to exit before replacing files.
- **Fuses:** `build/fuses.mjs` flips them in `afterPack`; on Linux the binary is `linux.executableName`.
- **Signing** is opt-in (`build/sign-trusted.mjs`, Azure Trusted Signing through `HMI_SIGNING_*` environment variables). Builds are unsigned by default.
- **HMI sources:** the `js/hmi/*.js` files are loaded one by one by `PostConfig.js` in every build; there is no minified HMI bundle.
- **`HMI_ENV=dev` on a packaged build** still runs the minified editor bundles: `dev=1` is only passed when `js/diagramly/Devel.js` exists.

## Architecture Notes

### Security Model
- **Content Security Policy** prevents remote script execution; the renderer never opens sockets.
- **contextBridge** exposes only specific APIs; **validateSender()** ensures IPC comes from the app's own pages.
- **Path authorisation:** `validateSender` alone is not enough, so file IPC also checks the path.
  - Writes go through `assertWritablePath` (paths blessed through OS chrome: file picker, file association, argv).
  - Reads, stat and watch go through `assertReadablePath` (the same set plus local paths in the user's configuration).
  - Both realpath-canonicalise first.
- **HMI > Publish** writes installers only to Documents or a folder picked in the OS dialog, and takes an icon only if it was picked there (`publishOutputDirs`/`publishIcons` in electron-store). A published runtime reads its project in main (`hmiRuntime.project`).
- **Built-in plugins only:** `isPluginsEnabled` is hardcoded `false`.
- **No data leaves the machine** apart from PLC traffic through `hmi-comms`.

### IPC Pattern
The renderer calls `electron.request({action: ...}, callback, error)`; main handles `rendererReq` (after `validateSender`) and replies on `mainResp`. Pushes use named channels: `hmiCommsEvent`, `hmiPublishEvent`, `hmiRuntimeExitPrompt`, `hmiShowAbout`.

### PLC Communications (hmi-comms)
- **What it is:** `comms/` is a standalone .NET 8 server (Logix via cslogix, SLC/MicroLogix via cscomm3_slc, Modbus TCP via FluentModbus). It speaks a versioned JSON protocol over WebSocket, documented in `comms/README.md`. It must stay independent of the editor and Electron.
- **Lifecycle:** main starts it lazily (`CommsSupervisor`), passes a random token on stdin, restarts it with capped backoff and stops it on quit.
- **Routing:** `hmiComms.*` actions go through `rendererReq` to a `CommsSession` per window.
- **Devices and tags:** in the HMI, devices replace InTouch access names. I/O tags carry `device` and `address`. `HmiCommsDriver` routes simulated tags to `HmiSimulator` and real-device tags to the server.
- **Library submodules:** cslogix and cscomm3_slc are AppendAutomation repositories. Changes there are committed in the submodule; the user pushes them.

### Alarms
- **Documentation:** `doc/HMI_ALARMS.md`.
- **Manager:** `js/hmi/HmiAlarms.js` holds `HmiAlarmManager`, one per Run. `HmiWindowManager` creates it on its own hub client and hands it to each window's `HmiRuntime` as `config.alarms`.
  - It watches every alarmed tag: analog limits with deadband, discrete `alarms.state` on/off.
  - Acknowledgement follows ISA-18.2.
  - It emits `names`, `change` and `event`. Runtimes re-evaluate dependants through `invalidateNames`.
- **Expressions:**
  - `HmiTypes.SYSTEM_TAGS` (`_AlarmsActive`, `_AlarmsUnacked`, `_AckAll`) compile without a dictionary entry and are reserved names.
  - `.InAlarm` and `.Acked` read the manager.
  - `Tag.Acked = 1` is the one allowed dotfield assignment.
- **Objects:**
  - Alarm List and Alarm History are vertices `shape=hmiAlarmList` and `shape=hmiAlarmHistory`, from the HMI sidebar palette.
  - Their settings are style keys (`hmiTitle`, `hmiColumns`, `hmiMaxEvents`, `fontSize`).
  - `HmiAlarmView` draws the live HTML table at Run.
- **History:** main-process `src/main/alarms/AlarmLog.js` writes one CSV per day under `userData/alarms/<store>/`, pruned after 90 days, via `hmiAlarms.append`/`hmiAlarms.recent`.
  - The renderer names the store and never passes a path.
  - The self tests' Runs log nothing (`HmiMenus.alarmStore`).

### Retentive tags
- **What it does:** memory tags with `retentive: true` keep their last value between Runs.
- **Main process:** `src/main/retentive/RetentiveStore.js` keeps `userData/retentive/<store>.json`, written atomically (temporary file and rename), behind `hmiRetentive.load/save/clear`.
- **Renderer (`js/hmi/HmiRetentive.js`):**
  - When the project has retentive tags, `HmiMenus.start` loads the values first. Run then starts asynchronously and `ui.hmiStarting` is set; `HmiRuntimeApp` waits for `hmiRunStateChanged`.
  - `driver.preset(values)` makes `HmiSimulator.connect` start memory tags from the saved values.
  - `HmiRetentiveKeeper`, owned by `HmiWindowManager`, saves changes one second after a burst, and again on stop.
- **Store:** the same as the alarm history (`HmiMenus.alarmStore`).
- **Editor:** HMI > Clear Retentive Values forgets them.

### Users and security
- **Documentation:** `doc/HMI_SECURITY.md`.
- **Renderer (`js/hmi/HmiSecurity.js`):**
  - synchronous SHA-256, HMAC and PBKDF2 (a script's `Login()` must return a value, and Web Crypto is asynchronous only);
  - `HmiSecurityManager`, one per Run, owned by `HmiWindowManager` and passed to runtimes as `config.security`. It answers `_Username`/`_AccessLevel`, fires `names`, handles the script-only actions (`HmiExpr` `FUNCTIONS` with `action: true`, called through `ctx.call`), and does auto logout (`settings.security.autoLogoutMin`).
- **Project:** `project.users` holds `{name, level, salt, hash, iterations}`; passwords are never stored.
- **Main process:** `src/main/security/UserStore.js` keeps runtime changes (`ShowUserManager()`, `ChangePassword()`) in `userData/users/<store>.json`, behind `hmiUsers.load/save/clear`. When that file exists it replaces the project's users; `HmiMenus.start` loads it before Run.
- **Enable link:** OR-combines into `visual.disabled`, the flag that already gates every touch link. The older `disable` link is hidden (milestone 99) but still applied.

### Automation (command line)
- **Commands:** `--hmi-build <spec.json>`, `--hmi-check`, `--hmi-render`, `--hmi-dump` and `--hmi-publish` (with `--product`, `--app-version`, …) drive the studio without its UI. Reference: `doc/HMI_AUTOMATION.md`.
- **Main process:** `runHmiCli` in `electron.js` runs before the single-instance lock, opens a hidden chromeless window with `hmicli=<mode>`, and answers `hmiCli.*` requests: `project`, `report`, `write`, `image`, `publish`, `done`, `fail`.
- **Exit codes:** 0 ok, 2 problems, 1 failure.
- **Renderer:** `js/hmi/HmiCli.js` builds projects from the JSON spec (`HmiCli.build`: types, links over defaults, pages via `ChangePage` because the graph is disabled), dumps them back losslessly, validates every page through `HmiMenus.collectProblems`, and renders pages with `editor.exportToCanvas`.
- **Tests:** `src/test/hmi-cli.test.js` runs the real app, so it needs a display.
- **Claude skill:** `.claude/skills/append-hmi-studio/` (linked into `~/.claude/skills`) teaches agents this workflow.

### Run-only Mode and Publish
- **Run-only switch:** `resources/hmi-runtime/runtime.json` (or `--hmi-runtime <dir>`) starts the app in run-only mode (`RuntimeMode.js`, `js/hmi/HmiRuntimeApp.js`):
  - its own userData;
  - a kiosk, full-screen or fixed window;
  - the exit policy (Ctrl+Alt+Shift+Q, optionally with a password hash).
- **Publish** (`Publisher.js`): the template is the Windows editor's own install, or `resources/runtime/win-x64` on Linux, or `dist-win-runtime` from source. Its steps:
  - rebrand the exe with resedit;
  - stage `hmi-runtime/`;
  - compile with makensis.
  - See `doc/HMI_PUBLISH.md`.

### Runtime views and Append HMI Web
- **View modes:** a runtime (`HmiWindowManager` with `fit`) has three views, set with `setView`:
  - `fit` (default);
  - `fill`: stretched with a CSS transform on the screen, with `mxUtils.convertPoint` corrected by `installStretchedPoints` so taps still land;
  - `original`: 1:1 with scroll bars.
- **URL parameters:** `hmiview=<view>` sets the starting view. `hmiviewmenu=1` adds a View menu in the top right corner (`HmiRuntimeApp.showViewMenu`), and each browser remembers its choice in localStorage.
- **Browser bridge:** Append HMI Web (github.com/AppendAutomation/AppendHMIWeb) runs this runtime in browsers through a bridge that defines `window.electron` with `hmiWeb: true` and `window.process.versions.electron`.
  - `js/bootstrap.js` therefore treats `window.electron.hmiWeb` like Electron.
  - Keep the runtime's IPC actions in step with AppendHMIDesktop and AppendHMIWeb, which use this repository as a submodule.

### Data Storage
userData is `%APPDATA%\Append HMI Studio` (Windows) or `~/.config/Append HMI Studio` (Linux). It holds electron-store `config.json`, Local Storage, `logs/main.log` and `comms-cache`. A published runtime uses `%APPDATA%\<Product>`.

## Testing

- `npm test`: main-process units:
  - CLI args and window bounds;
  - comms supervisor and session (with a real-binary run when `comms/publish/` has a build for this machine);
  - run-only mode;
  - Publish, including real makensis builds when `build/nsis` exists;
  - packaging and brand checks.
- `npm run test-comms`: the hmi-comms xUnit suites. `HMI_COMMS_EXE=<published binary>` tests a trimmed build.
- **In-app self test:** `urlParams.json` with `{"hmitest": 1}` next to `package.json`, then `HMI_ENV=dev npx electron .`. Results are logged as `HMITEST ...`. The log contains a NUL byte, so grep it with `-a`.
- **Windows end-to-end:** use the Windows test box (see the user's windows-build skill) with `.bat` files over SSH.

## CI

`.github/workflows/release.yml`, on a `v*` tag or manual run, on ubuntu-latest:
1. `npm test`;
2. `dist-win`;
3. `dist-linux`.

The results become artifacts; a tag also creates a draft release. `hash-gen.yml` adds checksums to releases.

## Important Constraints

1. **Recursive clone required:** the drawio fork and the comms libraries are submodules.
2. **Node 22.12+** and the **.NET 8 SDK**.
3. **Targets:** Windows x64 and Linux x64. macOS, arm64, MSI, Store and snap packaging were removed.
4. **No auto-update:** new versions are installed over old ones.
5. **Upstream merges** into `drawio` should keep the fork's few direct edits (see Branding) and the `js/hmi/` code.

## Key Dependencies

| Package | Purpose |
|---------|---------|
| `electron` | Desktop app framework |
| `electron-builder` | Packaging (unpacked Windows app, Linux packages) |
| `electron-store` | Persistent settings |
| `electron-log` | Logging |
| `@cantoo/pdf-lib` | PDF export |
| `resedit` | Windows exe version/icon resources for Publish |

CLI argument parsing is hand-rolled in `src/main/args.js`.
