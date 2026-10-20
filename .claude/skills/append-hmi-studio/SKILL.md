---
name: append-hmi-studio
description: Create, modify, check, preview and publish HMI (operator screen / SCADA panel) applications with Append HMI Studio, entirely from the command line. Describe tags, PLC devices (Allen-Bradley Logix, SLC/MicroLogix, Modbus TCP, simulator), windows, objects with animation links, alarms, SQL database logging and runtime settings as a JSON spec, build it into a .ahmi project, validate it, render the pages to PNG for review, and publish a Windows installer for the target PC. Use whenever asked to make, change or deploy an HMI application, operator screen, .ahmi project, or Append HMI Studio package.
---

# Append HMI Studio: build and publish HMI applications

Append HMI Studio is a desktop app that designs, runs and publishes HMI
applications. Its command-line mode lets you work without the UI:

- write a **JSON spec**;
- `--hmi-build` it into a `.ahmi` project;
- `--hmi-check` it;
- `--hmi-render` the pages to PNG and look at them;
- `--hmi-publish` a Windows installer.

The studio does the modeling, validation and packaging itself, so never write
`.ahmi` XML by hand.

- **Full reference** (every command, spec field, link type, expression syntax):
  [reference.md](reference.md).
- **Alarm behavior:** [alarms.md](alarms.md).
- **Users and access levels:** [security.md](security.md).
- **Recipes:** [recipes.md](recipes.md).
- **Indirect tags:** [indirect.md](indirect.md).
- **Windows, title bars and movable popups:** [windows.md](windows.md).
- **Trends:** [trends.md](trends.md).
- **List boxes:** [lists.md](lists.md).
- **QuickFunctions:** [quickfunctions.md](quickfunctions.md).
- **Databases (the `SQL*` functions):** [database.md](database.md).
- **The date and time, `Now()` and the time formats:**
  [datetime.md](datetime.md).
- **A complete example:** [example-spec.json](example-spec.json). It has a
  Logix PLC, alarmed tags, an overview page with a tank, a pump button, a list
  box an On start script fills, and a popup alarm window.

## 1. Find the studio

Use the first of these that exists (check with `--help`; it must list `--hmi-build`):

| Where | Command (`STUDIO` below) |
|---|---|
| Linux deb install | `"/opt/Append HMI Studio/append-hmi-studio"` |
| Linux AppImage | the `Append-HMI-Studio-*-x86_64.AppImage` file |
| Windows install | `"C:\Program Files\Append HMI Studio\Append HMI Studio.exe"` |
| Source checkout (a clone of this repository, after `npm install`) | `npx electron . --no-sandbox`, run from the repository root |

- **Display:** the modes open a hidden window, so they need a display. Use
  `xvfb-run STUDIO …` on a headless Linux box.
- **Absolute paths:** always pass absolute paths. The source-checkout form
  runs from the repository root, where relative paths would resolve.
- **Output:** results go to stdout, errors to stderr.
- **Alongside the editor:** the commands work while the studio is also open.

## 2. Workflow

1. **Understand the plant first.** Get the tags (name, type, PLC address,
   engineering range, units, description), the PLCs (protocol, IP), the
   screens wanted, the target screen resolution, and how the station should
   run (kiosk or window, exit policy). Ask the user for anything missing that
   cannot sensibly default. Do not invent PLC addresses.
2. **Write the spec** (see the rules below and reference.md). Save it next to
   where the project should live, e.g. `line3.json`.
3. **Build:** `STUDIO --hmi-build line3.json -o line3.ahmi`
   - **exit 1:** spec errors (unknown fields, types, links); nothing written.
     Fix and rerun.
   - **exit 2:** the project was written but has `PROBLEM …` lines (unknown
     tag in an expression, bad PLC address for the protocol, missing window,
     window off screen). Fix the spec and rebuild. Tag problems print as
     `PROBLEM Tags | <tag> | <message>`.
   - **exit 0:** done. `OK: no problems found`.
