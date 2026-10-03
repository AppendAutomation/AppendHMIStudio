# Indirect tags

An indirect tag has no value of its own. A script points it at another tag
with `LinkIndirectTag`, and from then on reading or writing the indirect tag
reads or writes that tag. One window or faceplate can therefore serve many
devices: the button that opens it links the indirect tags to the device's
tags.

## Defining indirect tags

In **HMI > Tag Dictionary**, choose one of the indirect types under **New
tag...** or as a tag's **Type**:

| Type | Can be linked to |
|---|---|
| `IndirectDiscrete` | Discrete tags (`MemoryDiscrete`, `IODiscrete`) |
| `IndirectAnalog` | Integer and real tags (`MemoryInteger`, `MemoryReal`, `IOInteger`, `IOReal`) |
| `IndirectMessage` | Message tags (`MemoryMessage`, `IOMessage`) |

An indirect tag has only a name, a type and a comment. Its range, units,
alarms and device are those of the tag it is linked to. It cannot be
retentive.

Because the type is known, the editor checks an indirect tag like any other.
For example, an `IndirectAnalog` can drive a Value Display or an analog User
Input, and an `IndirectDiscrete` a pushbutton.

## LinkIndirectTag

```
LinkIndirectTag(indirect, tag)
```

- **Where it works:** in scripts only (Action Scripts and window scripts).
- **Arguments:** both are tag **names** as text: string literals (`"Valve"`)
  or string expressions (`"SV" + Unit + "_Status"`, or a message tag holding
  a name).
- **Replacing a link:** a later call replaces the link.
- **Returns:** 1 when linked, 0 when not.
- **Validation:** names given as literals are checked when you validate. So is
  passing a tag itself rather than its name, e.g.
  `LinkIndirectTag(Valve, "SV001")` instead of `LinkIndirectTag("Valve", "SV001")`.
  The exception is a message tag, which is taken to hold a name.

```
{ The SV-001 button of a valve faceplate }
LinkIndirectTag("FP_Status", "SV001_Status");
LinkIndirectTag("FP_Mode", "SV001_Mode");
LinkIndirectTag("FP_Name", "SV001_Name");

{ The same, built from a number }
LinkIndirectTag("FP_Status", "SV" + Text(Unit, "000") + "_Status");
```

### Text in expressions

`+` joins text: when either side is text, the other is converted, so
`"SV" + 1` is `"SV1"`.

- **Numbers:** convert as written (`2.5` gives `"2.5"`).
- **Discrete values:** convert to `"1"` or `"0"`.
- **Padding:** `Text(value, "000")` pads with zeros.

## At run time

- **One set of links:** there is one set of links for the running
  application. A link made in one window applies in every window, including
  popups opened afterwards. In Append HMI Web, each browser keeps its own
  links.
- **Before the first link:** an indirect tag has bad quality (a Value Display
  shows `####`), and writes to it are ignored (and logged).
- **Values follow the linked tag:** screens follow it at once. Changing the
  link updates everything that shows the indirect tag.
- **Dotfields:** they describe the linked tag. `.Name` gives the linked tag's
  name, so a faceplate can show which device it is on. `.MinEU`, `.MaxEU`,
  `.EngUnits`, `.Comment`, `.InAlarm` and `.Acked` come from the linked tag,
  and `Valve.Acked = 1` acknowledges its alarm. Alarm color links on an
  indirect tag follow the linked tag's alarms.
- **Recipe books** may list indirect tags; they save and load whatever tag
  each is linked to at the time.
- **Links are not saved:** every Run starts unlinked. Link in the window's On
  show script, or in the button that opens it.

### When LinkIndirectTag fails

The link stays as it was, the function returns 0, and an **Indirect Tag
Error** window tells the operator the call and the reason. The window opens
after the script finishes, and repeated failures are collected in it. The
reasons are:

- an empty name;
- no tag of that name;
- the first name is not an indirect tag;
- the second is an indirect tag itself;
- the types don't match, e.g. an `IndirectAnalog` and a message tag.

## Command line

In a spec for `--hmi-build`, an indirect tag takes only `name`, `type` and
`comment`:

```json
"tags": [{"name": "FP_Status", "type": "IndirectAnalog", "comment": "Faceplate: valve status"}]
```

See [HMI_AUTOMATION.md](HMI_AUTOMATION.md).
