# hmi-comms

A standalone PLC communications server. It takes a list of tags, groups them
by device and polls each device efficiently over **EtherNet/IP (Logix)**,
**SLC 500 / MicroLogix (PCCC)** or **Modbus TCP**, and serves the values over a
WebSocket.

drawio-desktop-hmi ships it and starts it on demand (the Electron main process
relays for the HMI), but nothing in it depends on draw.io or Electron: any
WebSocket client that speaks the protocol below can use it, including the
published-application runtime.

- .NET 8, self-contained single-file executable, no runtime to install
- One polling thread per device connection, subscription-driven: only tags
  someone is subscribed to are read
- Protocol libraries: [cslogix](https://github.com/AppendAutomation/cslogix),
  [cscomm3_slc](https://github.com/AppendAutomation/cscomm3_slc) (submodules in
  `lib/`) and [FluentModbus](https://github.com/Apollo3zehn/FluentModbus)

## Building and testing

Requires the .NET 8 SDK or newer.

```bash
npm run build-comms                  # this machine's platform, into comms/publish/<os>-<arch>/
npm run build-comms -- --rid win-x64 # a specific runtime (win-x64, win-arm64, linux-x64,
                                     # linux-arm64, osx-x64, osx-arm64); --mac for both macOS
npm run build-comms -- --all         # every runtime the app ships
npm run test-comms                   # dotnet test comms/HmiComms.sln
```

The electron-builder configs pick `comms/publish/` up as `extraResources`, so
the server lands in `resources/comms/` of each package, outside the asar.

Layout:

| Path | What |
|---|---|
| `src/Hmi.Comms.Core` | Model, scheduler (`DeviceWorker`), engine and sessions, scaling, quality |
| `src/Hmi.Comms.Modbus`, `.Logix`, `.Slc` | Protocol drivers: address parsing, read planning, decoding, writes |
| `src/Hmi.Comms.Server` | The `hmi-comms` executable: command line, WebSocket, protocol |
| `test/*` | xUnit tests, including simulators for each protocol |
| `lib/cslogix`, `lib/cscomm3_slc` | Submodules |

## Running

```
hmi-comms --listen <host:port> (--token <t> | --token-stdin) [options]

  --listen 127.0.0.1:0     address to bind; port 0 picks a free one
  --token <t>              the token clients must present
  --token-stdin            read the token from the first line of stdin (keeps it
                           out of the process list); the server then exits when
                           stdin closes
  --allow-shutdown         honour the shutdown message
  --parent-pid <pid>       exit when this process exits
  --config <file>          devices and tags (and optionally the token) from a file
  --config-mode client|file
                           file: the file's configuration is all a client gets
  --allow-origin <origin>  browser origins allowed to connect (repeatable; * for any)
  --log-level debug|info|warn|error
  --log-file <path>
  --coalesce-ms <n>        how long value changes gather before a send (default 20)
```

On start it prints exactly **one line** to stdout and nothing after it:

```json
{"event":"listening","v":1,"host":"127.0.0.1","port":53817,"pid":1234,"version":"0.1.0"}
```

or `{"event":"fatal","message":"..."}` followed by exit code 2. Logs go to
stderr (and `--log-file`): one line per session and device state change, never
one per poll.

A token is always required, and listening on anything but loopback without one
is refused.

### Standalone configuration file

```json
{
  "token": "change-me",
  "devices": [
    {"name": "Line1", "protocol": "logix", "host": "10.0.0.5", "options": {"slot": 0}}
  ],
  "tags": [
    {"id": "Tank_Level", "device": "Line1", "address": "Program:Main.Tank.Level"}
  ]
}
```

The shapes are exactly those of the `configure` message. Every session starts
with this configuration; with `--config-mode file`, `configure` is refused and
clients list the tags with `tags`.

## Protocol, version 1

**Transport.** WebSocket at `ws://<host>:<port>/v1`, one JSON object per text
frame. Requests carry an `id` of the client's choosing and replies echo it.
Unknown fields are ignored; new message types are additive.

**Authentication.** The first message must be `hello` with the token. Anything
else first, a wrong token, or no hello within 5 s closes the socket with code
**4001**; an unsupported protocol version closes it with **4002**. Browsers
cannot set headers on a WebSocket, so the token travels in the first message;
requests with an `Origin` header are refused (HTTP 403) unless the origin is
allowed with `--allow-origin`.

### Values

Values travel as tuples:

```
[handle, value, quality, timestamp, status?, error?]
```

- `value`: number, boolean, string or null. Integers beyond ±2^53 are sent as
  strings, so no digits are lost.
- `quality`: OPC DA scale, `192` good, `0` bad.
- `timestamp`: UTC milliseconds of the read that produced the value.
- `status` and `error` appear only when the value is bad:

| status | meaning |
|---|---|
| `waiting` | not read yet |
| `comm` | the connection to the device is down; every tag on it goes bad at once |
| `device` | the device refused this tag; `error` has its reason (e.g. `Path segment error`, `Illegal data address (exception 2)`) |
| `config` | the tag cannot be read: unknown device, bad address |
| `disabled` | the device is disabled |
| `unavailable` | (used by the HMI when the server cannot be reached) |

### Messages

**hello**

```json
{"t":"hello","id":1,"v":1,"token":"...","client":"my-runtime/1.0"}
{"t":"hello","id":1,"ok":true,"v":1,"server":"hmi-comms/0.1.0","session":"s3",
 "configMode":"client","protocols":["logix","modbus","slc"]}
```

**configure**: replaces the session's devices and tags. Device names are the
session's own; two sessions naming the same PLC share one connection, and the
same address is read once for both. Every tag gets a handle, bad ones
included.

```json
{"t":"configure","id":2,
 "devices":[
   {"name":"Line1","protocol":"logix","host":"10.0.0.5","options":{"slot":0}},
   {"name":"Pumps","protocol":"modbus","host":"10.0.0.9","port":502,"timeoutMs":2000,
    "options":{"unitId":1,"byteOrder":"MLE"}},
   {"name":"Old","protocol":"slc","host":"10.0.0.7","enabled":true}],
 "tags":[
   {"id":"Level","device":"Line1","address":"Program:Main.Level",
    "scale":{"rawMin":0,"rawMax":32767,"euMin":0,"euMax":100},"deadband":0.1},
   {"id":"Flow","device":"Pumps","address":"HR:100:FLOAT"},
   {"id":"Run","device":"Pumps","address":"CO:4","readOnly":true},
   {"id":"Count","device":"Old","address":"C5:0.ACC"}]}

{"t":"configureResult","id":2,"ok":true,
 "tags":[{"id":"Level","h":1,"normalized":"Program:Main.Level"},
         {"id":"Flow","h":2,"normalized":"HR:100:FLOAT:MLE"},
         {"id":"Run","h":3,"normalized":"CO:4"},
         {"id":"Count","h":4,"normalized":"C5:0.ACC"}],
 "errors":[]}
```

Device fields: `name`, `protocol`, `host`, `port` (default per protocol),
`timeoutMs` (3000), `minScanMs` (50), `enabled` (true), `options` (below).
Tag fields: `id`, `device`, `address`, `dataType` (a hint for addresses that
name no type: BOOL, INT, DINT, REAL, STRING...), `deadband` (in engineering
units), `scale` (`rawMin`, `rawMax`, `euMin`, `euMax`, `clamp`), `readOnly`.

Scaling is applied by the server both ways: reads are converted to
engineering units, writes back to raw (rounded and range-checked for integer
tags).

**tags** lists the session's tags and handles, in the same form as
configureResult (useful with a `--config` file).

**subscribe**: replaces the subscription. The reply comes first, then a full
**snapshot** of every requested tag, then **change** messages. Tags may be
named by id or handle. `rateMs` is the poll period; `rates` gives individual
tags their own (e.g. each device's scan rate).

```json
{"t":"subscribe","id":3,"tags":["Level","Flow",4],"rateMs":250,"rates":{"Count":1000}}
{"t":"subscribeResult","id":3,"ok":true}
{"t":"snapshot","sub":3,"values":[[1,42.3,192,1790000000123],[2,null,0,1790000000000,"waiting"],
                                  [4,17,192,1790000000123]]}
{"t":"change","values":[[1,42.4,192,1790000000373],[2,null,0,1790000000380,"comm","Connection refused"]]}
```

Only subscribed tags are polled. Changes are coalesced per session, latest
value per tag, so a slow client gets fewer, merged updates rather than a
growing queue. Replies are never held up behind changes. A change is sent when
the quality, status or error changes, or the value moves by more than the
tag's deadband.

**unsubscribe**: `{"t":"unsubscribe","id":4}` gives `unsubscribeResult`.

**read**: reads now, whatever the schedule.

```json
{"t":"read","id":5,"tags":["Flow"]}
{"t":"readResult","id":5,"values":[[2,12.5,192,1790000000400]]}
```

**write**: by id (`values`) or handle (`handles`). Each result is known only
once the device has answered. Values are coerced to the tag's type: integers
rounded and range-checked, strings length-checked, booleans from
`true`/`false`/`1`/`0`.

```json
{"t":"write","id":6,"values":{"Flow":12.5,"Run":true},"handles":[[1,50]],"timeoutMs":5000}
{"t":"writeResult","id":6,"results":{"Flow":{"ok":true},"Run":{"ok":false,"error":"Tag is read-only"},
                                     "1":{"ok":true}}}
```

**validate**: checks addresses for a protocol without configuring anything.

```json
{"t":"validate","id":7,"protocol":"modbus","addresses":["HR:5:FLOAT","40001"],
 "options":{"byteOrder":"MLE"},"dataType":"REAL"}
{"t":"validateResult","id":7,"results":[
  {"ok":true,"normalized":"HR:5:FLOAT:MLE","dataType":"Float32","writable":true},
  {"ok":false,"error":"Use HR:0 for 40001 (offsets are 0-based: table and offset)"}]}
```

**probe**: tries a device's connection, then disconnects.

```json
{"t":"probe","id":8,"device":{"name":"P","protocol":"slc","host":"10.0.0.7"}}
{"t":"probeResult","id":8,"ok":false,"error":"Connection refused","ms":3.1}
```

**status**: each of the session's devices; also pushed whenever one changes
state (`disconnected`, `connecting`, `connected`, `backoff`, `idle`,
`disabled`).

```json
{"t":"status","devices":[{"name":"Line1","state":"connected","lastError":null,"pollMs":18.2,"badPoints":0}]}
```

**diag**: statistics per device: requests per cycle, average and maximum poll
time, overruns, reconnects, write counts and the current read plan, which
shows exactly how tags were grouped into requests.

**ping** gives **pong**. **shutdown** stops the server, but only if it was
started with `--allow-shutdown`. Errors come back as
`{"t":"error","id":n,"code":"bad_request","message":"..."}`.

## Devices and addresses

Connection failures reconnect with capped, jittered backoff (0.5 s rising to
30 s). A connection nobody needs is closed after 30 s.

### EtherNet/IP: `logix` (ControlLogix, CompactLogix, Micro800)

Options: `slot` (0), `micro800` (false). Port 44818.

Addresses are tag names, case-insensitive: `Tank_Level`,
`Program:Main.Tank_Level`, `Motor.Amps`, `Arr[3]`, `Grid[1,2]`, `Status.5`
(a bit of an integer). The type is not declared: the controller reports it on
the first read, and writes use it. Bits are read with their integer (bits of
one integer share one read) and written with Read-Modify-Write, which the
controller applies atomically.

Reads are packed into Multiple Service Packets sized to the connection the
controller grants: 4002 bytes with a Large Forward Open, 504 otherwise. Sizes
are learned from earlier replies; a packet refused as too large is split.

### SLC 500 / MicroLogix: `slc`

Options: `maxBytesPerRequest` (236), `maxGapElements` (118), `swapStringBytes`
(true: SLC processors store string characters with each word's bytes swapped).
Port 44818.

| Address | Meaning |
|---|---|
| `N7:0`, `N7:0/3` | integer, bit of an integer |
| `B3:2/5`, `B3/37` | bit (word 2 bit 5; the second form counts bits from the start of the file) |
| `F8:1` | float |
| `L9:0`, `L9:0/20` | long, bit of a long |
| `S:1/5` | status file |
| `ST9:0`, `A10:0` | string (82 characters), ASCII (2 characters) |
| `T4:0.ACC` `.PRE` `.EN` `.TT` `.DN` | timer members |
| `C5:0.ACC` `.PRE` `.CU` `.CD` `.DN` `.OV` `.UN` | counter members |
| `R6:0.LEN` `.POS` `.EN` `.EU` `.DN` `.EM` `.ER` `.UL` `.IN` `.FD` | control members |
| `I:1.0/3`, `O:2.0` | input and output words by slot and word (inputs are read-only) |

Neighbouring elements of a file are read in one request of up to 236 bytes
(118 integers, 59 floats, 39 timers, 2 strings). A run the processor refuses
(usually one reaching past the end of a file) is split until only the missing
element is bad. Bits are written with masked writes; everything else with one
typed write.

### Modbus TCP: `modbus`

Options: `unitId` (1), `byteOrder` (`BE`), `maxGapRegisters` (16),
`maxGapCoils` (64), `maxRegsPerRead` (125), `maxCoilsPerRead` (2000). Port 502.

Addresses: `HR|IR|CO|DI:<offset>[.bit][:TYPE][:ORDER]`, with **0-based**
offsets. `HR:0` is the first holding register (classic 40001). Classic 5- and
6-digit references are refused with the right spelling, rather than guessed.

- Types: `INT16` (default), `UINT16`, `INT32`, `UINT32`, `FLOAT`, `INT64`,
  `UINT64`, `DOUBLE`, `STRINGn` (n characters). CO and DI are BOOL; `HR:4.3`
  is bit 3 of a register.
- Byte orders, for the wire bytes A B C D of a 32-bit value:

  | Order | Bytes | Description |
  |---|---|---|
  | `BE` | ABCD | Modbus standard |
  | `LE` | DCBA | fully reversed |
  | `MBE` | BADC | bytes swapped within each word; a single INT16 is swapped too |
  | `MLE` | CDAB | word order reversed, the common PLC float order; INT16 is unchanged |

  64-bit values follow the same rule over four words.
- Reads coalesce per unit and table within the gap limit and request caps. A
  block the device refuses (exception 2 or 3) is split until only the missing
  register is bad; busy and gateway exceptions fail the block without dropping
  the connection. Units behind one gateway share a socket.
- Writes: FC05 for coils, FC06 for one register, FC16 for more, and
  read-modify-write for a bit of a register. A change the device makes to the
  register's other bits in between would be overwritten.
