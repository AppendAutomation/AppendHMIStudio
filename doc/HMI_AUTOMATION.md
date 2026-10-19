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
  device's protocol;
- the database sections: a bind list naming a tag the dictionary does not
  have, a column name one of the databases reserves or that needs brackets
  around it, and the same rules the Database dialog applies to a name, a
  data source, a template column and a query;
- the names a database call writes as text — a data source, bind list, table
  template or query that does not exist — and a literal empty where
  expression on `SQLUpdate` or `SQLDelete`, which would act on every row in
  the table.

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
| `touchFill` | The touch background color: what an object with a touch animation is drawn in while the operator holds it down (unset, the default: a press changes nothing) |
| `runtime.windowMode` | `kiosk` (default; full screen, no taskbar), `fullscreen` or `window` (fixed, at the resolution) |
| `runtime.exit` | `shortcut` (default, Ctrl+Alt+Shift+Q), `password` (shortcut then password) or `never` |
| `runtime.exitPassword` | With `exit: password`, the password in plain text; only a salted hash is stored. A dumped spec carries `salt`/`hash` instead |
| `publish` | Remembered Publish dialog options (`productName`, `version`, `publisher`, `scope`, …); optional |
| `security.autoLogoutMin` | Minutes without a touch or key press before the user is logged out (0, the default: never) |
| `scripts` | Application scripts: `onStart` (once, before the first window opens), `whileRunning` (every `everyMs`, default 1000, for as long as the application runs) and `onShutdown` (once as it stops, after its windows close). They run whichever windows are open |

### users

Operators who log in with `ShowLogin()` or `Login()`. See
[HMI_SECURITY.md](HMI_SECURITY.md).

```json
"users": [{"name": "Operator", "level": 100, "password": "op"},
          {"name": "Supervisor", "level": 900, "password": "super"}]
```

| Field | Meaning |
|---|---|
| `name` | 1 to 32 letters, digits, spaces or `. _ - @`; case-insensitive; not `None` |
| `level` | Access level, a whole number 0 to 9999 (default 0) |
| `password` | Plain text; hashed at build, never stored |
| `salt`, `hash`, `iterations` | In place of `password`, as `--hmi-dump` writes them |

### recipeBooks

Named sets of tag values that operators save and load with the recipe script
functions. See [HMI_RECIPES.md](HMI_RECIPES.md).

```json
"recipeBooks": [
  {"name": "Plasticizers", "uploadDownload": true,
   "items": [{"tag": "Edit_TotalVol", "ioTag": "Rcp_TotalVol"}, {"tag": "Edit_Pct0", "ioTag": "Rcp_Pct0"}],
   "recipes": {"Standard": {"Edit_TotalVol": 10, "Edit_Pct0": 60}}}
]
```

| Field | Meaning |
|---|---|
| `name` | 1 to 64 characters; case-insensitive, unique |
| `uploadDownload` | `true` to pair each tag with an Upload/Download tag (default `false`) |
| `items` | The Save/Load tags, in order: `{"tag", "ioTag"}`, or plain tag names. `ioTag` needs `uploadDownload` |
| `recipes` | Optional starting recipes, `{name: {tag: number or text}}`, used until a PC saves its own |

### quickFunctions

Functions this project defines, callable from scripts and from animation
expressions. See [HMI_QUICKFUNCTIONS.md](HMI_QUICKFUNCTIONS.md).

```json
"quickFunctions": [
  {"name": "Scaled",
   "args": [{"name": "Raw", "type": "Analog"}, {"name": "Span", "type": "Analog"}],
   "body": "IF Span == 0 THEN\n\tReturn 0;\nENDIF;\nReturn Raw * 100 / Span;"}
]
```

| Field | Meaning |
|---|---|
| `name` | What calls it. The tag name rules, and not a tag's, a system tag's, a built-in function's or another QuickFunction's name |
| `args` | Up to 8: `{name, type}` with `type` `Analog`, `Discrete` or `Message`. A plain string is an Analog argument. An argument may not take a tag's name |
| `body` | The script that runs, with the arguments in scope. `Return <expression>;` gives a value back and stops there; without one it gives 0 |

