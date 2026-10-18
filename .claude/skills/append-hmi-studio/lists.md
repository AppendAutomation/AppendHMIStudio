# List Boxes

A **List Box** shows a list of text items that the operator can pick from. Drop one from the HMI palette onto a window and give it a name; scripts then fill it, empty it, sort it and move its selection by that name, and whatever the operator touches is written to a message tag.

Use one wherever a screen needs a choice that isn't fixed at design time: the batches waiting to run, the lots a query returned, the faults logged since the shift started, the files on a USB stick.

## The List Box object

| Setting | Where | Default | Meaning |
|---|---|---|---|
| Name | Animation tab | `List1`, `List2`, … | The name scripts use. Unique in the project; a pasted or duplicated list gets the next free name |
| Title | Animation tab | the list's name | The heading, when the title bar is shown |
| Show title bar | Animation tab | off | Draws the heading, and holds the arrow buttons |
| Items | Animation tab | none | Starting items, one per line. The list holds these when the Run starts |
| Selected item | Animation tab | none | A message tag the selection is written to |
| Select up / Select down | Animation tab | none | Discrete tags: a rising edge moves the selection one item |
| Up and down arrow buttons | Animation tab | off | Two buttons in the title bar that do the same |
| Title bar background / text | Animation tab | dark gray on white | The heading's colors |
| Selected row background / text | Animation tab | blue on white | The selected item's colors |
| Fill | Style tab | white | The background behind the rows |
| Line, Line width | Style tab | blue gray, 1 | The border |
| Font, size, color | Text tab | Helvetica 14, dark gray | The rows |

A color left unset keeps the built-in look, so an existing screen is unchanged when you set only one of them.

The rows scroll when there are more than fit. The selected row is always brought back into view — after a sort, after an item above it is removed, and when a script selects something off screen.

## What the operator does

Touching a row selects it. The selection is written to the **Selected item** tag at once, so the rest of the screen — a value display, a button's Enable when, a script — sees the choice immediately.

**Select up** and **Select down** move the selection one item on a rising edge, which lets a pair of physical buttons or on-screen pushbuttons drive the list. With nothing selected, Down picks the first item and Up the last. The built-in arrow buttons do the same and need no tags.

## Script functions

All six are for scripts only. Each takes the list's name as text: a literal in quotes, or a message tag holding the name. Each returns 1, or 0 with the reason shown to the operator in the function error window ("List Error").

| Function | Effect |
|---|---|
| `ListClear(list)` | Removes every item and clears the selection |
| `ListAdd(list, item)` | Adds the item to the bottom |
| `ListLoad(list, text)` | Adds the comma-separated items in `text` to the bottom |
| `ListRemove(list, item)` | Removes the item |
| `ListSelect(list, item)` | Selects the item |
| `ListSort(list, order)` | Sorts the items alphabetically: `"ASC"` or `"A"`, `"DESC"` or `"D"` |

```
ListClear("Batches");
ListLoad("Batches", "B-1024, B-1025, B-1026");
ListAdd("Batches", NewBatch);
ListSort("Batches", "ASC");
ListSelect("Batches", NewBatch);
```

**Items are text.** An item is trimmed as it goes in, and an empty one is refused. Duplicates are allowed: the script decides what goes in the list.

**Finding an item ignores case**, so `ListRemove("Flavors", "vanilla")` removes `Vanilla`. `ListRemove` and `ListSelect` **do nothing and still return 1** when the list hasn't got the item — a list the operator has already changed does not throw errors at a script that tidies up after it. `ListRemove` takes the first match when an item appears twice.

**`ListLoad` splits on commas and trims**, dropping empty entries: `" Red , Green ,, Blue, "` loads Red, Green and Blue. There is no quoting, so an item that contains a comma goes in with `ListAdd`.

**Sorting ignores case** (Apple, banana, Cherry), and keeps the selection — the selected item stays selected and the list scrolls so it is still in view.

**Removing the selected item clears the selection**, and so does `ListClear`; the Selected item tag is set to an empty string.

A list holds at most **5000 items**. A call that would take it past that is refused.

Validate checks quoted names: a list that no page has, an empty name, and a bare non-message tag where a name belongs (`ListClear(Batch)` instead of `ListClear("Batch")`). It also reports a sort order that is not ASC, DESC, A or D, and two list boxes with the same name.

## The list belongs to the Run, not to a window

Every list box in the project exists from the moment the Run starts, whether or not a window shows it. So:

- an **On start** application script can fill a list the operator opens later;
- two windows showing the same list name show the same items and the same selection;
- closing a window and opening it again leaves the list as it was;
- the Selected item tag is written even with no window open.

Items are **not** kept between Runs. Each Run starts with the Items box's own lines and whatever the scripts add. To hold a choice across Runs, write the Selected item tag to a retentive memory tag and select it again in an On start script.

## Filling a list from a database

A list box and the `SQL*` functions go together: a query's rows become the items the operator picks from.

```
DIM Res AS Analog;

ListClear("Lots");
Res = SQLSelect(ConnId, "Lots", "LotRow", "Status = 'Open'", "LotId");
Res = SQLFirst(ConnId);

WHILE Res == 0
    ListAdd("Lots", LotId);
    Res = SQLNext(ConnId);
ENDWHILE;

Res = SQLEnd(ConnId);
```

Each move writes the row into the bind list's tags, so `LotId` holds the value of the row the cursor is on.

See [HMI_SQL.md](HMI_SQL.md) for the database side, and [HMI_QUICKFUNCTIONS.md](HMI_QUICKFUNCTIONS.md) for wrapping a sequence like this in a function the screens can call.

## Automation

The `--hmi-build` spec takes a List Box as an object of type `list` with a `list` link:

```json
{
  "id": "flavors",
  "type": "list",
  "x": 20, "y": 70, "width": 260, "height": 320,
  "links": {
    "list": {
      "name": "Flavors",
      "title": "Flavors",
      "titleBar": true,
      "items": "Chocolate\nVanilla\nStrawberry",
      "selectedTag": "Sel_Flavor",
      "upTag": "Step_Up",
      "downTag": "Step_Down",
      "arrows": true,
      "selFill": "#00695c",
      "selColor": "#ffffff"
    }
  }
}
```

See [HMI_AUTOMATION.md](HMI_AUTOMATION.md).
