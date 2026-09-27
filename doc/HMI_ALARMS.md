# Alarms

Append HMI Studio watches every alarmed tag while an application runs, in the
editor's **HMI > Run** and in a published runtime. It does this whether or not
a window shows the tag.

## Configuring alarms

In **HMI > Tag Dictionary**, select a tag:

- **Analog tags** (Integer, Real): set any of **LoLo**, **Low**, **High** and
  **HiHi**. An empty field means no alarm at that limit.
  - **Deadband** stops an alarm chattering. An alarm clears only once the value
    is back inside its limit by the deadband: with High 90 and Deadband 2, a Hi
    alarm clears below 88.
  - Limits are tested from the outside in, so HiHi and LoLo win over Hi and Lo.
- **Discrete tags**: **Alarm when** On (1) or Off (0).
- **Description:** the tag's **Comment** is the alarm's description.

Alarm limits are also columns in the Tag Dictionary's CSV export and import:
`alarmLoLo`, `alarmLow`, `alarmHigh`, `alarmHiHi`, `alarmDeadband` and
`alarmState` (`on`/`off`).

## How alarms behave

- **Entering alarm, or escalating** (Hi to HiHi, Lo to LoLo, or crossing to the
  other side) raises an **Alarm** event. The alarm then needs acknowledging.
- **Easing back** (HiHi to Hi) records a **Change** and keeps an
  acknowledgement already given.
- **Returning to normal** records a **Return**.
- **Acknowledging** records an **Ack**.
- **Listing:** an alarm stays in the active list until it has returned to
  normal *and* been acknowledged (ISA-18.2). Acknowledging an alarm that is
  still active keeps it listed, shown as acknowledged, until it clears.
- **Bad quality:** a tag with bad quality keeps its alarm state until good
  values return.

## System tags and dotfields

| Name | Meaning |
|---|---|
| `_AlarmsActive` | Number of active alarms (read-only) |
| `_AlarmsUnacked` | Number of unacknowledged alarms (read-only) |
| `_AckAll` | Write 1 to acknowledge every alarm, e.g. `_AckAll = 1` in a pushbutton's action script |
| `Tag.InAlarm` | 1 while the tag's alarm is active |
| `Tag.Acked` | 1 unless the tag has an unacknowledged alarm; `Tag.Acked = 1` in a script acknowledges it |

The system tags work anywhere a tag does: Value Display, colors, Visibility
and scripts. These names are reserved, so no dictionary tag can use them.

## Alarm objects

The **HMI** palette in the shape sidebar has two objects:

- **Alarm List**:
  - shows the alarms that are active or not yet acknowledged, newest first;
  - rows are colored by state: unacknowledged red (Hi-Hi/Lo-Lo stronger),
    acknowledged amber, cleared-but-unacknowledged grey;
  - each unacknowledged row has an **Ack** button, and the heading has
    **Ack All** and the counts.
- **Alarm History**:
  - shows alarm events newest first, colored by event;
  - it starts with the events already logged on this PC, then adds new ones.

Select one and open the **Animation** tab to set its title, font size, columns
and, for the history, how many events it shows. In the editor the object shows
a preview; the live table appears at Run.

## History files

- **Format:** events are logged as CSV, one file per day, readable in a
  spreadsheet. Columns: Time, Event, Tag, Description, Condition, Value, Limit.
- **Location:**
  - editor: `~/.config/Append HMI Studio/alarms/<project>/` on Linux, or
    `%APPDATA%\Append HMI Studio\alarms\<project>\` on Windows, where
    `<project>` is the project's file name;
  - published runtime: `%APPDATA%\<Product>\alarms\<Product>\`.
- **Retention:** files older than 90 days are deleted.
- **Timestamps:** times come from the moment the value reached the application,
  not from the PLC's clock.