4. **Look at it:** `STUDIO --hmi-render line3.ahmi -o line3-pages`. Open the
   PNGs (Read tool) and check the layout: overlaps, text fitting, alignment.
   Adjust and rebuild.
   - Each PNG is one page, cropped to its content.
   - A popup's page renders alone, without its title bar or the page beneath,
     so check popup placement from the numbers (`window.x/y/width/height`
     against the screen size).
   - Animation isn't applied.
5. **Publish** once the user wants a package:
   ```
   STUDIO --hmi-publish line3.ahmi --product "Line 3 HMI" --app-version 1.0.0 \
          --publisher "Plant name" [--scope machine] [--desktop-shortcut] [--autostart] \
          [--compression fast] -o dist/
   ```
   It prints `Created <path>/Line 3 HMI-1.0.0-Setup.exe`. Default compression
   is `small` (about 3.5 min); use `fast` for test builds.
6. **Report:** tell the user the files produced, the check result, and how to
   install:
   - run the Setup.exe on the Windows 10/11 x64 target, or `/S` for silent;
   - the installer is unsigned, so SmartScreen asks once;
   - the runtime log is `%APPDATA%\<Product>\logs\main.log`.

To **change an existing project**:
- run `STUDIO --hmi-dump project.ahmi -o project.json`, edit the JSON, and
  build it back to a new file;
- dump → build is lossless;
- don't overwrite the user's `.ahmi` without asking; keep the original until
  they confirm.

## 3. Rules that matter

- **Coordinates are screen pixels.** (0, 0) is the top left of the target
  screen (`settings.width` × `settings.height`, default 1024 × 768). Keep
  objects inside it.
- **Each page is a window,** and a window shows the part of its page under
  its rectangle.
  - Full-screen pages: draw from (0, 0).
  - A popup at `x: 112, y: 184` must have its objects drawn at those
    coordinates.
  - `titleBar: true` takes 24 px from the top of the window's height.
- **Startup:** list page names in `settings.startup`; without it the first
  page opens.
- **Navigation:** `showWindow`/`hideWindow` links name pages. A `replace`
  window closes windows it overlaps, `overlay` opens on top, and `popup` is
  modal.
- **Fields with fields of their own are objects,** never text: `sim`
  (`{"mode": "ramp", "periodMs": 5000}`), `alarms`, a device's `options`. The
  build refuses text there.
- **Expressions are strings,** even numbers (`"atMax": "100"`). They are
  InTouch-style QuickScript:
  - operators: `AND OR NOT`, `== <> < <= > >=`, `+ - * / MOD`;
  - functions: `Abs`, `Round`, `Min`, `Max`, `Text(x, "0.0")`, and the text
    family `StringLeft`, `StringRight`, `StringMid`, `StringTrim`,
    `StringUpper`, `StringLower`, `StringInString`, `StringReplace`,
    `StringFromIntg`, `StringFromReal`, `StringToIntg`, `StringToReal`
    (positions count from 1);
  - the time as text: `Now()` is `HH:mm:ss` and `Now(format)` takes a pattern
    (`yyyy-MM-dd HH:mm:ss`, `MM/dd/yyyy hh:mm tt`) or a name (`TIME`,
    `TIME12`, `HHMM`, `DATE`, `US`, `EU`, `DATETIME`, `STAMP`, `ISO`,
    `LONG`). A format has to hold at least one token and no stray letter, so
    build text by joining strings: `"Run " + Now("DATETIME")`;
  - dotfields: `Tag.InAlarm`, `Tag.Acked`, `Tag.MaxEU`;
  - scripts (`pushbutton.action`, window `onShow`/`whileShowing`/`onHide`,
    application `settings.scripts`): statements end with `;`,
    `IF … THEN … ELSE … ENDIF;`, the loops
    `FOR i = 1 TO n [STEP by] … NEXT;` and `WHILE cond … ENDWHILE;` (100,000
    passes per run of a script, then it reports and stops), and
    `DIM a, b AS Analog;` (or `Discrete`, `Message`) declares local variables
    at the top level, which may not share a tag's name.
