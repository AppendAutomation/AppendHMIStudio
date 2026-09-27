# HMI automation: build, check, render and publish from the command line

Append HMI Studio can be driven without its user interface, by scripts, CI or
AI agents. A project is described as a JSON spec, turned into a `.ahmi`
project, checked, rendered to images for review, and published as a Windows
installer. Each step uses the studio's own model, validation and Publisher.
The files are the same as those saved from the editor.

```
append-hmi-studio --hmi-build   line3.json   -o line3.ahmi      # spec -> project, then check
append-hmi-studio --hmi-check   line3.ahmi                      # problems, exit code 2 if any
append-hmi-studio --hmi-render  line3.ahmi   -o pages/          # one PNG per page
append-hmi-studio --hmi-dump    line3.ahmi   -o line3.json      # project -> spec
append-hmi-studio --hmi-publish line3.ahmi   --product "Line 3 HMI" --app-version 1.0.0 -o dist/
```

## Running the studio

| Installation | Command |
|---|---|
| Linux (deb) | `/opt/Append HMI Studio/append-hmi-studio` |
| Linux (AppImage) | `./Append-HMI-Studio-<version>-x86_64.AppImage` |
| Windows | `"C:\Program Files\Append HMI Studio\Append HMI Studio.exe"` |
| Source checkout | `npx electron . --no-sandbox` (from the repository root) |

- **Needs a display:** these modes open a hidden window. On a headless Linux
  machine, use `xvfb-run`.
- **Output:** results go to stdout, errors to stderr.
- **Alongside the editor:** the commands work while the studio is also open.
- **Use absolute paths.** The source-checkout form runs from the repository
  root, so relative paths resolve there, not from your working folder.

## Commands

