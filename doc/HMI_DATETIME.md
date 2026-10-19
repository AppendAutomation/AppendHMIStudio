# The Date and Time

Every application can read this PC's clock. There is nothing to configure: the date and time **system tags** are always there, the `Now()` function formats the time any way a screen or a report needs, and `_TimeFormat` chooses the format `_Now` is shown in.

The clock is plant-local throughout: a row a script stamps and reads back reports the same wall clock.

## The system tags

| Tag | Type | Reads |
|---|---|---|
| `_Year` | integer | The year, all four digits |
| `_Month` | integer | The month, 1 to 12 |
| `_Day` | integer | The day of the month, 1 to 31 |
| `_Hour` | integer | The hour, 0 to 23 |
| `_Minute` | integer | The minute, 0 to 59 |
| `_Second` | integer | The second, 0 to 59 |
| `_Msec` | integer | The millisecond within the second, 0 to 999 |
| `_DateString` | message | The date, as `2026-10-18` |
| `_TimeString` | message | The time of day, as `14:05:09` |
| `_DateTime` | message | Both, as `2026-10-18 14:05:09.270` — the text a database date or date-and-time column takes as it stands |
| `_Now` | message | The date and time in the format `_TimeFormat` holds |
| `_TimeFormat` | message | That format. `HH:mm:ss` at the start of every Run |

They work anywhere a tag does: a Value Display, a Visibility or color expression, a window script, an application script, a database insert. None of them is written by the PLC and none is held anywhere — each value is worked out as it is read, so a script reading `_Msec` gets the millisecond it ran in. An object showing one is repainted once a second.

Every one of them is read-only except **`_TimeFormat`**, which a script sets:

```
_TimeFormat = "yyyy-MM-dd HH:mm:ss";
```

The names are reserved: no tag in the dictionary may be called `_Now`, and the Tag Dictionary lists them all (marked **System**) so the names and what they read are written down where they are needed. They cannot be edited or deleted there.

## Now()

```
Now()            the time, as HH:mm:ss
Now(format)      the date and time in a format of your own
```

`Now()` is an ordinary function, not a script action, so it can be used in an animation link as readily as in a script — a Value Display showing `Now("US")` needs no tag at all, and it is repainted once a second like any other object showing the clock.

```
Header_Text = "Report run " + Now("DATETIME");
```

## Formats

A format is written as a **pattern** of tokens, or by the **name** of one of the popular formats. Names are matched without regard to case.

### Named formats

| Name | Pattern | 18 October 2026 at 14:05:09.270 |
|---|---|---|
| `TIME` | `HH:mm:ss` | 14:05:09 |
| `TIME12` | `hh:mm:ss tt` | 02:05:09 PM |
| `HHMM` | `HH:mm` | 14:05 |
| `DATE` | `yyyy-MM-dd` | 2026-10-18 |
| `US` | `MM/dd/yyyy` | 10/18/2026 |
| `EU` | `dd/MM/yyyy` | 18/10/2026 |
| `DATETIME` | `yyyy-MM-dd HH:mm:ss` | 2026-10-18 14:05:09 |
| `STAMP` | `yyyy-MM-dd HH:mm:ss.fff` | 2026-10-18 14:05:09.270 |
| `ISO` | `yyyy-MM-ddTHH:mm:ss` | 2026-10-18T14:05:09 |
| `LONG` | `dddd, MMMM d, yyyy` | Sunday, October 18, 2026 |

`STAMP` is exactly what `_DateTime` reads, which is the text a database date column takes — use it when a script builds its own stamp.

### Pattern tokens

| Token | Reads | Example |
|---|---|---|
| `yyyy` | Year, four digits | 2026 |
| `yy` | Year, two digits | 26 |
| `MMMM` | Month name | October |
| `MMM` | Month name, short | Oct |
| `MM` | Month, 01 to 12 | 10 |
| `M` | Month, 1 to 12 | 10 |
| `dddd` | Day name | Sunday |
| `ddd` | Day name, short | Sun |
| `dd` | Day of month, 01 to 31 | 18 |
| `d` | Day of month, 1 to 31 | 18 |
| `HH` | Hour, 00 to 23 | 14 |
| `H` | Hour, 0 to 23 | 14 |
| `hh` | Hour, 01 to 12 | 02 |
| `h` | Hour, 1 to 12 | 2 |
| `mm` | Minute, 00 to 59 | 05 |
| `m` | Minute, 0 to 59 | 5 |
| `ss` | Second, 00 to 59 | 09 |
| `s` | Second, 0 to 59 | 9 |
| `fff` | Millisecond, 000 to 999 | 270 |
| `tt` | AM or PM | PM |
| `t` | A or P | P |

Case matters: `MM` is the month and `mm` the minute, `HH` the 24-hour clock and `hh` the 12-hour one.

Separators, digits and punctuation are kept as they are, so `dd.MM.yyyy`, `MM/dd/yyyy` and `yyyyMMdd_HHmmss` all work. **Letters, though, have to be part of a token**: a pattern is a pattern, not a sentence, and `Batch report` is refused rather than coming out as `BaPc2 reporP`. Where a letter has to be taken through as itself, put a backslash in front of it — `\a\t HH:mm` gives `at 14:05` — but joining strings reads better:

```
Header_Text = "Shift started at " + Now("HH:mm");
```

Month and day names are in English.

### When a format means nothing

A format has to be a known name, or a pattern: at least one token, and no letter that is not part of one. `"Batch report"` is not a pattern, and neither is an empty string or anything longer than 64 characters. Whatever is not a format gives **`HH:mm:ss`** — the time still shows, rather than the object going blank:

```
Now("Batch report")        14:05:09
_TimeFormat = "nope";      _Now reads 14:05:09
```

**Validate** reports a format written out in full that means nothing, so a typo is found at design time rather than on the plant floor. A format built from tag values is not checked, since its value is not known until the Run.

## What `_TimeFormat` belongs to

`_TimeFormat` belongs to the **Run**, not to a window and not to the project file:

- every Run starts with `HH:mm:ss`;
- a script sets it, and every window of the Run follows at once — including windows that are not open yet;
- nothing is kept when the Run stops.

Setting it in the **On start** application script is the usual way to choose an application's own clock format:

```
_TimeFormat = "US";
```

An operator can be given the choice as well: a **User Input** link on `_TimeFormat` (a string input) lets them type one, or a few pushbuttons can each set a format with an Action Script.

## Examples

A header that shows the date and the time, with the date in American order:

```
_TimeFormat = "MM/dd/yyyy HH:mm:ss";     On start
```

and a Value Display on `_Now`.

A report line, built in a script:

```
DIM Line AS Message;
Line = "Batch " + Batch_Number + " finished " + Now("LONG") + " at " + Now("TIME12");
```

A row stamped into a database table, where the column is a date and time:

```
SQLSetParamDateTime(1, 1, _DateTime);
```

A shift that is known from the hour alone:

```
IF _Hour >= 7 AND _Hour < 15 THEN Shift = 1; ENDIF;
IF _Hour >= 15 AND _Hour < 23 THEN Shift = 2; ENDIF;
```

## See also

- [HMI_AUTOMATION.md](HMI_AUTOMATION.md) — the system tag names in the command-line reference, and the reserved-name rule
- [HMI_SQL.md](HMI_SQL.md) — stamping a row, and the date and time parameters
- [HMI_ALARMS.md](HMI_ALARMS.md) and [HMI_SECURITY.md](HMI_SECURITY.md) — the other system tags