- **QuickFunctions** (`quickFunctions`): functions the project defines, with
  typed arguments and a body that gives a value back with
  `Return <expression>;`. Call one like any built-in, from a script or from an
  animation expression; one that writes a tag or calls an action function is
  script-only. Use them to write a rule once instead of copying an expression
  into every object. See [quickfunctions.md](quickfunctions.md).
- **Application scripts** (`settings.scripts`): `onStart`, `whileRunning`
  (every `everyMs`) and `onShutdown` run for the whole application,
  whichever windows are open. Put logic that must always run there, not in a
  window's `whileShowing`.
- **Links need only fields that differ** from the defaults (reference.md
  lists them).
  - Colors are `#RRGGBB`.
  - For analog color bands the first band whose `max` the value is below wins;
    the last band has `"max": null`.
- **Tags:**
  - types: `Memory…` (internal) or `IO…` (on a PLC: needs `device` and
    `address`), each Discrete, Integer, Real or Message;
  - names are case-insensitive, and the system tags are reserved:
    `_AlarmsActive`, `_AlarmsUnacked`, `_AckAll`, `_Username`,
    `_AccessLevel`, and the clock — `_DateString` (`2026-10-17`),
    `_TimeString` (`14:05:09`), `_DateTime` (`2026-10-17 14:05:09.270`),
    `_Year`, `_Month`, `_Day`, `_Hour`, `_Minute`, `_Second`, `_Msec`,
    `_Now` (the time in the format `_TimeFormat` holds) and `_TimeFormat`
    (`HH:mm:ss` at the start of every run; a script sets it for the whole
    application, usually in `onStart`). They are worked out as they are read,
    and read-only apart from `_AckAll` and `_TimeFormat`;
  - the `comment` is the alarm description;
  - `"retentive": true` on a memory tag keeps its last value between runs,
    for setpoints, modes and recipe names the operator changes.
- **Addresses:** use the device's protocol syntax.
  - `logix`: `Tank_Level`, `Program:Main.Pump.Run`, `Numbers[3]`, `Status.5`;
  - `slc`: `N7:0`, `B3:1/4`, `F8:2`, `T4:0.ACC`;
  - `modbus`: `HR:0`, `HR:10:FLOAT`, `CO:5`.
  - The check validates the syntax; it does not connect to the PLC.
- **Simulator:** a `simulator` device (or memory tags with `sim`) keeps
  simulating in the published package. Use real devices for production.
- **Alarms:**
  - analog `alarms: {loLo, low, high, hiHi, deadband}`, discrete
    `alarms: {state: "on"|"off"}`;
  - show them with `type: "alarmList"` / `"alarmHistory"` objects, colored
    through their `style` (`hmiActUnackFill`, `hmiHeadColor`, … in
    reference.md) and framed by `fillColor`, `strokeColor` and the font keys;
  - flash until acknowledged with a `blink` link on
    `Tag.InAlarm AND NOT Tag.Acked`;
  - an Ack All button is a `pushbutton.action` with `onDown: "_AckAll = 1;"`.
- **Security:**
  - `users: [{name, level (0-9999), password}]` (hashed at build);
  - a Login button is a `pushbutton.action` with `onDown: "ShowLogin();"`, and
    Log Out is `"Logout();"`;
  - gate controls with an `enable` link, e.g.
    `{"expr": "_AccessLevel >= 500"}`, and show who is logged in with a
    `valueDisplay` of kind `string` on `_Username`;
  - `settings.security.autoLogoutMin` logs out after idle minutes;
  - ask the user for the users and levels; never invent passwords for a real
    plant, and tell the user which passwords you set.
- **Indirect tags** (one faceplate for many devices):
  - tags of type `IndirectDiscrete`, `IndirectAnalog` or `IndirectMessage` (only `name`, `type`, `comment`);
  - the button that opens the faceplate links them, with names as text:
    `onDown: "LinkIndirectTag(\"FP_Status\", \"SV001_Status\");"`, plus a `showWindow` link;
  - the faceplate's links use the indirect tags; `FP_Status.Name` shows the linked tag.
