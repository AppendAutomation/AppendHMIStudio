# Windows

Every page of the drawing is a **window** of the application, named by its page tab. **HMI > Window Properties** says how that page is displayed when it opens: its type, its place and size on the screen, whether it has a title bar, what that bar looks like, whether the operator may move it, and the scripts that run while it is open.

## Window types

| Type | Behavior | Used for |
|---|---|---|
| Replace | Closes any window it overlaps when it opens | Screens, one per view of the plant |
| Overlay | Opens on top of other windows and leaves them open | A header, a navigation bar, anything shared by every screen |
| Popup (modal) | Stays on top of every other window, and nothing beneath it can be touched until it closes | Faceplates, confirmations, keypads |

Several windows are open at once. A screen built as a Replace window over the middle of the display, with Overlay windows above and below it, redraws only the middle when the operator changes screen.

## Position and size

A window is a rectangle on the screen, in screen pixels: **Left**, **Top**, **Width** and **Height**, with **Full Screen**, **Fit to Content** and **Center** to set them. The preview under the fields shows where it falls on the screen.

A window shows **the part of its page inside that rectangle** — the dashed border on the canvas. Objects for a popup are therefore drawn at the popup's position on its page, not at the page's origin, and moving a window does not move what is drawn in it.

`ShowWindow(name, left, top)` opens a window somewhere else for one showing. It still shows the same objects: only the rectangle moves, not the part of the page it is over.

## The title bar

**Title bar** draws a bar across the top of the window with the window's name and a close button. The bar is **part of the window's height**, as it is on a device, so a 300 px window with a 40 px bar leaves 260 px of the page below it.

| Setting | Default | Meaning |
|---|---|---|
| Height (px) | 24 | The bar's height, 8 to 400, and never taller than the window |
| Font | built-in | The font family of the title |
| Font size (px) | 13 | The title's size, 6 to 72, scaled with the screen like everything else |
| Text color | white | The title and the close button |
| Background | dark blue | The bar itself |
| Movable | off | The operator may move the window — popups only (below) |

An empty font or color keeps the built-in one, so a window built before these settings existed looks exactly as it did, and setting one of them changes only that one.

## Moving a window at Run

Turn **Movable** on, and at Run the operator moves that window by dragging its title bar — with a mouse, or a finger on a touch panel. It is offered for **Popup** windows: the popup is the window that lands over the plant the operator is watching, and the one worth sliding aside. A Replace window's overlap rule and an Overlay's place in the stack belong to the design, so those stay where they are put.

What moving does, and does not do:

- **The window moves; its contents do not.** It keeps showing the same part of its page, exactly as a `ShowWindow` with coordinates does.
- **A window may hang off an edge**, but not out of reach: at least 48 px of its width stays on the screen, and the title bar never leaves it.
- **Nothing is remembered.** Close the window and open it again and it is back where it is defined, or where the `ShowWindow` call puts it. A window re-shown while it is still open stays where the operator left it, unless the call gives coordinates.
- **The project file is not changed.** Where the operator drags a window at Run is not a design change.

A window with no title bar cannot be moved, since the bar is what the operator grabs.

## Scripts

| Script | When it runs |
|---|---|
| On show | Once, as the window opens, before it is drawn |
| While showing | Every **Every (ms)** (1000 by default, at least 50) while the window is open |
| On hide | Once, as the window closes, while it can still write tags |

Window scripts run in the window's own runtime, so they read the values its objects show and write through the same driver. Application scripts (**HMI > Application Settings**) cover what must run whether or not a window is open.

## The JSON spec

`--hmi-build` takes the same properties under a page's `window` (see [HMI_AUTOMATION.md](HMI_AUTOMATION.md)):

```json
{
  "name": "Device Faceplate",
  "window": {
    "type": "popup",
    "titleBar": true,
    "movable": true,
    "titleHeight": 40,
    "titleFont": "Verdana",
    "titleFontSize": 18,
    "titleFill": "#1b5e20",
    "titleColor": "#ffffff",
    "x": 730, "y": 150, "width": 460, "height": 560,
    "onShow": "FP_Level = 0;",
    "whileShowing": "FP_Level = Tank_Level;",
    "everyMs": "250",
    "onHide": "FP_Prefix = \"\";"
  }
}
```

Only what differs from the default is written to the project file, so a window nobody has configured stays out of it.

## Validate

**HMI > Validate** reports a window that extends past the screen, a title bar taller than its window, a height or font size out of range, and **Movable** on a window that is not a popup or has no title bar.