A function that writes a tag or calls an action function can be called from a
script only. A body may not call `ShowRecipeSelect` or a waiting `ShowWindow`,
and may not take part in recursion.

### dataSources

The databases the application can open, named by the `SQL*` script functions.
The whole feature is described in [HMI_SQL.md](HMI_SQL.md).

```json
"dataSources": [
  {"name": "Plant", "provider": "mssql", "host": "10.0.0.5", "database": "Batches",
   "user": "hmi", "password": "secret", "options": {"legacyTls": true}},
  {"name": "Local", "provider": "sqlite", "file": "batches.db", "folder": "documents"}
]
```

| Field | Meaning |
|---|---|
| `name` | What `SQLConnect` names. Up to 64 characters, no quotation marks |
| `provider` | `mssql` (Microsoft SQL Server) or `sqlite` (a local file) |
| `host`, `port`, `database`, `user` | SQL Server only. The port defaults to 1433 |
| `password` | SQL Server only. Given in plain text, it is wrapped before the project is written, so the project never holds it as typed; a dump gives the wrapped form back. **Wrapping is obfuscation, not security** |
| `file` | SQLite only: a file name, never a path. It may not be absolute or contain `..` |
| `folder` | SQLite only: `appdata` (the default) or `documents`. A project names a folder rather than a path, because an absolute path names one machine's account and a published application runs on another PC |
| `timeoutMs` | How long to wait for the database, 100 to 600000. Default 15000 |
| `options` | SQL Server only: `encrypt` (default false), `trustServerCertificate` (default true), `legacyTls`, `instanceName` |

Encryption is off by default, and `legacyTls` exists because a plant SQL Server
is often old enough to offer only TLS versions a current computer refuses. If a
connection fails with a protocol or handshake error, turn `legacyTls` on, or
leave `encrypt` off.

A target PC can hold its own credentials for a data source, which win over the
project's, so a site can keep the password out of the project altogether.

### bindLists

A bind list pairs a tag with a database column. A select writes the columns
into the tags; an insert or an update reads the tags into the columns.

```json
"bindLists": [
  {"name": "BatchRow", "items": [
    {"tag": "Batch_Name", "column": "BatchName"},
    {"tag": "Tank_Level", "field": "InAlarm", "column": "LevelAlarm"},
    "Batch_Operator"
  ]}
]
```

| Field | Meaning |
|---|---|
| `name` | What the functions that take a bind list name |
| `items` | `{tag, column}`, in order. A plain string is a tag written to a column of the same name |
| `field` | A tag dotfield, so a bind list can carry `InAlarm` or `Acked`. It must be one a tag has |
| `delim` | Accepted for projects brought over from elsewhere, and otherwise ignored: see below |

Every item needs a tag and a column, and a column may appear only once in a
list.

A value goes into an insert or an update **as a bound parameter**, never as
text pasted into the statement, so nothing has to be quoted or delimited and a
tag holding a quotation mark cannot rewrite the statement. `delim` exists for
projects whose bind lists carry one; it is kept on load and on save, and it
changes nothing.

`SQLInsert` and `SQLUpdate` send the tags' **current** values, which the Run
watches from its start whether or not any window shows them. A value of bad
quality is written as a null and noted in the runtime log: one tag of twenty
may be on a link that is down, and a record with a null in it still says what
was known. A `NOT NULL` column makes the database refuse such a row, which is
the schema's decision to make.

A `TimeDate` dotfield is sent as a date and time rather than as the
millisecond number it is, so it can stamp a `datetime` column. The other
dotfields go as they read.

A bind list names tags from the dictionary, so there are three ways to stamp a
row with the time it was written: a tag's `TimeDate` dotfield, which is when
the value was read; a Message tag a script sets from `_DateTime` just before
the insert, which is when the row was built; or the database's own clock in a
statement of your own (`GETDATE()` on SQL Server,
`CURRENT_TIMESTAMP` on SQLite), which is when it landed.

### tableTemplates

The shape of a table the application creates for itself with
`SQLCreateTable`.

