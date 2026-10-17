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
│   ├── alarms/, retentive/, security/, recipes/, trends/ # Run-time stores in userData (AlarmLog, RetentiveStore, UserStore, RecipeStore, TrendLog)
│   ├── sql/                  # SQL* script functions: SqlClient (tedious, node:sqlite), SqlSession, DataSourceStore, Secret, ResultCodes
│   ├── print/ScreenPdf.js    # ScreenToPDF: window capture fallback and the Save dialog
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
├── doc/                      # BUILDING.md, RELEASE_PROCESS.md, HMI_PUBLISH.md, HMI_SQL.md, …
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
  - Page View off for new documents (`Graph.prototype.defaultPageVisible`; `?pv=1` and a file's own setting win);
  - the light appearance by default (`installAppearance`: `darkMode` false in new settings, and a stored upstream `'auto'` moved to light once, marked `hmiLightDefault`);
  - app name and logo;
  - Help menu, About and licenses (`HmiDialogs.showAbout`, fed by the `hmiApp.info` IPC);
  - hidden help icons, the refused upstream links and the tab-bar repository link;
  - resource strings, re-parsed after the language bundle loads.
- **Upstream files edited directly** (small merge surface):
  - `index.html` (title, and the start-up screen with the Append Automation logo, `images/append-automation-logo.png`);
  - the desktop check in `js/bootstrap.js`, `js/export.js` and `js/vsdxImporter.js`: `window.electron`, not the app name in the user agent;
  - in `ElectronApp.js`: one filter name, and File > Open's filter list (`chooseFileEntry`), where Append HMI Studio Projects (.ahmi, .drawio-hmi) come first as the default. The preload's `electron` object is read-only, so its requests cannot be intercepted.
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
- **No data leaves the machine** apart from what a project configures: PLC traffic through `hmi-comms`, and the databases its data sources name. Statements are sent only from main (`src/main/sql/`); a window never opens a socket, never names a file path and never gets a stored password back.

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

### Recipes
- **Documentation:** `doc/HMI_RECIPES.md`.
- **Project:** `project.recipeBooks` holds `{name, uploadDownload, items: [{tag, ioTag}], recipes}`; `recipes` are starting recipes, used until the PC saves its own for that book.
- **Renderer (`js/hmi/HmiRecipes.js`):**
  - `HmiRecipeManager`, one per Run, owned by `HmiWindowManager` and passed to runtimes as `config.recipes`. It subscribes to every book tag on its own hub client and handles the `Recipe*` script functions (routed by name in `HmiRuntime`'s `ctx.call`).
  - Saving is debounced and merges: it loads what the PC has and applies only its own changes since it last synchronized. Append HMI Web's browsers also refresh every 5 s (`REFRESH_MS`).
  - Failures go to the Recipe Error window (`HmiDialogs.queueRecipeError`), shown after the script, coalesced.
  - `HmiRecipeListView` draws the Recipe List (`shape=hmiRecipeList`, hidden link `recipeList`, milestone 99) at Run; `HmiRuntime.TAG_FIELDS` subscribes its tag fields.
- **Asynchronous scripts:** `ShowRecipeSelect` is the one `async: true` function. `HmiExpr`'s script evaluator is a resumable frame stack (`ctx.callAsync`), and the parser allows async calls only as a statement or the whole right-hand side of an assignment.
- **Main process:** `src/main/recipes/RecipeStore.js` keeps `userData/recipes/<store>.json` (atomic) behind `hmiRecipes.load/save/clear`, and writes or reads the CSV files of RecipeExport/RecipeImport through the OS dialog (`hmiRecipes.exportCsv/importCsv`; the generic file IPC refuses CSV). In Append HMI Web these two have no answer and the browser downloads or picks the file.
- **Store:** the same as the alarm history (`HmiMenus.alarmStore`). HMI > Clear Runtime Recipes forgets it.

### Indirect tags
- **Documentation:** `doc/HMI_INDIRECT.md`.
- **Types:** `IndirectDiscrete`, `IndirectAnalog` and `IndirectMessage` (`HmiTypes.isIndirect`, `indirectAccepts`) hold only name, type and comment. The simulator, retentive store and alarm manager skip them.
- **Links:** `HmiDriverHub` (in `HmiWindows.js`) keeps one set of links per Run. It subscribes an indirect name as its linked tag, re-delivers the linked tag's batches under the indirect name (`alias`), writes through (`HmiDriverClient.write`), and on `link()` sends every client the new value. So runtimes, alarm views and recipe books need nothing of their own.
- **Function:** `js/hmi/HmiIndirect.js` has `HmiIndirect.check` (shared with the parser's `checkLink` for literal names, run at Validate) and `HmiIndirect.call`, routed by `HmiRuntime`'s `ctx.call`. `HmiRuntime.linked(name)` resolves dotfields, `.Acked` and alarm colors to the linked tag.
- **Failures:** go to the shared function error window (`HmiDialogs.queueFunctionError`, titled by its contents).
- **Scripts read any tag:** a runtime also subscribes the tags its action scripts and window scripts read (`bindScripts`, `config.scripts`), and `getValue` falls back to `driver.get` for a value not yet delivered. A faceplate's On show script reading `FP_Prefix` depends on this.
- **Example:** LiquidWeighHMI's Device Faceplate (one popup for 14 devices; manual section 9.4).

### Trends
- **Documentation:** `doc/HMI_TRENDS.md`.
- **The object:** `shape=hmiTrend` (`js/hmi/HmiTrend.js`, palette entry in `HmiAlarms.installPalette`). Its settings are the hidden `trend` link (milestone 99): `{name, title, spanSec, sampleMs, refreshMs, gridColor, pens: [{tag, min, max, color}]}`, edited in the Animation tab (`B['trend']` in `HmiLinkPanels.js`). Background, border, pen width and axis font are the cell's `fillColor`, `strokeColor`, `strokeWidth` and font keys.
- **Names:** unique in the project. `Graph.prototype.cellsAdded` (editor graph only, `graph.hmiUi`) gives a new or clashing trend the next free name (`HmiTrend.nameNew`, over every page through `HmiTrend.collect`). Validate reports duplicates, pens that aren't analog or discrete, and script calls naming a trend or pen that doesn't exist (`HmiMenus.trendCallProblem`).
- **Manager:** `HmiTrendManager`, one per Run, owned by `HmiWindowManager` and passed to runtimes as `config.trends`. It samples every pen of every trend from the start on its own hub client, records on change plus a 60 s heartbeat (the held sample before a change too), keeps 24 h in memory, appends to disk every 5 s (`hmiTrends.append`) and reads older ranges back (`hmiTrends.query`). The store is the alarm history's (`HmiMenus.alarmStore`); with no store nothing is written. It writes a gap at stop.
- **Functions:** `TrendSetPen`, `TrendPause`, `TrendPlay`, `TrendScrollBack`, `TrendScrollForward` (`HmiTrend.FUNCTIONS`, routed by `HmiRuntime`'s `ctx.call`). The selected pen and paused/scrolled end time belong to the trend name for the Run (`manager.states`). Names are text; `HmiExpr` refuses a bare non-message tag (`checkTrendCall`).
- **Run view:** `HmiTrendView`, a canvas over the cell (`HmiRuntime.startAlarmViews`), redrawn every `refreshMs` while live and on the manager's `change`. No buttons of its own: navigation is scripted.
- **Main process:** `src/main/trends/TrendLog.js` (shared with AppendHMIDesktop and AppendHMIWeb): `userData/trends/<store>/trends-YYYY-MM-DD.csv` (`Time` ms, `Tag`, `Value`, empty for a gap), 7 days and 500 MB per store, queries thinned to about 4000 points per tag (`decimate`). `TrendRecorders` lets one browser at a time write a store in Append HMI Web.

### QuickFunctions
- **Documentation:** `doc/HMI_QUICKFUNCTIONS.md`.
- **Model:** `project.quickFunctions` holds `{name, args: [{name, type}], body}`; XML `<quickFunctions><quickFunction name><quickArg name type/><quickBody/></quickFunction></quickFunctions>`, the body as a child text element (an empty body writes no element, so the file round trips byte for byte). `js/hmi/HmiQuickFn.js` owns the shape, the name rules (`nameProblem`, shared by the dialog, the CLI and Validate), the cycle detection and the compile wrappers.
- **The language (`HmiExpr.js`):** a third mode, `'quick'`, next to `'script'` and expressions; `HmiExpr.isStatements(mode)` is what every "a script may, a link may not" test now asks. A body is compiled through `HmiExpr.compile(body, {project, mode: 'quick', fn})`, whose `Parser` pre-seeds the arguments as locals, so they resolve, coerce and stay out of `deps` like any `DIM` local. The cache key gains a `q:` prefix and the argument signature.
- **`Return`** is contextual like `DIM` (never in `WORD_OPS`, so a tag named Return still works): `atReturn` wants a statement position and a value or a `;` after it. `eval_return` throws `HmiExpr.ReturnSignal`, caught in `callQuick`, which unwinds the frame stack and any nested `IF` without touching either statement executor.
- **Calling:** `parseCall` looks a name up in the project after the built-ins miss and emits `{type: 'quickcall'}`, a node type that `hasAsync`, `asyncCallOf` and the window/trend literal checks ignore. It refuses a cycle, a wrong argument count, an impure function outside a script, and a body that does not compile, then merges the callee's `deps` and `writes` into the caller's -- which is all the dependency propagation there is, and it is transitive. `eval_quickcall` looks the function up through the `Evaluator`'s new `scope` and `callQuick` binds the arguments (coerced, keeping quality and timestamp), caps depth at `HmiQuickFn.MAX_DEPTH` and calls at `MAX_CALLS`, and names the function in any error.
- **Purity:** `compile().impure` is true when the source writes a tag, calls an action function or calls an impure QuickFunction. The call site gates on it exactly as the built-in `action` flag is gated.
- **The pausing calls:** `ShowRecipeSelect` and a waiting `ShowWindow` are refused in a body. A `ShowWindow` with nothing to wait for is marked `sync` on the node in quick mode only, so it runs through `eval_call`'s action branch and `HmiRuntime.context().call`'s new `ShowWindow` route; a plain script keeps the path it always had.
- **The script tools** under the body (`HmiDialogs.scriptTools`, reusable by any script field): Validate reports the compile on demand into its own message line, New Tag opens `showTagEditor` on a free name and re-checks the body after a save (`scope.touch()` first, since the draft has its own revision), and Browse Tags opens `HmiDialogs.showTagBrowser` and copies the pick through `HmiDialogs.copyText` (the app's clipboard helper, with an `execCommand` fallback; when both refuse, the message names the tag).
- **Elsewhere:** `HmiDialogs.showQuickFunctions` (the dialog, editing a copy against `HmiQuickFn.draftScope`), `HmiDialogs.scriptArea`'s optional `errorsOf`, `HmiMenus.checkQuickFunctions` and its `collectProblems` block, `HmiCli.buildQuickFunctions`/`dump`, and the `HmiTagRefs.eachProjectField` holder -- whose `shield` (the argument names) is threaded through `scanText`/`rewriteText`/`namesIn` so a tag rename cannot rewrite an argument.

### SQL databases
- **Documentation:** `doc/HMI_SQL.md`.
- **Project:** `project.dataSources`, `bindLists`, `tableTemplates` and `queries`; XML `<dataSources><dataSource …>`, `<bindLists><bindList><bindItem tag field column delim/>`, `<tableTemplates><tableTemplate><templateColumn …/>`, `<queries><query name><querySql>` with the statement as a child text element (like a script, so a file round trips). A data source password is wrapped (`hmi1:`), never plain.
- **Renderer (`js/hmi/HmiSql.js`):** the providers (`mssql`, `sqlite`), the column types, the folder rule, the name and shape rules (`dataSourceProblem`, `bindListProblem`, `templateProblem`, `queryProblem`, shared by the dialog, the CLI and Validate), the result codes (`HmiSql.CODES`/`MESSAGES`, a test keeps them in step with `src/main/sql/ResultCodes.js`, which the renderer cannot import), the DDL builder (`tableSql`), parameter conversion (`paramFor`, `decimalParam`, `datePrecision`, `parseDateTime`) and `HmiSqlManager`.
- **Manager:** `HmiSqlManager`, one per Run, owned by `HmiWindowManager` and passed to runtimes as `config.sql`. It subscribes every bind list's tags on its own hub client at `BIND_RATE_MS` (so an insert finds values for tags no window shows), holds each connection's results and cursor in the renderer (moving through rows is synchronous), keeps prepared statements for the Run, and closes every connection at stop (which rolls back an open transaction). `read()` asks the driver first and the cached batch second, because a script sets tags and inserts in the same pass. Failures go to the shared function error window, titled Database Error; `WARNINGS` (-4, -5, -1006) go to the runtime log instead, since a walk over rows ends by running off the end of them.
- **Expressions:** the `SQL*` entries in `HmiExpr.FUNCTIONS` are `action: true`, and the ones that reach a database are `async: true`, so the parser allows them only as a statement or the whole right-hand side of an assignment. `tagArgs` marks the arguments that are tags written to (`SQLConnect`, `SQLPrepareStatement`, `SQLInsertPrepare`). `HmiSql.NAME_ARGS` and `WHERE_ARGS` drive the parser's literal checks and `HmiTagRefs`' `NAME_ARGS` rewriting.
- **Main process (`src/main/sql/`):** `SqlClient.js` is the only code that talks to a database — tedious for SQL Server (imported lazily), `node:sqlite` for a file; `SqlSession.js` is one window's connections (`MAX_CONNECTIONS` 8, numbers count up, `validateSqlArgs` throws on a malformed request); `DataSourceStore.js` keeps per-PC credentials in `userData/datasources/<store>.json` (set and summarized, never read back to a window); `Secret.js` wraps a password; `ResultCodes.js` holds the numbers. `resolveFile` confines a SQLite file to `<userData>/databases` or Documents. Sessions are per `webContents` in `electron.js`, dropped when the window goes and at `will-quit`.
- **Dialog:** `HmiDialogs.showDatabase` (HMI > Database), four tabs over one draft; Test Connection calls `hmiSql.probe`; a stored password is never shown, only kept or cleared. HMI > Clear Database Passwords forgets this computer's own (`hmiSql.clearOverrides`, refused in the runtime).
- **Validate:** `HmiMenus.checkDatabase` (the shared rules, plus a bind list naming a tag the dictionary hasn't — a system tag gets its own wording — and `columnNote` for a reserved or bracket-needing column name) and the `SQL*` call checks in the parser.
- **Packaging:** `tedious` ships; the Azure AD packages are excluded and `build/sql-stubs/*` is mapped over them by both electron-builder configs (see `build/sql-stubs/README.md`), and every glob must precede the first `{from, to}` entry. **Desktop and Web must name `tedious` in their own `package.json`:** they import these modules through the `studio` submodule, but npm resolves from the app's own `node_modules`.
- **Date and time:** `HmiTypes.SYSTEM_TAGS` entries carry `provider` (`alarms` | `security` | `clock`), which `HmiRuntime.readField` routes on. The clock has no state: `HmiTypes.CLOCK_READERS` works each value out from a `Date` as it is read, and `HmiWindowManager.startClock` only invalidates `HmiTypes.CLOCK_NAMES` once a second to repaint. `HmiTypes.stampText` is the one definition of the database stamp (`HmiSql.dateText` is it).
- **Tests:** `src/test/sql.test.js` (SQLite for real, the connect-spec builder, the stores, the code tables; the SQL Server tests run only with `HMI_SQL_HOST`, `HMI_SQL_USER` and `HMI_SQL_PASS` set, like the comms tests with `HMI_COMMS_EXE`). In-app: `sql.*`, `sqlDlg.*`, `clock.*` and `string.*`.

### Editor conveniences
- **An HMI edit marks the file, not just the editor:** `HmiFile.markModified(ui)` sets `editor.modified` **and** calls `file.fileChanged()`. Everything in `js/hmi` that used to call `ui.editor.setModified(true)` goes through it. Without the file's own flag, a change to tags, devices, users, recipes, QuickFunctions, application settings or window properties left `getCurrentFile().isModified()` false, so the title showed nothing, no draft was written, and the close prompt the desktop main process raises (`isModified` -> the renderer -> Save / Discard Changes / Cancel) never appeared.
- **The Tag Dictionary** lists tags alphabetically (case-insensitive, in `visibleTags`, for the list only -- the project keeps its own order, which is what the file and the CSV export use), and its New tag... dropdown sits above the filter, with Duplicate and Delete under the list.
- **Duplicating counts up:** `HmiDialogs.nextName(name, taken)` increments the number at the end of a name and adds a 1 when there is none (NewTag, NewTag1, NewTag2; only a trailing number counts, so Pump9_Run gives Pump9_Run1). `HmiDialogs.freeName` keeps a name that nothing has. Used by the Tag Dictionary's Duplicate and New tag, the QuickFunctions dialog's Duplicate and Add, and the New Tag button under a script.
- **The screen area at start-up:** a new document opens with the canvas origin in the middle of the window, which shows only the top left corner of the screen frame. `HmiFrame.showScreen` zooms out to fit the whole screen area, never past 100%, and centers it. It is called from the `setFileData` wrapper in `HmiFile.js` (deferred a tick), not from init: loading the empty file is what sets the view, through `Editor.setGraphXml`, about a second after init, so anything done earlier is undone. `showScreenIfEmpty` makes it skip a document that has cells, which is how an opened file keeps the view it came with.

### Tag references (find/replace, and what a rename reaches)
- **Documentation:** `doc/HMI_TAG_REFERENCES.md`.
- **The map:** `js/hmi/HmiTagRefs.js` is the one place that knows where a tag name can live. It is driven by the link registry: every `HmiTypes.defineLink` carries `refs` (field -> `tag` | `expr` | `script`) and `refArrays` (`bands`, `pens`), so a new link field is classified where it is defined. `HmiTagRefs.IGNORED_FIELDS` lists the fields that never name a tag, and the self test (`testTagRefs`) fails if a link defines a field in neither list.
- **Visiting:** `eachField(cfg, {key, roles, exclude}, fn)` yields `{holder, field, role, key, path}`; `eachCellField`, `pageCells`, `withDescendants`, `scope(ui)` (selection, else the page), `collect` (the distinct names with their counts), `find`, `eachProjectField` (window scripts, application scripts, recipe book items and the tag-keyed recipe values) and `eachProjectSite`/`findInProject` for the whole project.
- **Rewriting** always takes a whole map, so a swap works and nothing cascades. `rewriteText` lexes with `HmiExpr.Lexer` and splices by token offset: it skips dotfields on another tag, function names, the `InTouch:` qualifier, `DIM` locals and the DIM statement itself, and system tags, and it also rewrites the tag-name string arguments of `LinkIndirectTag` and `TrendSetPen` (`NAME_ARGS`; TrendSetPen's first argument is a trend name). A lexer error leaves the text alone and is reported. `rewriteName` handles a bare-name field.
- **Applying:** `apply(ui, scope, map)` writes every cell inside one outer `beginUpdate`/`endUpdate`, so it is one undo entry. `rename(ui, map)` is the project-wide path: the current page through `apply`, other pages by mutating `page.root` after `ui.updatePageRoot(page)` and setting `page.needsUpdate`, then the project's own holders; only the current page is undoable, and `HmiTagDialog.renameNote` says so.
- **Dialogs (`HmiDialogs.js`):** `showFindReplaceTags` (`HmiFindReplaceDialog`) is the HMI menu item, beside Tag Dictionary; `showTagEditor` (`HmiTagEditorDialog`) edits or creates one tag from a draft copy, with `showSaveConfirm` (Save / Discard / Cancel, dismiss means cancel). The per-tag property form is `HmiTagForm.render(host)`, shared with the Tag Dictionary through five adapters on `HmiTagDialog`.
- **Also fed by the map:** `HmiMenus.checkLink(project, cfg, key)` (Validate now covers the range bounds, `everyMs` and the action scripts) and `HmiRuntime.prototype.dependencies(cfg, key)` (a tag used only as a range bound is subscribed; `HmiRuntime.NOT_SUBSCRIBED` keeps trend pens out, since `HmiTrendManager` subscribes those).

### Arcs
- **The shape:** `js/hmi/HmiArc.js` registers `shape=hmiArc`, a vertex drawn as a polyline along a circle (`arcType=circle`, with `aspect=fixed`) or an ellipse.
  - It runs counterclockwise from `arcStart` to `arcEnd`. Angles are degrees as on a protractor: 0 at the right, so 90 is the top, and a start greater than the end sweeps through 0.
  - The width goes from `strokeWidth` to `arcEndWidth`. When they differ, the line is filled as an outline (`HmiArc.taper`), so it isn't dashed.
  - Its ends use `mxMarker` with `startArrow`/`endArrow`, `startSize`/`endSize` and `startFill`/`endFill`. The line is trimmed by however far each marker moves its end point.
- **Palette:** two entries are appended to General by wrapping `Sidebar.addPaletteFunctions`.
- **Style tab:** an Arc section is added at the top (wrapping `StyleFormatPanel.init`), and the Fill section is hidden.
- **CLI types:** `arc` and `circleArc`.

### Flipping groups
- `js/hmi/HmiFlip.js` replaces `Graph.flipChildren`, which upstream moved only a group's direct children. A group now mirrors about its center: every descendant vertex moves to its mirrored place, toggles its own flip (one cell at a time, since `toggleCellStyles` sets all cells from the first), has its rotation reversed and gets `legacyAnchorPoints=0`. Edge points are mirrored too. A single object still flips in place.
- Arrange > Direction's Flip items call `graph.flipCells`, like the Arrange tab's buttons; upstream they only toggled the selected cell's style.

### ShowWindow (script function)
- **`ShowWindow(name[, left, top[, modal[, wait]]])`** is `async` in `HmiExpr` (statement or assignment only). `HmiRuntime`'s `ctx.callAsync` hands it to `HmiWindowManager.callShowWindow`, which checks its arguments, shows the window with `propsWith` overrides and calls `done(1)` at once, or from `close()` with wait (`win.onClosed`). A resume is dropped when the caller's window or the Run has stopped.
- **Moved windows** keep showing their defined part of the page (`pageX`/`pageY`). A popup with `modal: false` gets no blocker.
- **Touches pass through hidden objects** (`HmiRuntime.touchedCell`), so overlapping variants with visibility links work.
- **Run cursor and selection:** text in `.hmiScreen` is not selectable. Each window's content shows the arrow, and the pointing hand only over an object `isTouchable` (a touch link that is enabled, visible and not disabled), set by `updateCursor` on mouse move; CSS makes the parts inherit it.

### ScreenToPDF (script function)
- **`ScreenToPDF()`** (`js/hmi/HmiScreenPdf.js`) is synchronous like RecipeExport: `HmiRuntime`'s `ctx.call` hands it to `HmiScreenPdf.call` with the window manager, which returns 1 at once. Failures go to the function error window ("Screen to PDF Error"); a second call while one is saving returns 0.
- **Capture:** the `.hmiScreen` element is cloned inside `HmiWindowManager.atDesignSize` (laid out at scale 1 and back in the same task, so nothing paints), then drawn through an SVG foreignObject with the page's style sheets, about 2000 px across. Popups and HTML overlays come along; editor dialogs do not. In the desktop apps, a screen with a non-`data:` picture, or a tainted canvas, falls back to `hmiScreenPdf.capture` (main's `capturePage` of the visible screen).
- **PDF:** written in the renderer (`HmiScreenPdf.document`): one landscape Letter page (792 × 612 pt), the image fitted inside 18 pt margins and centered, Flate through pako (or `CompressionStream`).
- **Save:** `hmiScreenPdf.save` in main (`src/main/print/ScreenPdf.js`, shared with AppendHMIDesktop) checks the data is a PDF and writes only where the OS dialog says, through a hidden temporary file renamed into place (`writeWhole`), so a file manager never sees it empty: Nautilus took an empty file for text and kept that view. The default name is the newest replace window's page name through `HmiScreenPdf.fileName` (invalid characters become `_`, reserved device names get `_`, empty becomes Screen). Append HMI Web downloads a Blob.
- **Example:** LiquidWeighHMI's PRINT SCREEN header button (manual section 9.6).

### Application scripts and local variables
- **Application scripts:** `project.settings.scripts` holds `onStart`, `whileRunning`, `onShutdown` and `everyMs` (`HmiProject.APP_SCRIPTS`; XML `<appScripts everyMs>` with one child element per script, inside `<settings>`). They are edited in Application Settings' Scripts section and checked by Validate (`HmiMenus.checkAppScripts`).
- **Running:** `HmiWindowManager` runs them in a window-less `HmiRuntime` (`graph: null`, `startScripts`/`stopScripts`: it keeps values and writes through its own hub client). On start runs before the startup windows open, While running on a timer of at least 50 ms, and On shutdown in `stop()` after the windows' On hide and before the last retentive save. With no application script there is no extra runtime.
- **Closing a run-only window:** the Run must stop first, so On shutdown runs. `src/main/runtime/RuntimeShutdown.js` (shared with AppendHMIDesktop) holds the first allowed close, sends `hmiRuntimeShutdown`, and closes on `hmiRuntime.stopped` or after 5 s. `HmiRuntimeApp.shutdown` stops the Run and waits for retentive saves in flight (`HmiRetentive.pending`). A Windows session end is not held. Append HMI Web stops on `pagehide`, as far as it can.
- **Pretty-printed files** put a script element's text on its own indented line; `HmiProject.scriptText` strips that on load (window and application scripts).
- **Local variables:** `DIM a, b AS Analog | Discrete | Message;` in scripts only (`HmiExpr` `parseDim`). DIM and AS are words only in that statement, so a tag named Dim still works. A declaration is allowed only at the top level, names are visible after it, and a name may not be a tag's (any case), a system tag's or already declared. References compile to `{type: 'local'}` nodes (not dependencies or writes); the evaluator keeps them per run of the script (`Evaluator.locals`, kept through an asynchronous pause) and coerces assignments to the type (`HmiExpr.coerceLocal`), keeping the value's quality.
- **Loops:** `FOR tag_or_variable = start TO end [STEP by] … NEXT` and `WHILE cond … ENDWHILE`, in scripts only.
  - `HmiExpr.LOOP_WORDS` is deliberately **not** in `WORD_OPS`: a plant project names a tag `Step` or `Next`, and a word operator would stop such a tag compiling anywhere, animation links included. `HmiExpr.loopWords(tokens)` classifies the words by position once, and both the parser (`atLoopWord`) and `HmiTagRefs.scanText` read that one answer, so a rename cannot rewrite a keyword and Find/Replace does not list one as a name.
  - A FOR reads its bounds once and keeps the count in the frame, writing the loop variable each pass: a tag's value comes back from the driver, and a loop that waited for the read-back would never finish. `STEP 0` is refused. Bad quality takes no pass, as in `eval_if`.
  - `loopEntry`/`loopAgain` hold the semantics once, used by `resume`'s frame stack (where a pausing call in a body resumes mid-loop) and by `eval_for`/`eval_while`. `HmiExpr.MAX_ITERATIONS` (100,000) caps all the loops in one run of a script together, counted on the evaluator and reset in `eval_script`, so a QuickFunction called in a loop spends the same budget.

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

### User Manual and examples
- **Files:** `doc/Append-HMI-Studio-User-Manual.docx` and `.pdf` are built from `doc/manual/`, and illustrated with `examples/LiquidWeighHMI.ahmi`.
- **LiquidWeighHMISim** (`examples/LiquidWeighHMISim.ahmi`) needs no PLC: `examples/tools/make_liquidweigh_sim.py` turns a `--hmi-dump` of LiquidWeighHMI into it, with every PLC tag a memory tag, the controller's first scan as the On start application script and its ladder routines as While running (every 100 ms; tags written only on change, per-pass values as DIM locals, timers from `Sim_Clock.TimeDate`). Regenerate it after changing LiquidWeighHMI.
- **Building:**
  - `python3 doc/manual/build_manual.py` writes the .docx from `doc/manual/images/` (python-docx; tables follow the house table rules, measured with Carlito for Calibri);
  - `python3 doc/manual/finish.py --docx` (LibreOffice through uno) fills in the table of contents and writes the PDF.
- **Screenshots:** taken from a running Studio over its debugging port, preferably the installed build on the Windows test box, in the console session so the window can be 1440 × 900 (the user's windows-build skill has the steps); the install is checked in the same pass. LiquidWeighHMI's Run screenshots use the test PLC; the Trends screen uses LiquidWeighHMISim.
  - Open the example from outside the repository: file IPC refuses paths inside the app folder.
  - Turn off scrollbar mode (`ui.setScrollbars(false)`) before fitting a page.
  - Use `window.resizeTo` for the window size, not viewport emulation.
  - Keep personal paths and addresses out of them.

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
userData is `%APPDATA%\Append HMI Studio` (Windows) or `~/.config/Append HMI Studio` (Linux). It holds electron-store `config.json`, Local Storage, `logs/main.log`, `comms-cache`, the run-time stores (`alarms/`, `retentive/`, `users/`, `recipes/`, `trends/`, `datasources/`) and `databases/` for a project's SQLite files. A published runtime uses `%APPDATA%\<Product>`.

## Testing

- `npm test`: main-process units:
  - CLI args and window bounds;
  - comms supervisor and session (with a real-binary run when `comms/publish/` has a build for this machine);
  - run-only mode;
  - Publish, including real makensis builds when `build/nsis` exists;
  - SQL: SQLite for real, connect specs, the credential store and the result codes (live SQL Server with `HMI_SQL_HOST`, `HMI_SQL_USER`, `HMI_SQL_PASS`);
  - packaging and brand checks.
- `npm run test-comms`: the hmi-comms xUnit suites. `HMI_COMMS_EXE=<published binary>` tests a trimmed build.
- **In-app self test:** `urlParams.json` with `{"hmitest": 1}` next to `package.json`, then `HMI_ENV=dev npx electron .`. Results are logged as `HMITEST ...`. The log contains a NUL byte, so grep it with `-a`.
  - In an installed build (for example on the Windows box, over `--remote-debugging-port`): set `urlParams['hmitest'] = '1'`, load `js/hmi/HmiSelfTest.js` with a script element, then call `HmiSelfTest.run(Hmi.ui)` and collect the console. Without the flag, Run starts asynchronously and the run checks fail.
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
| `tedious` | Microsoft SQL Server driver (`SQL*` script functions); SQLite is `node:sqlite` |
| `resedit` | Windows exe version/icon resources for Publish |

CLI argument parsing is hand-rolled in `src/main/args.js`.