- **Arcs:** `type` `arc` (ellipse) or `circleArc` (circle), with
  `style` `arcStart=…;arcEnd=…;` (degrees as on a protractor: 0 at the right,
  counterclockwise; 0 to 180 is the top half), `strokeWidth` (start width) and
  `arcEndWidth` (end width), `startArrow`/`endArrow` (`classic`, `block`, `open`, …),
  `startSize`/`endSize`, and `startFill`/`endFill` (0 for an outline end).
- **Windows from scripts:** `ShowWindow("Name")`, or `ShowWindow("Name", left, top, modal, wait)`.
  `wait` 1 makes the script pause until the window closes, e.g. a confirmation popup.
- **Screen to PDF:** a button whose Action Script is `ScreenToPDF();` saves the screen as a
  landscape PDF through a Save dialog (the page name is the default file name).
- **Recipes:**
  - `recipeBooks: [{name, uploadDownload, items: [{tag, ioTag}], recipes}]`;
    Save/Load tags are usually memory tags the operator edits, `ioTag` the PLC
    tags;
  - a Select button: `onDown: "RecipeName = ShowRecipeSelect(\"Book\"); IF
    RecipeName <> \"\" THEN RecipeLoad(\"Book\", RecipeName); ENDIF;"`;
    Save and Download: `RecipeSave(\"Book\", RecipeName);
    RecipeDownload(\"Book\", RecipeName);`;
  - a `recipeList` object with a `recipeList` link `{book, selectedTag}` lists
    the recipes on screen; `titleFill`, `titleColor`, `selFill` and `selColor`
    color its heading and selection, and `fillColor`, `strokeColor` and the
    font keys its frame.