```json
"tableTemplates": [
  {"name": "BatchLog", "columns": [
    {"name": "BatchName", "type": "text", "length": 80, "allowNull": false, "index": "nonunique"},
    {"name": "Target", "type": "number"},
    {"name": "Stamp", "type": "datetime"}
  ]}
]
```

| Field | Meaning |
|---|---|
| `name` | What `SQLCreateTable` names |
| `columns` | `{name, type}` and the fields below. A plain string is a text column |
| `type` | `text`, `integer`, `number`, `decimal`, `boolean`, `date`, `time` or `datetime`. These are the studio's own types; the statement that creates the table maps them to whatever the data source wants |
| `length` | Characters for `text`, total digits for `decimal`. Left out, a text column is created 255 characters wide and a decimal as 18 digits |
| `scale` | `decimal` only: digits after the point, 0 to 38. Default 2 |
| `index` | `none` (default), `nonunique` or `unique` |
| `allowNull` | False to refuse an empty value. Default true |

A column name may appear only once in a template. Validate warns if a column
is named after a keyword that SQL Server or SQLite reserves, such as `USER`,
`KEY` or `ORDER`, and if a name needs square brackets around it wherever a
script mentions it.

`SQLCreateTable` creates the table and then one index per column that asks for
one, named `<table>_<column>_idx`. A table that is already there is left as it
is, so an application can make sure of its table every time it starts. The types become SQL Server's
`NVARCHAR(n)`, `INT`, `FLOAT`, `DECIMAL(p,s)`, `BIT`, `DATE`, `TIME` and
`DATETIME`. SQLite has no date type of its own and treats types as
preferences, so there a date column is `TEXT`, holding what the application
writes into it — ISO 8601 text, which SQLite's own date functions understand —
and a number column is `REAL`.

### queries

SQL statements kept in the project by name, so nothing has to be deployed
beside the installer. `SQLLoadStatement(ConnectionID, "Name")` loads one.

```json
"queries": [
  {"name": "RecentBatches",
   "sql": "SELECT BatchName, Target, Stamp\nFROM BatchLog\nWHERE Target > ?\nORDER BY Stamp DESC"}
]
```

| Field | Meaning |
|---|---|
| `name` | What `SQLLoadStatement` names |
| `sql` | The statement. Any length, line breaks and all. A `?` marks a parameter to be set before the statement runs |

`SQLLoadStatement` names a query of the project rather than a file on disk: a
path belongs to one machine's account, and a published application runs on
another PC. The statement travels in the project, so there is nothing to
deploy beside the installer.

A statement a script runs for itself is built with `SQLSetStatement` and
`SQLAppendStatement`, or loaded with `SQLLoadStatement`, into the one
statement buffer each connection carries. Nothing is put between the pieces an
append joins, because a statement is as often continued in the middle of a
name or a quoted value as between words.

`SQLExecute(ConnectionID, BindList, StatementID)` runs it. Statement 0 is the
buffer, which runs as it stands and is emptied afterwards; a statement
prepared with `SQLPrepareStatement` has a number of its own and can be run
again and again. A `?` in a statement has to be filled in, so the buffer
cannot carry one — a statement with parameters is prepared first, and the
`SQLSetParam*` family addresses it by its number.

A statement that returns rows fills the results table, walked with `SQLFirst`
and the rest through the bind list named in the call; a zero-length bind list
says the rows are not for tags, and they are still counted by `SQLNumRows`. A
statement that returns no rows leaves a selection already in hand alone, so
one can be run in the middle of walking another's rows. Where the columns a
statement returns do not line up with the bind list, the rows that did come
back are still written as far as they go, and the runtime log says so once.
`SQLUpdateCurrent` works only on the rows of a `SQLSelect`, which is the call
that names the table to write back to.

