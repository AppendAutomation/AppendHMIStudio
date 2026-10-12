# Trends

A **Trend** plots tag values over time. Drop one from the HMI palette onto a window, give it pens, and at Run it shows the last few minutes, redrawn as values change. Scripts move it: they pick the pen whose scale is shown, pause it, scroll back and forward through the history, and return to live. The trend has no buttons of its own, so the screen's own buttons and scripts decide how operators use it.

## The Trend object

| Setting | Where | Default | Meaning |
|---|---|---|---|
| Name | Animation tab | `Trend1`, `Trend2`, … | The name scripts use. Unique in the project; a pasted or duplicated trend gets the next free name |
| Title | Animation tab | none | A heading above the plot |
| Time span (seconds) | Animation tab | 600 | The time shown, 10 s to 24 h |
| Sample period (ms) | Animation tab | 1000 | How often the pens are sampled, at least 100 ms |
| Refresh period (ms) | Animation tab | 1000 | How often the picture is redrawn while live, at least 100 ms |
| Grid color | Animation tab | light gray | The grid lines and the plot's frame |
| Pens | Animation tab | none | Up to 8: tag, color, Min and Max |
| Fill | Style tab | white | The background |
| Line | Style tab | blue gray | The border |
| Line width | Style tab | 1 | Every pen's line; the selected pen is drawn 1 px thicker |
| Font, size, color | Text tab | Helvetica 11, dark gray | The axis labels and title |

**Pens** are analog or discrete tags (not message or indirect tags). Each pen is drawn against its own range:

- **Min and Max** are the values at the bottom and top of the plot. Left empty, an analog pen uses the tag's Min EU and Max EU.
- **A discrete pen** is drawn as steps between 0 and 1. With no range of its own it uses -0.5 to 1.5, which puts its line in the middle half. Give each discrete pen a different range to stack them: -4 to 2 puts 0 and 1 in the top third, and 0 to 6 puts them in the bottom third.

At Run the y axis shows the **selected pen**: its range on the scale, and its name and engineering units, in its color, beside the axis. The first pen is selected when the Run starts. The time axis is along the bottom, on whole seconds, minutes or hours of the clock. A gap in a line means there was no good value: bad quality, or the application wasn't running.

## Script functions

All five are for scripts only. Each takes the trend's name as text: a literal in quotes, or a message tag holding the name. Each returns 1, or 0 with the reason shown to the operator in the function error window.

| Function | Effect |
|---|---|
| `TrendSetPen(trend, tag)` | Selects the pen for `tag` (text: a literal or a message tag): the y axis shows its range and name, and its line is 1 px thicker |
| `TrendPause(trend)` | Freezes the picture. Recording goes on |
| `TrendPlay(trend)` | Back to live |
| `TrendScrollBack(trend)` | Moves the start and end times back by half the time span. A live trend pauses first |
| `TrendScrollForward(trend)` | Moves them forward by half the time span, stopping at now. The trend stays paused until `TrendPlay` |

```
TrendSetPen("LevelTrend", "LIT100");
TrendSetPen(TrendName, PenTag);
TrendScrollBack("LevelTrend");
```

A pen's selection and a trend's paused or scrolled position belong to its name for the whole Run. They apply in every window that shows the trend, and survive the window closing and opening again. A new Run starts live with the first pen selected.

A paused trend shows "Paused" and the date and times it covers in its top right corner.

Validate checks quoted names: a trend that no page has, a pen the trend doesn't have, and a bare non-message tag where a name belongs (`TrendSetPen("T1", LIT100)` instead of `TrendSetPen("T1", "LIT100")`).

## Recording and history

Every pen of every trend is recorded from the moment the Run starts, whether or not a window shows the trend, so a trend opened later already has its data.

- **Sampling:** each tag at the shortest sample period among the trends that have it.
- **What is kept:** a value is recorded when it changes, and once a minute while it doesn't. Before a change, the last unchanged sample is kept too, so a line stays flat up to the moment it moved.
- **Memory:** the last 24 hours.
- **Disk:** the history is written every 5 seconds to one CSV file per day, `trends-YYYY-MM-DD.csv`, beside the alarm history:
  - Append HMI Studio's Run: `%APPDATA%\Append HMI Studio\trends\<project>\` (`~/.config/Append HMI Studio/trends/<project>/` on Linux);
  - a published package or Append HMI Desktop: `%APPDATA%\<Product>\trends\<Product>\`.

  The columns are `Time` (milliseconds since 1970, UTC), `Tag` and `Value` (empty for a gap).
- **Limits:** files are kept 7 days, and at most 500 MB per application. The oldest day goes first; today's file is never removed.
- **After a restart** a trend shows what was recorded before it, with a gap where the application wasn't running.
- **Scrolling back** past what memory holds reads the files. Long ranges are thinned to about 4000 points per pen, keeping each stretch's first, lowest, highest and last values and its gaps, so peaks still show.

Append HMI Web keeps one history on the server. Every browser runs the application and samples its trends, but one browser at a time writes the history. When it closes or goes quiet for 15 seconds, another takes over.

The self tests and the command-line tools record nothing to disk.

## Building trends from the command line

In the JSON spec (see [HMI_AUTOMATION.md](HMI_AUTOMATION.md)), a trend is an object of type `trend` with a `trend` link:

```json
{"type": "trend", "x": 40, "y": 40, "width": 900, "height": 400, "style": "fontSize=14;",
 "links": {"trend": {"name": "LevelTrend", "title": "Tank levels", "spanSec": 600,
   "pens": [{"tag": "LIT100", "color": "#1f77b4"},
            {"tag": "Pump1_Running", "min": "-4", "max": "2", "color": "#2ca02c"}]}}}
```

A trend given no name gets the next free `TrendN`.