- **Trends:**
  - a `trend` object with a `trend` link `{name, title, spanSec, pens: [{tag, min, max, color}]}`;
    pens are analog or discrete tags, each on its own range (empty: the tag's); give
    discrete pens different ranges to stack their steps (-4 to 2, 0 to 6);
  - the trend has no buttons: add buttons with Action Scripts such as
    `TrendScrollBack(\"Main\");`, `TrendScrollForward(\"Main\");`, `TrendPause(\"Main\");`,
    `TrendPlay(\"Main\");` and `TrendSetPen(\"Main\", \"LIT100\");`;
  - every pen is recorded from the start of the Run, with 7 days of history on disk.
- **List boxes** ([lists.md](lists.md)):
  - a `list` object with a `list` link `{name, selectedTag}` shows items the
    operator picks from; add `titleBar: true` with a `title` for a heading,
    `items` for starting items (one per line), `arrows` or `upTag`/`downTag`
    to step the selection, and `selFill`/`selColor` to color it;
  - scripts fill and work it by name: `ListClear(\"Lots\");`,
    `ListLoad(\"Lots\", \"A, B, C\");`, `ListAdd(\"Lots\", NewLot);`,
    `ListRemove(\"Lots\", SelLot);`, `ListSelect(\"Lots\", NewLot);` and
    `ListSort(\"Lots\", \"ASC\");`;
  - the list belongs to the Run, so an On start script can fill one the
    operator opens later; items are not kept between Runs;
  - a list box and a `SQLSelect` walk go together: clear the list, then
    `ListAdd` the bind list's tag as the cursor moves down the rows.
- **Databases** (`SQL*` script functions, [database.md](database.md)):
  - `dataSources` name the databases: `{"name": "BatchDb", "provider":
    "sqlite", "file": "batches.db", "folder": "documents"}`, or `mssql` with
    `host`, `database`, `user`, `password` and `options`. A SQLite source
    names a file, never a path. Prefer SQLite unless the user names a server:
    it needs no server, no credentials and no authorization;
  - `bindLists` pair a tag with a column (`{tag, column}`, or a plain tag
    name for a column of the same name). A select writes the columns into the
    tags; an insert reads the tags into the columns. Dictionary tags only —
    not a system tag;
  - `tableTemplates` let the application create its own table, so nothing has
    to be set up by hand on the target PC: `SQLCreateTable` on start, and
    ignore the failure when it already exists;
  - `queries` are statements kept by name, loaded with `SQLLoadStatement`.
    Don't deploy `.sql` files;
  - the shape of a logging script, in `settings.scripts.onStart` and the path
    that completes the work:
    ```
    Res = SQLConnect(Db_ConnId, "BatchDb");
    Res = SQLCreateTable(Db_ConnId, "BatchLog", "BatchLogTable");
    ...
    Batch_Stamp = _DateTime;
    Res = SQLInsert(Db_ConnId, "BatchLog", "BatchRow");
    IF Res <> 0 THEN Db_Status = SQLErrorMsg(Res); ENDIF;
    ```
  - every function returns a result code, 0 for success (`SQLNumRows` returns
    the count, `SQLErrorMsg` the message text). The ones that reach a database
    pause the script, so they may only be a statement or the whole
    right-hand side of an assignment;
  - a selection is walked with `SQLFirst` and `SQLNext`, which write each row
    into the bind list's tags — so a "recent rows" screen is one bind list per
    row of Message tags, or one row shown at a time with Prev and Next
    buttons;
  - `SQLUpdate` and `SQLDelete` refuse an empty where expression. Build one
    with the `String*` functions, or use parameters.
- **Runtime settings:**
  - `windowMode` `kiosk` (default, locked full screen), `fullscreen` or
    `window`;
  - `exit` `shortcut` (Ctrl+Alt+Shift+Q, default), `close` (the window's
    close button, or Alt+F4, and the shortcut too), `password` (give
    `exitPassword`; only a hash is stored) or `never`;
  - `close` suits a project run on an office PC beside other programs;
    `shortcut` or `password` suit a dedicated panel;
  - never choose `never` unless the user asks. It can only be stopped by
    shutting Windows down.
- **Touch feedback:** `settings.touchFill` is one color for the whole
  application. Every object with a touch animation is drawn in it while the
  operator holds it down, and only while the touch would do something, so a
  touch screen answers a press. Unset (the default) a press changes nothing;
  set it on a touch-panel project.
- **Labels** are plain text; `html=1` is already in every `type`'s style. Add
  style keys with `style`, e.g. `"fontSize=16;fontStyle=1;fillColor=#dae8fc;"`.
  - A `valueDisplay` replaces the label at Run (a button can show Start/Stop).
  - Still give a placeholder `label`, or the object is blank in renders.
- **Equipment symbols:** any draw.io shape works through `style`, including
  the P&ID libraries: `shape=mxgraph.pid.pumps.centrifugal_pump_1;`,
  `shape=mxgraph.pid.valves.gate_valve;`, `shape=mxgraph.pid.vessels.tank;`.
  An unknown name draws a rectangle, so render to confirm.
- **Percent Fill** clips the object to the percentage, and the unfilled part,
  outline included, isn't drawn. Stack an identical `fillColor=none` shape on
  top as the vessel outline.
- **Modbus reals** are `HR:n:FLOAT` (32-bit float), and a scaled integer is
  `HR:n` with `scaled: true`. The grammar is
  `HR|IR|CO|DI:<offset>[.bit][:TYPE][:ORDER]` (details in reference.md). If
  the data type isn't stated, ask.
- **Popups:** a title bar shows the page name and a × close button. The Alarm
  List object already has **Ack All** and per-row **Ack**.
- **Typos fail:** unknown fields anywhere in the spec are errors (exit 1)
  that list the allowed fields.

## 4. Good screen practice

- **Colors:** a restrained palette. Grey equipment, color only for state:
  green running, red alarm or fault, amber warning.
- **Readability:** values with units (`valueDisplay` `suffix`), 12–16 pt text,
  titles 18–24 pt.
- **Navigation:** a consistent bar (same buttons, same place on every page).
- **Alarms:** keep an Alarm List (or a count bound to `_AlarmsActive`) visible
  on the overview.
- **Review:** render and look before calling a layout finished.