A parameter keeps the type it was first given, because the statement was
prepared for a shape; `SQLClearParam` forgets one, so a different type may
then be set. A character parameter set again adds to what is there, which is
how a value longer than one call's worth is assembled, and once the statement
has run the next value replaces it rather than lengthening it. The advisory
arguments of the older call forms — a character parameter's `MaxLength`, a
decimal's `Precision` and `Scale`, a date and time's `Precision` — may be left
off. `MaxLength` chose between two character types for a driver that no longer
needs telling. `Precision` and `Scale` are passed on to the database, because a
decimal parameter that declares no scale arrives rounded to whole numbers; left
off, they are taken from the digits the value itself has. A date and time's
`Precision` is how many characters of `2026-10-17 14:05:09.270` to keep — 10
the date, 16 to the minute, 19 to the second — and a length that falls inside a
field keeps the fields before it.

`SQLTransact` opens a transaction on a connection, and `SQLCommit` or
`SQLRollback` ends it. A Run that stops with one open rolls it back, as
closing the connection does.

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
| `name` | Letters, digits, `_`, `$`; starting with a letter, `_` or `$`; up to 63 characters; case-insensitive. The system tag names are reserved: `_AlarmsActive`, `_AlarmsUnacked`, `_AckAll`, `_Username`, `_AccessLevel`, `_DateString`, `_TimeString`, `_DateTime`, `_Year`, `_Month`, `_Day`, `_Hour`, `_Minute`, `_Second`, `_Msec`, `_Now` and `_TimeFormat` |
| `type` | `MemoryDiscrete`, `MemoryInteger`, `MemoryReal`, `MemoryMessage`, `IODiscrete`, `IOInteger`, `IOReal`, `IOMessage`, or the indirect types `IndirectDiscrete`, `IndirectAnalog` and `IndirectMessage` (only `comment` besides; see [HMI_INDIRECT.md](HMI_INDIRECT.md)) |
| `comment` | Description; also the alarm description |
| `initial` | Starting value (memory tags) |
| `engUnits`, `minEU`, `maxEU` | Units and engineering range (Integer/Real) |
| `onMsg`, `offMsg` | Text for 1 and 0 (Discrete) |
| `device`, `address` | I/O tags: device name and PLC address |
| `scaled`, `minRaw`, `maxRaw` | I/O analog: set `scaled: true` to map raw minRaw..maxRaw to minEU..maxEU |
| `scanMs` | Per-tag scan period (I/O) |
| `retentive` | `true` on a memory tag keeps its last value from one run to the next (it starts from the saved value instead of `initial`) |
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
| `window` | Window properties. `type`: `replace` (default; closes windows it overlaps), `overlay` or `popup` (modal). Also `titleBar` (true adds a bar inside the height), `x`, `y`, `width`, `height` (default: the whole screen), and the scripts `onShow`, `whileShowing` (every `everyMs`, default 1000) and `onHide`. With a title bar: `titleHeight` (24), `titleFont`, `titleFontSize` (13), `titleFill`, `titleColor` -- an empty font or color keeps the built-in one -- and `movable` (false; a popup the operator can move by dragging its title bar). See [HMI_WINDOWS.md](HMI_WINDOWS.md) |
| `objects` | The objects on the page |

A window shows the part of its page under its rectangle, so draw a popup's
objects at the popup's `x`/`y`, below its title bar if it has one. A title
bar shows the page name and a × close button.

### objects

| Field | Meaning |
|---|---|
| `id` | Optional, unique on the page; needed for connector ends |
| `type` | Shorthand style: `rect`, `roundedRect`, `ellipse`, `text`, `button`, `line`, `arrow`, `triangle`, `cylinder`, `arc`, `circleArc`, `alarmList`, `alarmHistory`, `recipeList`, `list` or `trend`. Optional when `style` names a shape |
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
- `hmiTitle`: the heading's text;
- `hmiTitleBar`: `0` for no heading bar at all, `1` to keep it; by default an object has a bar unless its `hmiTitle` is empty;
- `hmiColumns`: comma list of `time`, `tag`, `description`, `condition`, `value`, `state` (list) or `time`, `event`, `tag`, `description`, `condition`, `value`, `limit` (history);
- `hmiMaxEvents`: history only, default 200;
- `fontSize`;
- the colors, each a background and a text key: `hmiTitleFill`/`hmiTitleColor`
  (title bar), `hmiHeadFill`/`hmiHeadColor` (column header), and for a list
  `hmiActUnackFill`/`hmiActUnackColor`, `hmiActAckFill`/`hmiActAckColor`,
  `hmiClrUnackFill`/`hmiClrUnackColor`, or for a history
  `hmiAlmFill`/`hmiAlmColor`, `hmiRtnFill`/`hmiRtnColor`,
  `hmiAckFill`/`hmiAckColor`, `hmiChgFill`/`hmiChgColor`. A key you leave out
  keeps the built-in color. A list has three row states, not four: an alarm
  stays listed until it has both returned to normal and been acknowledged.

