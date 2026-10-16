# Tag references: Find/Replace, and what counts as one

Append HMI Studio keeps tag names in more places than an animation's main
expression: in band limits, in range bounds, in trend pens, in Recipe List
tags, in action and window scripts, and in the tag names handed as text to
`LinkIndirectTag` and `TrendSetPen`. **HMI > Find/Replace Tag References...**
re-points all of them at once, and the same map is what makes a Tag Dictionary
rename complete.

## HMI > Find/Replace Tag References...

The dialog lists every tag named by what is in scope, each with a field for the
name to use instead.

- **Scope** is decided when the dialog opens, and the first line says which it
  is:
  - nothing selected: every object on the current window (page);
  - something selected: those objects and the objects inside them.
- **Uses** counts how many fields name that tag within the scope.
- **Replace with** is the new name. An empty field leaves that tag alone.
- **Status** says what will happen: `ready`, `new: double-click to create` for
  a name that is not in the dictionary yet, or why the name cannot be used.

System tags (`_AlarmsActive`, `_AckAll`, `_Username`, …) are not listed: they
are supplied by the runtime and cannot be replaced.

### Replacing text in the names

Fill in **Find** and **Replace with** at the top and press **Fill
replacements** to rewrite that text in every listed name — `Pump1_` to
`Pump2_`, for example. Only names containing the find text are filled, so a
name typed by hand is never overwritten. **Match case** makes the search
case-sensitive; without it the search ignores case and the replacement is
inserted exactly as typed. **Clear replacements** empties the column.

### Validate

**Validate** checks every replacement, then dry-runs the whole set and reports
only problems the project does not already have, so an existing problem
elsewhere does not bury the new ones. It reports:

- a name that is not a valid tag name, is a system tag name, or is not in the
  dictionary;
- an expression or script that would no longer compile;
- a field that cannot be read (an unterminated comment or string), which is
  left exactly as it was;
- a replacement that is a `DIM` local variable in a script it appears in,
  because the reference would then read the variable and not the tag.

### Apply

**Apply** makes every replacement in one step, which one Undo reverses however
many objects it touched. The whole set is applied at once, so two tags can
trade names: `A` to `B` and `B` to `A` does what it says.

Apply refuses while any replacement is invalid, and says how many. Afterwards
the dialog stays open and the list is rebuilt from the same scope, so the new
names are shown with their counts and the replacement fields are empty.

### Creating a tag from the dialog

Double-click a name to open a single-tag editor on it:

- double-clicking in the **Tag** column opens the tag that is referenced, with
  its properties filled in;
- double-clicking a **Replace with** field opens the name typed there, and
  offers to create it when the dictionary does not have it. The type is
  guessed from the way the page uses the name; change it if the guess is
  wrong.

The editor holds the same properties as the Tag Dictionary, for one tag:

- **Save** writes the tag into the project. It is disabled until something
  changes (a tag being created counts as changed).
- **Close** asks whether to save first, when there is anything to save.
- **Discard & Close** throws the edit away.
- A tag's name can only be changed while it is being created. Renaming an
  existing tag belongs in the Tag Dictionary, which moves every reference with
  it.

## What counts as a tag reference

The map is built from the link registry, so each link declares which of its
fields name a tag:

| Role | Fields |
|---|---|
| A bare tag name | `tag` (User Input, Pushbutton, the alarm color links, Sliders), `selectedTag`, `upTag`, `downTag` (Recipe List), a trend's `pens[].tag` |
| An expression | `expr`, `enableExpr`, `min`, `max`, `rateMs`, a color link's `bands[].max`, `atMin`, `atMax`, `offsetMin`, `offsetMax`, `pctMin`, `pctMax`, `angleMin`, `angleMax`, `travelMin`, `travelMax`, `everyMs` |
| A script | Pushbutton Action's `onDown`, `whileDown`, `onUp`; a window's On show, While showing and On hide; the application scripts |

Free text is never searched for tag names: a Value Display's prefix, suffix,
format, On/Off text, a User Input's prompt, a window name, a recipe book name
and a trend's own name and title are left exactly as they are.

In an expression or a script the names are taken from the language's own
lexer, not matched as text, so:

- a dotfield moves with its tag (`Tank_Level.MaxEU` becomes
  `Vessel_Level.MaxEU`) while `Other.Tank_Level` is left alone;
- `InTouch:Tank_Level` keeps its qualifier;
- a name inside a brace comment `{ … }` or inside a string is not touched,
  except for the tag-name arguments of `LinkIndirectTag(indirect, tag)` and
  `TrendSetPen(trend, tag)` — `TrendSetPen`'s first argument is a trend's
  name, so it stays;
- a function's name is not a reference, so a tag called `Abs` survives
  `Abs(x)`;
- a `DIM` local variable is never rewritten, and a script that declares a
  local with the name being replaced is left alone.

A field whose text cannot be read at all keeps its old name and is reported
rather than guessed at.

## Renaming a tag

HMI > Tag Dictionary's **Name** field still renames a tag, and now carries
every reference in the **whole project** with it: every page's objects, the
window scripts, the application scripts, recipe book items and the tag names
inside saved recipes. The dialog reports what it changed.

Only the current page's objects are part of the drawing, so only those come
back with one Undo. The rest is the project's own state: the note says so when
it has changed something outside the current page.

## Also fixed by the shared map

- **HMI > Validate Expressions** checks the fields it used to skip: the range
  bounds (`atMin`, `pctMax`, `angleMin`, `travelMax`, …), `everyMs`, and the
  Pushbutton Action scripts.
- **At Run**, a tag used only as a range bound is now subscribed, so a bar
  graph whose maximum is `Tank_Level.MaxEU` or a tag follows live data instead
  of whatever the driver happened to hold.
- **Deleting a tag** counts references across the whole project before asking.
