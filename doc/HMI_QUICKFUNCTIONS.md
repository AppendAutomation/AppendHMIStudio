# QuickFunctions

A QuickFunction is a piece of script logic with a name, stored in the project
and called like any other function. It takes typed arguments and gives a value
back with `Return`, so a calculation or a sequence written once can be used
from every window, button and animation link that needs it.

Manage them in **HMI > QuickFunctions...**.

## Defining one

Each function has:

- a **name**, which is what a script or an expression calls. It follows the
  rules a tag name does (a letter or `_`, then letters, digits and `_`), and it
  may not be the name of a tag, a system tag, a built-in function or another
  QuickFunction;
- **arguments**, up to eight, each with a name and a type — **Analog**,
  **Discrete** or **Message**. An argument may not take a tag's name, for the
  same reason a `DIM` local may not: inside the body a name has to mean one
  thing;
- a **script body**, the statements that run when it is called.

The dialog shows each function's signature and whether it can be used in
**scripts and expressions** or in **scripts only** — see *Where a function can
be called* below.

## The body

The body is a script, so it can do what any script can: assign tags, `IF …
THEN … ELSE … ENDIF`, declare local variables with `DIM`, and call built-in
and other QuickFunctions.

The arguments are read like variables:

```
{ Scaled(Raw, Span): the raw count as a percentage of Span }
IF Span == 0 THEN
	Return 0;
ENDIF;

Return Raw * 100 / Span;
```

An argument can be written to as well, as a scratch variable; the value is
coerced to the argument's type, exactly as a `DIM` local is.

### Return

`Return <expression>;` gives the value back and stops the function there,
wherever it appears — including inside an `IF`. `Return;` on its own stops it
with the default value.

The value can be anything an expression can produce: a literal, a tag's value,
an argument, a calculation. It carries its quality and its time with it, so a
function that returns a tag read from a dead device returns bad quality and a
Value Display shows `####`, just as the tag itself would.

A function that never reaches a `Return` gives **0**, with good quality.
Validate says so, so a function that was meant to return something does not
quietly give zero for ever.

Because a body can branch, the quality of what comes back is the quality of
the branch that actually ran. The tags a function reads are still all
subscribed, whichever branch runs, so an animation using it repaints whenever
any of them changes.

`Return` is a keyword only where a statement begins and a value follows, so a
project with a tag called `Return` keeps working: `Return = 1;` still assigns
that tag, and `x = Return + 1;` still reads it.

### While you are writing one

Three buttons sit under the body:

- **Validate** checks the script there and then, and writes what it finds —
  or `No problems found.` — under the buttons. The field also marks itself as
  you type, as every script field does; the button is for asking, and for
  clearing the message once you have dealt with it.
- **New Tag...** opens the single-tag editor on a name nothing has yet, so a
  tag the script needs can be made without leaving it. Saving one re-checks the
  script, so an `unknown tag` complaint clears as soon as the tag exists.
- **Browse Tags...** lists the project's tags with a filter — on the name or
  the comment — and copies the one you pick to the clipboard, ready to paste
  into the body. Double-clicking a row picks it. If the clipboard cannot be
  reached, the message names the tag anyway.

## Calling one

From a script:

```
Tank_Percent = Scaled(Tank_Raw, 4000);
```

From an animation link's expression — a Value Display, a fill percentage, a
color band, a visibility rule:

```
Scaled(Tank_Raw, 4000)
```

The name is matched whatever its case, like the built-ins. Arguments are
passed by value and coerced to the declared type.

### Where a function can be called

A QuickFunction that **writes a tag**, or calls an **action function**
(`RecipeSave`, `ShowLogin`, `LinkIndirectTag`, `ScreenToPDF`, `TrendPause`,
`ShowWindow` …), or calls another QuickFunction that does, can be used **in
scripts only** — exactly like those functions themselves. An animation link
runs on every repaint, and acting on every repaint is not something an
animation should do. Validate and the editor say so where you try it.

Everything else can be used in scripts **and** in expressions.

### What a body may not do

- **The calls that pause.** `ShowRecipeSelect`, and `ShowWindow` with a `wait`
  argument, stop a script until the operator answers. A function cannot be
  paused, so these are refused in a body. Call them from the script that calls
  the function:

  ```
  IF SetUpFaceplate("P1") THEN
  	ShowWindow("Faceplate", 100, 200, 1, 1);
  ENDIF;
  ```

  A plain `ShowWindow("Faceplate")`, which opens the window and carries on, is
  allowed in a body.
- **Recursion.** A function may call other QuickFunctions, but not itself,
  directly or through a chain of others. Validate reports the whole cycle, and
  a call to a function in one is an error where it is written. Nesting is
  capped at 16 deep at run time as a backstop.

## Cost

A QuickFunction called from an animation expression runs every time that
object repaints, which is every time any tag it depends on changes. Keep the
body short, and put expensive work in a window or application script that
writes a tag the link reads instead.

## In the project spec

QuickFunctions are a top-level section of the JSON spec used by
`--hmi-build` (see [HMI_AUTOMATION.md](HMI_AUTOMATION.md)):

```json
"quickFunctions": [
  {
    "name": "Scaled",
    "args": [{"name": "Raw", "type": "Analog"}, {"name": "Span", "type": "Analog"}],
    "body": "IF Span == 0 THEN\n\tReturn 0;\nENDIF;\nReturn Raw * 100 / Span;"
  }
]
```

An argument given as a plain string (`"args": ["Raw"]`) is an Analog argument.
`--hmi-dump` writes the section back in full, so a dump and a rebuild give the
same project.

## What Validate reports

- a name that cannot be used: bad characters, a keyword, a type name, a system
  tag, a tag's name, a built-in's name, or two functions with the same name;
- the same for an argument, plus two arguments with one name;
- anything wrong with the body, as it would in any other script, with the
  arguments in scope;
- a function that is part of a recursive cycle;
- a function with no `Return` at all, which always gives 0.

## Renaming tags

A QuickFunction body is a tag reference site like any script: **Edit >
Find/Replace Tag References** and the Tag Dictionary's rename reach into it,
and the delete count includes it. Argument names are left alone, even when a
tag of the same name exists — in the body they are the argument, not the tag.