The background, border and text font of an alarm object are its style's
`fillColor`, `strokeColor`, `strokeWidth`, `fontFamily`, `fontSize` and
`fontColor`, as a trend's are.

At Run the Alarm List's heading already has the counts and an **Ack All**
button, and each unacknowledged row has **Ack**. With `hmiTitleBar=0` there is
no heading, so the counts and Ack All go with it; the rows keep their own Ack
buttons and a script can acknowledge through `_AckAll`.

A `recipeList` object takes its settings from a `recipeList` link, a `list` object from a `list` link, and a `trend` object from a `trend` link (below). Their background, border, line width and font are their style's `fillColor`, `strokeColor`, `strokeWidth`, `fontFamily`, `fontSize` and `fontColor`.

The arcs are open curves along an ellipse (`arc`, filling the box) or a circle (`circleArc`, kept round). Set them in `style`:
- `arcStart`, `arcEnd`: degrees as on a protractor, 0 at the right and counterclockwise, the arc running counterclockwise from start to end (default 0 to 180, the top half; a start greater than the end sweeps through 0);
- `strokeWidth`, `arcEndWidth`: the line width at the start and at the end (the end defaults to the start; differing widths are drawn solid, not dashed);
- `startArrow`, `endArrow`: `none`, `classic`, `classicThin`, `block`, `blockThin`, `open`, `openThin`, `oval`, `diamond`, `diamondThin`, `box`, `halfCircle`, `dash`, `cross`, `circle` or `circlePlus`;
- `startSize`, `endSize`: the end sizes;
- `startFill`, `endFill`: `0` for an outline end.