| Command | Does | Exit code |
|---|---|---|
| `--hmi-build <spec.json> [-o <project.ahmi>]` | Builds the project (default output: the spec's name with `.ahmi`), writes it, then checks it | 0 ok, 2 problems (file still written), 1 spec errors (nothing written) |
| `--hmi-check <project.ahmi>` | Runs HMI > Validate Expressions on every page | 0 ok, 2 problems, 1 failure |
| `--hmi-render <project.ahmi> [-o <folder>]` | Writes `01-<page>.png`, `02-<page>.png`, … (default folder: `<name>-pages`) | 0 ok, 1 failure |
| `--hmi-dump <project.ahmi> [-o <spec.json>]` | Writes the project as a spec (default: stdout) | 0 ok, 1 failure |
| `--hmi-publish <project.ahmi> [options] [-o <folder>]` | Checks, then builds `<Product>-<version>-Setup.exe` (default folder: the project's). Problems are reported but do not stop it, so check first | 0 ok, 1 failure |

**What the check covers:**
- every expression and script, compiled against the tag dictionary;
- Show/Hide Window targets;
- window sizes against the screen;
- each I/O tag's device and address, validated by the comms server for the
  device's protocol.

Each problem is printed as `PROBLEM <page> | <object> | <link> | <message>`,
where the object is the cell id and its label. Project-level problems have
three fields: `PROBLEM Tags | <tag> | <message>`.

**Publish options:**

| Option | Default | Meaning |
|---|---|---|
| `--product <name>` | project file name | Product name: the installed program, shortcuts, Add/Remove Programs |
| `--app-version <x.y.z>` | 1.0.0 | Package version |
| `--publisher <name>` | none | Publisher shown in Windows |
| `--scope user\|machine` | user | Install for the current user (no admin) or all users (admin) |
| `--compression small\|fast` | small | Solid LZMA (about 3.5 min, about 165 MB) or zlib (under a minute, about 215 MB) |
| `--desktop-shortcut` | off | Add a desktop shortcut |
| `--autostart` | off | Start with Windows |

The window mode and exit policy come from the project's runtime settings. See
`doc/HMI_PUBLISH.md` for installing the result.

## The JSON spec (version 1)

```json
{
  "version": 1,
  "settings": {
    "width": 1024, "height": 768,
    "startup": ["Overview"],
    "runtime": {"windowMode": "kiosk", "exit": "shortcut"}
  },
  "devices": [ {"name": "CLX", "protocol": "logix", "host": "192.168.2.118", "options": {"slot": 0}} ],
  "tags": [
    {"name": "Tank_Level", "type": "IOReal", "device": "CLX", "address": "Numbers[0]",
     "comment": "Day tank level", "engUnits": "%", "minEU": 0, "maxEU": 100,
     "alarms": {"high": 90, "hiHi": 95, "deadband": 2}}
  ],
  "pages": [
    {"name": "Overview", "background": "#f5f5f5",
     "objects": [
       {"id": "tank", "type": "cylinder", "x": 60, "y": 80, "width": 120, "height": 200, "label": "Tank",
        "links": {"percentFill.vertical": {"expr": "Tank_Level", "atMin": "0", "atMax": "100"}}}
     ]}
  ]
}
```

- **Unknown fields are errors** at every level: spec, device, tag, page,
  window, object, connector and link. Typos fail with exit 1, listing the
  allowed fields.
- **Links need only what differs:** each link is filled in over its type's
  defaults.
- **Coordinates:** pixels on the page. Page coordinates are screen
  coordinates, with (0, 0) the top left of the target screen.

### settings

| Field | Meaning |
|---|---|
| `width`, `height` | Target screen resolution (default 1024 × 768) |
| `startup` | Page names opened when the application starts, in order (none: the first page) |
| `runtime.windowMode` | `kiosk` (default; full screen, no taskbar), `fullscreen` or `window` (fixed, at the resolution) |
| `runtime.exit` | `shortcut` (default, Ctrl+Alt+Shift+Q), `password` (shortcut then password) or `never` |
| `runtime.exitPassword` | With `exit: password`, the password in plain text; only a salted hash is stored. A dumped spec carries `salt`/`hash` instead |
| `publish` | Remembered Publish dialog options (`productName`, `version`, `publisher`, `scope`, …); optional |

### devices

| Field | Meaning |
|---|---|
| `name` | Referenced by tags' `device` |
| `protocol` | `simulator`, `logix` (ControlLogix/CompactLogix, EtherNet/IP), `slc` (SLC 500/MicroLogix, PCCC) or `modbus` (Modbus TCP) |
| `host`, `port` | PLC address (port defaults: 44818, 502) |
| `timeoutMs`, `scanMs`, `enabled` | Request timeout (3000), scan period (250), false to switch off |
| `options` | `logix`: `slot` (0), `micro800` (false). `slc`: `maxGapElements` (118), `swapStringBytes` (true). `modbus`: `unitId` (1), `byteOrder` (`BE`, `MLE`, `MBE`, `LE`), `maxGapRegisters` (16) |

A `simulator` device simulates its tags in the editor's Run *and* in a
published package.

### tags

| Field | Meaning |
|---|---|
| `name` | Letters, digits, `_`, `$`; starting with a letter, `_` or `$`; up to 63 characters; case-insensitive. `_AlarmsActive`, `_AlarmsUnacked` and `_AckAll` are reserved |
| `type` | `MemoryDiscrete`, `MemoryInteger`, `MemoryReal`, `MemoryMessage`, `IODiscrete`, `IOInteger`, `IOReal` or `IOMessage` |
| `comment` | Description; also the alarm description |
| `initial` | Starting value (memory tags) |
| `engUnits`, `minEU`, `maxEU` | Units and engineering range (Integer/Real) |
| `onMsg`, `offMsg` | Text for 1 and 0 (Discrete) |
| `device`, `address` | I/O tags: device name and PLC address |
| `scaled`, `minRaw`, `maxRaw` | I/O analog: set `scaled: true` to map raw minRaw..maxRaw to minEU..maxEU |
| `scanMs` | Per-tag scan period (I/O) |
| `alarms` | Analog: `loLo`, `low`, `high`, `hiHi`, `deadband`. Discrete: `state` `on` or `off` (in alarm at 1 or 0). See `doc/HMI_ALARMS.md` |
| `sim` | Simulated value in Run: `mode` (`sine`, `ramp`, `random`, `toggle`, `static`), `periodMs` |

Addresses by protocol:
- **`logix`:** `Tank_Level`, `Program:MainProgram.Pump.Run`, `Numbers[3]`, `Status.5`.
- **`slc`:** `N7:0`, `B3:1/4`, `F8:2`, `T4:0.ACC`, `ST9:0`.
- **`modbus`:** `HR|IR|CO|DI:<offset>[.bit][:TYPE][:ORDER]`, with 0-based
  offsets (`HR:0` is classic 40001; classic 5- and 6-digit references are
  refused).
  - Tables: holding registers `HR`, input registers `IR`, coils `CO`,
    discrete inputs `DI` (CO and DI are 1-bit).
  - `TYPE`: `INT16` (default), `UINT16`, `INT32`, `UINT32`, `FLOAT`, `INT64`,
    `UINT64`, `DOUBLE`, `STRINGn`.
  - `ORDER` of a multi-word value: `BE` (ABCD, default), `LE` (DCBA), `MBE`
    (BADC), `MLE` (CDAB). It overrides the device's `byteOrder`.
  - `HR:4.3` is bit 3 of a register.
  - Examples: `HR:0:FLOAT`, `IR:10:INT32:MLE`, `CO:5`, `DI:0`.
- **Real values:** a PLC real in registers is `HR:n:FLOAT`. A scaled integer
  is `HR:n` with `scaled: true` and `minRaw`/`maxRaw`.

### pages

Each page is a window of the application.

| Field | Meaning |
|---|---|
| `name` | Unique; used by `startup` and Show/Hide Window links |
| `background` | Page (window) color, e.g. `#ffffff` |
| `window` | Window properties. `type`: `replace` (default; closes windows it overlaps), `overlay` or `popup` (modal). Also `titleBar` (true adds a 24 px bar inside the height), `x`, `y`, `width`, `height` (default: the whole screen), and the scripts `onShow`, `whileShowing` (every `everyMs`, default 1000) and `onHide` |
| `objects` | The objects on the page |

A window shows the part of its page under its rectangle, so draw a popup's
objects at the popup's `x`/`y`, below its title bar if it has one. A title
bar shows the page name and a × close button.

### objects

| Field | Meaning |
|---|---|
| `id` | Optional, unique on the page; needed for connector ends |
| `type` | Shorthand style: `rect`, `roundedRect`, `ellipse`, `text`, `button`, `line`, `arrow`, `triangle`, `cylinder`, `alarmList` or `alarmHistory`. Optional when `style` names a shape |
| `style` | draw.io style keys added after the type's (later keys win), e.g. `fillColor=#dae8fc;strokeColor=#6c8ebf;fontSize=16;fontStyle=1;rounded=1;` |
| `x`, `y`, `width`, `height` | Position and size |
| `label` | Text (HTML allowed with `html=1`, which the types set) |
| `links` | Animation links, below |
| `children` | Objects inside this one (a group or container; coordinates are relative to it) |

**Any draw.io shape** can be used through `style`, including the P&ID
libraries.
- Examples: `shape=mxgraph.pid.pumps.centrifugal_pump_1;`,
  `shape=mxgraph.pid.valves.gate_valve;`, `shape=mxgraph.pid.vessels.tank;`.
- Names follow the stencil files: `stencils/pid/pumps.xml` shape "Centrifugal
  Pump 1" is `mxgraph.pid.pumps.centrifugal_pump_1`.
- Color links work on them as on any shape.
- Render to check that a name drew a shape: an unknown name draws a plain
  rectangle.

A **connector** is `{"edge": true, "id", "source": <object id>, "target": <object id>, "style", "label", "sourcePoint": [x, y], "targetPoint": [x, y], "points": [[x, y], …]}`. Give ends by `source`/`target` or by points.

The alarm objects are settings in `style`:
- `hmiTitle`: the heading, empty for none;
- `hmiColumns`: comma list of `time`, `tag`, `description`, `condition`, `value`, `state` (list) or `time`, `event`, `tag`, `description`, `condition`, `value`, `limit` (history);
- `hmiMaxEvents`: history only, default 200;
- `fontSize`.

At Run the Alarm List's heading already has the counts and an **Ack All**
button, and each unacknowledged row has **Ack**.

### Animation links

All fields that take numbers are expressions, as strings.

| Key | Fields (defaults) |
|---|---|
| `fillColor.discrete`, `lineColor.discrete`, `textColor.discrete` | `expr`, `on` (#00CC00), `off` (#808080) |
| `fillColor.analog`, `lineColor.analog`, `textColor.analog` | `expr`, `bands`: `[{"max": "50", "color": "#00CC00"}, {"max": null, "color": "#CC0000"}]`: first band whose `max` the value is below; the last (`max: null`) catches the rest |
| `fillColor.discreteAlarm` (and `line`/`text`) | `tag`, `on` (in alarm), `off` (normal) |
| `fillColor.analogAlarm` (and `line`/`text`) | `tag`, `loLo`, `low`, `normal`, `high`, `hiHi` (colors) |
| `visibility` | `expr`, `sense`: `visible` (shown while true) or `invisible` |
| `blink` | `expr`, `rateMs` ("500"), `attrs`: an array of any of `"fill"`, `"line"`, `"text"` (default `["fill"]`); `fill`, `line`, `text` (colors); `blank` (flash to nothing) |
| `disable` | `expr` (touch ignored while true) |
| `valueDisplay` | `kind` (`analog`, `discrete`, `string`), `expr`, `format` ("0.0"), `prefix`, `suffix`, `onText`, `offText`. At Run it replaces the object's label, so a button can read Start/Stop from a discrete tag. Give a `label` as a design-time placeholder, or the object is blank in the editor and in renders |
| `location.horizontal`, `location.vertical` | `expr`, `atMin`, `atMax`, `offsetMin`, `offsetMax` (pixels) |
| `size.width`, `size.height` | `expr`, `atMin`, `atMax`, `pctMin`, `pctMax`, `anchor` (`left`/`right`/`center`, `top`/`bottom`/`center`) |
| `percentFill.horizontal`, `percentFill.vertical` | `expr`, `atMin`, `atMax`, `pctMin` ("0"), `pctMax` ("100"). The object is clipped to that percentage, from the bottom (vertical) or left (horizontal); the rest of it, outline included, is not drawn. For a vessel, fill a shape with the product color and place an identical shape with `fillColor=none` on top as the outline. Renders show the object fully filled |
| `orientation` | `expr`, `atMin`, `atMax`, `angleMin`, `angleMax` (degrees, clockwise); `pivot: "point"` with `pivotDx`, `pivotDy` (offset from the object's center) to turn about another point |
| `userInput` | `kind` (`analog`, `discrete`, `string`), `tag`, `min`, `max`, `prompt`, `keypad` (true) |
| `pushbutton` | `tag`, `action`: `toggle`, `set`, `reset` or `direct` (1 while pressed), `enableExpr` |
| `pushbutton.action` | Scripts `onDown`, `whileDown` (every `everyMs`), `onUp` |
| `showWindow`, `hideWindow` | `window` (page name), `enableExpr` |
| `slider.horizontal`, `slider.vertical` | `tag`, `atMin`, `atMax`, `travelMin`, `travelMax` (pixels) |

### Expressions and scripts

- **Values:** tag names (`Tank_Level`) and dotfields: `.Value`, `.Name`,
  `.Quality`, `.TimeDate`, `.MinEU`, `.MaxEU`, `.MinRaw`, `.MaxRaw`,
  `.EngUnits`, `.Comment`, `.InAlarm`, `.Acked`.
- **System tags:** `_AlarmsActive`, `_AlarmsUnacked`, `_AckAll`.
- **Operators:** `+ - * / MOD`, `== <> < <= > >=`, `AND OR NOT`, parentheses.
- **Literals:** numbers and `"text"`.
- **Functions:** `Abs`, `Sqrt`, `Int`, `Round`, `Min(a, b)`, `Max(a, b)`,
  `Sqr`, `StringLen`, `Text(value, "0.00")`.
- **Scripts** (window scripts, `pushbutton.action`) are statements separated by
  `;`:
  - assignments: `Pump_Run = 1;`, `Tank_Level.Acked = 1;` (acknowledges),
    `_AckAll = 1;`;
  - `IF cond THEN … ELSE … ENDIF;`.
- **Bad quality:** an expression with a bad-quality input has a bad result
  (Value Display shows `####`), and an `IF` on bad data runs neither branch.

## Suggested loop for an agent

1. Write the spec: tags and devices first, then pages and objects.
2. Run `--hmi-build`. Fix every spec error (exit 1) and every `PROBLEM`
   (exit 2) until the exit code is 0.
3. Run `--hmi-render` and look at the PNGs: layout, overlaps, labels.
   - Each image is one page, cropped to what is drawn on it plus a 10 px
     border, not the whole screen.
   - A popup's page is drawn on its own, not over the page it pops up on and
     without its title bar.
   - Animation isn't applied: values, fills and colors show their design
     state.
4. For changes to an existing project, run `--hmi-dump` first, edit the JSON,
   and build it back (dump → build is lossless).
5. Run `--hmi-publish` with the product name and version. Then install the
   installer on the target PC (`/S` for silent).
