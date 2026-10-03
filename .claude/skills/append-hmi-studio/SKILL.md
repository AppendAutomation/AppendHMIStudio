---
name: append-hmi-studio
description: Create, modify, check, preview and publish HMI (operator screen / SCADA panel) applications with Append HMI Studio, entirely from the command line. Describe tags, PLC devices (Allen-Bradley Logix, SLC/MicroLogix, Modbus TCP, simulator), windows, objects with animation links, alarms and runtime settings as a JSON spec, build it into a .ahmi project, validate it, render the pages to PNG for review, and publish a Windows installer for the target PC. Use whenever asked to make, change or deploy an HMI application, operator screen, .ahmi project, or Append HMI Studio package.
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
- **A complete example:** [example-spec.json](example-spec.json). It has a
  Logix PLC, alarmed tags, an overview page with a tank, a pump button and a
  popup alarm window.

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
- **Expressions are strings,** even numbers (`"atMax": "100"`). They are
  InTouch-style QuickScript:
  - operators: `AND OR NOT`, `== <> < <= > >=`, `+ - * / MOD`;
  - functions: `Abs`, `Round`, `Min`, `Max`, `Text(x, "0.0")`, …
  - dotfields: `Tag.InAlarm`, `Tag.Acked`, `Tag.MaxEU`;
  - scripts (`pushbutton.action`, window `onShow`/`whileShowing`/`onHide`):
    statements end with `;`, `IF … THEN … ELSE … ENDIF;`.
- **Links need only fields that differ** from the defaults (reference.md
  lists them).
  - Colors are `#RRGGBB`.
  - For analog color bands the first band whose `max` the value is below wins;
    the last band has `"max": null`.
- **Tags:**
  - types: `Memory…` (internal) or `IO…` (on a PLC: needs `device` and
    `address`), each Discrete, Integer, Real or Message;
  - names are case-insensitive, and `_AlarmsActive`, `_AlarmsUnacked`,
    `_AckAll`, `_Username` and `_AccessLevel` are reserved system tags;
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
  - show them with `type: "alarmList"` / `"alarmHistory"` objects;
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
- **Recipes:**
  - `recipeBooks: [{name, uploadDownload, items: [{tag, ioTag}], recipes}]`;
    Save/Load tags are usually memory tags the operator edits, `ioTag` the PLC
    tags;
  - a Select button: `onDown: "RecipeName = ShowRecipeSelect(\"Book\"); IF
    RecipeName <> \"\" THEN RecipeLoad(\"Book\", RecipeName); ENDIF;"`;
    Save and Download: `RecipeSave(\"Book\", RecipeName);
    RecipeDownload(\"Book\", RecipeName);`;
  - a `recipeList` object with a `recipeList` link `{book, selectedTag}` lists
    the recipes on screen.
- **Runtime settings:**
  - `windowMode` `kiosk` (default, locked full screen), `fullscreen` or
    `window`;
  - `exit` `shortcut` (Ctrl+Alt+Shift+Q, default), `password` (give
    `exitPassword`; only a hash is stored) or `never`;
  - never choose `never` unless the user asks. It can only be stopped by
    shutting Windows down.
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