For example a gauge sweep over the top, `{"type": "circleArc", "x": 40, "y": 40, "width": 140, "height": 140, "style": "arcStart=315;arcEnd=225;strokeWidth=8;endArrow=block;"}`.

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
| `enable` | `expr`, `sense`: `enabled` (touch works only while true) or `disabled` (touch ignored while true). Bad quality disables. Gates every touch link on the object, e.g. `"_AccessLevel >= 500"` |
| `disable` | `expr` (touch ignored while true); superseded by `enable`, still accepted |
| `valueDisplay` | `kind` (`analog`, `discrete`, `string`), `expr`, `format` ("0.0"), `prefix`, `suffix`, `onText`, `offText`. At Run it replaces the object's label, so a button can read Start/Stop from a discrete tag. Give a `label` as a design-time placeholder, or the object is blank in the editor and in renders |
| `location.horizontal`, `location.vertical` | `expr`, `atMin`, `atMax`, `offsetMin`, `offsetMax` (pixels) |
| `size.width`, `size.height` | `expr`, `atMin`, `atMax`, `pctMin`, `pctMax`, `anchor` (`left`/`right`/`center`, `top`/`bottom`/`center`) |
| `percentFill.horizontal`, `percentFill.vertical` | `expr`, `atMin`, `atMax`, `pctMin` ("0"), `pctMax` ("100"). The object is clipped to that percentage, from the bottom (vertical) or left (horizontal); the rest of it, outline included, is not drawn. For a vessel, fill a shape with the product color and place an identical shape with `fillColor=none` on top as the outline. Renders show the object fully filled |
| `orientation` | `expr`, `atMin`, `atMax`, `angleMin`, `angleMax` (degrees, clockwise); `pivot: "point"` with `pivotDx`, `pivotDy` (offset from the object's center) to turn about another point |
| `userInput` | `kind` (`analog`, `discrete`, `string`), `tag`, `min`, `max`, `prompt`, `keypad` (true), `masked` (false; `true` on a string entry shows dots, for passwords) |
| `pushbutton` | `tag`, `action`: `toggle`, `set`, `reset` or `direct` (1 while pressed), `enableExpr` |
| `pushbutton.action` | Scripts `onDown`, `whileDown` (every `everyMs`), `onUp` |
| `showWindow`, `hideWindow` | `window` (page name), `enableExpr` |
| `slider.horizontal`, `slider.vertical` | `tag`, `atMin`, `atMax`, `travelMin`, `travelMax` (pixels) |
| `recipeList` | On a `recipeList` object only: `book`, `title` (empty: the book's name), `selectedTag` (message tag the selection follows and touch writes), `upTag`, `downTag` (discrete tags; a 0 to 1 change moves the selection), `arrows` (false; ▲▼ buttons in the heading), `titleFill`, `titleColor` (the heading's colors), `selFill`, `selColor` (the highlighted row's); an empty color keeps the built-in one |
| `list` | On a `list` object only: `name` (unique; empty gets the next free `ListN`), `title` (the list's name), `titleBar` (false), `items` (starting items, one per line), `selectedTag` (message tag the selection is written to), `upTag`, `downTag` (discrete tags; a 0 to 1 change moves the selection), `arrows` (false; ▲▼ buttons in the heading), `titleFill`, `titleColor`, `selFill`, `selColor`; an empty color keeps the built-in one. See [HMI_LISTS.md](HMI_LISTS.md) |
| `trend` | On a `trend` object only: `name` (unique; empty gets the next free `TrendN`), `title` (`""`), `spanSec` (600; 10 to 86400), `sampleMs` (1000; at least 100), `refreshMs` (1000), `gridColor`, `pens` (up to 8: `[{tag, min, max, color}]`, analog or discrete tags; `min`/`max` `""` for the tag's range, -0.5 to 1.5 for a discrete tag). See [HMI_TRENDS.md](HMI_TRENDS.md) |

### Expressions and scripts

- **Values:** tag names (`Tank_Level`) and dotfields: `.Value`, `.Name`,
  `.Quality`, `.TimeDate`, `.MinEU`, `.MaxEU`, `.MinRaw`, `.MaxRaw`,
  `.EngUnits`, `.Comment`, `.InAlarm`, `.Acked`.
- **System tags:** `_AlarmsActive`, `_AlarmsUnacked`, `_AckAll`, `_Username`
  (`None` when logged out), `_AccessLevel` (0 when logged out), and the date
  and the time:
  - `_DateString` (`2026-10-17`), `_TimeString` (`14:05:09`) and `_DateTime`
    (`2026-10-17 14:05:09.270`) are text; `_Year`, `_Month` (1-12), `_Day`,
    `_Hour` (0-23), `_Minute`, `_Second` and `_Msec` are numbers.
  - `_Now` is the date and time in the format `_TimeFormat` holds, which is
    `HH:mm:ss` at the start of every Run. See
    [HMI_DATETIME.md](HMI_DATETIME.md) for the formats.
  - They are plant-local time, and `_DateTime` is the text a `datetime`
    column takes as it stands, so a script can stamp a row with it.
  - They are worked out as they are read, so a script reading `_Msec` gets
    the millisecond it ran in. A screen showing one is repainted once a
    second.
  - All of them are read-only but `_AckAll` and `_TimeFormat`, which a script
    writes.
  - The Tag Dictionary and Browse Tags list them, marked **System**; they
    cannot be edited or deleted there.
- **Operators:** `+ - * / MOD`, `== <> < <= > >=`, `AND OR NOT`, parentheses.
- **Literals:** numbers and `"text"`.
- **Functions:** `Abs`, `Sqrt`, `Int`, `Round`, `Min(a, b)`, `Max(a, b)`,
  `Sqr`, `StringLen`, `Text(value, "0.00")`.
- **The time as text:** `Now()` is `HH:mm:ss`, and `Now(format)` is the date
  and time in a format of your own -- a pattern (`yyyy-MM-dd HH:mm:ss`,
  `MM/dd/yyyy hh:mm tt`) or the name of a popular one (`TIME`, `TIME12`,
  `HHMM`, `DATE`, `US`, `EU`, `DATETIME`, `STAMP`, `ISO`, `LONG`). It is not
  an action, so a link may show it, and it is repainted once a second. A
  format that means nothing gives `HH:mm:ss`, and `--hmi-check` reports a
  literal one that does. See [HMI_DATETIME.md](HMI_DATETIME.md).
- **Text functions**, in links as well as scripts. A character position counts
  from 1:
  - `StringLeft(text, count)`, `StringRight(text, count)`,
    `StringMid(text, start, count)` -- a count that runs past the end stops
    there;
  - `StringTrim(text)` (the spaces at either end), `StringUpper(text)`,
    `StringLower(text)`;
  - `StringInString(text, find)`: where `find` starts, or 0 when it is not
    there;
  - `StringReplace(text, find, with)`: every occurrence, not just the first;
  - `StringFromIntg(number[, base])`: the digits, in base 2 to 36 (10 by
    default; base 16 gives `ff`, so wrap it in `StringUpper` for `FF`);
  - `StringFromReal(number, decimals[, kind])`: `"f"` a plain number (the
    default), `"e"` exponential, `"g"` the shorter of the two with trailing
    zeros dropped and `decimals` counting significant digits;
  - `StringToIntg(text)`, `StringToReal(text)`: the number the text starts
    with, or 0 when it holds none -- an operator's typing reaches these, so
    they do not fail on it.
- **Scripts** (application scripts, window scripts, `pushbutton.action`) are
  statements separated by `;`:
  - assignments: `Pump_Run = 1;`, `Tank_Level.Acked = 1;` (acknowledges),
    `_AckAll = 1;`;
  - `IF cond THEN … ELSE … ENDIF;`;
  - loops: `FOR tag_or_variable = start TO end [STEP by] … NEXT;` and
    `WHILE cond … ENDWHILE;`. A FOR reads its bounds once, counts them itself
    (a tag as the loop variable is written every pass, so prefer a `DIM`
    variable) and refuses a `STEP` of 0; a WHILE evaluates its condition
    before every pass, and bad quality ends it, as it stops an IF from
    branching. All the loops in one run of a script may take 100,000 passes
    together, after which the script reports the cap in the runtime log and
    stops — so a condition that never comes false cannot hang the window.
    `FOR`, `TO`, `STEP`, `NEXT`, `WHILE` and `ENDWHILE` are keywords only
    where a loop can put them, so a tag named `Step` or `Next` keeps working;
  - local variables: `DIM name[, name …] AS Analog | Discrete | Message;` at
    the top level of the script (not inside IF, FOR or WHILE), before the
    names are used.
    They start at 0 or `""` each time the script runs, have no dotfields, and
    may not share a name with a tag or system tag (Validate reports it). An
    Analog holds a number, a Discrete 0 or 1, a Message text;
  - security functions, in scripts only: `ShowLogin();`, `Login(name, password)`
    (1 or 0), `Logout();`, `ChangePassword(old, new)` (1 or 0),
    `ShowUserManager();`;
  - recipe functions, in scripts only: `RecipeSave(book, name)`,
    `RecipeLoad`, `RecipeUpload`, `RecipeDownload`, `RecipeExport`,
    `RecipeImport` (all `(book, name)`), `RecipeDelete(book, name[, confirm])`,
    `RecipeRename(book, name, newName)` (each 1 or 0), and
    `x = ShowRecipeSelect(book[, x, y, w, h])` (the chosen name or `""`; only
    as a statement or the whole right-hand side of an assignment). See
    [HMI_RECIPES.md](HMI_RECIPES.md);
  - `LinkIndirectTag(indirect, tag)`, in scripts only: points an indirect tag
    at a tag, both given as names in text (`"SV" + Unit + "_Status"`); 1 or 0.
    See [HMI_INDIRECT.md](HMI_INDIRECT.md);
  - `ShowWindow(name[, left, top[, modal[, wait]]])`, in scripts only: opens a
    window (page) by name, as text. `left`, `top` move it for this showing
    (screen pixels; `""` keeps its own); `modal` 0 or 1 overrides a popup's
    modality; `wait` 1 pauses the script until the window closes (default:
    carry on at once). Only as a statement or the whole right-hand side of an
    assignment; 1, or 0 with a Window Error;
  - **QuickFunctions**, the functions the project defines (`quickFunctions`,
    above): called like any other, `Scaled(Raw, 4000)`, from a script or from
    an animation expression. Their arguments are typed and their bodies give a
    value back with `Return <expression>;` (without one, 0). One that writes a
    tag or calls an action function can be used in scripts only. See
    [HMI_QUICKFUNCTIONS.md](HMI_QUICKFUNCTIONS.md);
  - `ScreenToPDF()`, in scripts only: saves the screen as shown (popups
    included) as a one-page landscape Letter PDF, fitted and centered with
    at least 1/4" margins, through a Save dialog that offers the screen's
    page name as the file name (scrubbed of characters a file name cannot
    hold). Like `RecipeExport` it returns 1 once the dialog opens and the
    script carries on; a failure shows a Screen to PDF Error. Append HMI Web
    downloads the file;
  - `TrendSetPen(trend, tag)`, `TrendPause(trend)`, `TrendPlay(trend)`,
    `TrendScrollBack(trend)` and `TrendScrollForward(trend)`, in scripts only:
    names as text. SetPen selects the pen whose range and name the y axis
    shows (drawn 1 px thicker); the scrolls move the shown times by half the
    span (back pauses a live trend; forward stops at now); Play returns to
    live. 1, or 0 with a function error. See [HMI_TRENDS.md](HMI_TRENDS.md);
  - `ListClear(list)`, `ListAdd(list, item)`, `ListRemove(list, item)`,
    `ListSelect(list, item)`, `ListLoad(list, text)` and
    `ListSort(list, order)`, in scripts only: names and items as text. Add
    puts an item at the bottom and Load the comma-separated, trimmed items
    of its text; Remove and Select find an item whatever its case and do
    nothing (still 1) when the list hasn't got it; Sort takes `"ASC"`,
    `"DESC"`, `"A"` or `"D"` in any case, ignores case and keeps the
    selection in view. 1, or 0 with a List Error. See
    [HMI_LISTS.md](HMI_LISTS.md);
  - **the `SQL*` family**, in scripts only: `SQLConnect(ConnIdTag,
    "DataSource")` and `SQLDisconnect`; `SQLSelect(ConnId, "Table",
    "BindList", "WhereExpr", "OrderByExpr")` with `SQLFirst`, `SQLNext`,
    `SQLPrev`, `SQLLast`, `SQLGetRecord`, `SQLNumRows` and `SQLEnd`;
    `SQLInsert`, `SQLUpdate`, `SQLUpdateCurrent`, `SQLDelete`,
    `SQLClearTable`, `SQLCreateTable` and `SQLDropTable`; the statement and
    parameter calls `SQLSetStatement`, `SQLAppendStatement`,
    `SQLLoadStatement`, `SQLPrepareStatement`, the `SQLSetParam*` family,
    `SQLClearParam`, `SQLExecute` and `SQLClearStatement`; the prepared
    insert `SQLInsertPrepare`, `SQLInsertExecute` and `SQLInsertEnd`;
    `SQLTransact`, `SQLCommit` and `SQLRollback`; and `SQLErrorMsg(code)`,
    which gives the words for a result code as text. Each of the others
    returns a result code, 0 for success (`SQLNumRows` returns the count).
    The ones that reach a database pause the script, so like
    `ShowRecipeSelect` they may be used only as a statement or as the whole
    right-hand side of an assignment. See [HMI_SQL.md](HMI_SQL.md).
- **Text:** `+` joins text when either side is text, converting the other
  (`"Real" + 2` is `"Real2"`; discrete values give `"1"` or `"0"`).
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
