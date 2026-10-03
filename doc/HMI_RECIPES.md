# Recipes

A recipe is a named set of tag values that an operator saves and loads again
later, for example the setpoints of a product. Recipes are grouped in
**recipe books**:

- **Save/Load tags:** each book lists the tags its recipes hold, usually memory
  tags the operator edits on screen.
- **Upload/Download tags:** optionally, a book pairs each of them with another
  tag, usually the PLC tag the value is sent to.

## Defining recipe books

**HMI > Recipes** lists the project's books.

- **Add**, **Delete:** the buttons under the book list.
- **Name:** 1 to 64 characters, case-insensitive.
- **Tags:** **Add tag** adds a row. Each row has a Save/Load tag and, with
  **Upload/Download tags** ticked, its paired tag. ▲ and ▼ reorder the rows;
  **Remove** deletes one. A tag that is not in the dictionary is marked.
- **Starting recipes:** recipes kept in the project, used on a PC until it
  saves its own recipes for that book. **Remove** deletes one. They come from
  an imported JSON file or a command-line spec.
- **Import...**, **Export...:** a JSON file with books and their starting
  recipes, `{"version": 1, "recipeBooks": [...]}` in the same form as the
  command-line spec below. An imported book with an existing name replaces it,
  after a question.
- **OK** checks every book and saves them into the project.

**Validate** reports books with tags that no longer exist, rows without a
Save/Load tag and tags listed twice.

## Script functions

These work only in scripts (Action Scripts and window scripts), not in
animation expressions. `book` and `name` are text: quoted literals, message
tags or any text expression.

| Function | What it does |
|---|---|
| `RecipeSave(book, name)` | Saves the current values of the book's Save/Load tags as recipe `name`, replacing one with that name. Refused if any value has bad quality |
| `RecipeLoad(book, name)` | Writes the recipe's values to the Save/Load tags, converted to each tag's type |
| `RecipeDownload(book, name)` | Copies each Save/Load tag's value to its Upload/Download tag (`name` is not used) |
| `RecipeUpload(book, name)` | Copies each Upload/Download tag's value back to its Save/Load tag (`name` is not used) |
| `RecipeExport(book, name)` | Writes the recipe to a CSV file chosen by the operator |
| `RecipeImport(book, name)` | Takes a recipe CSV file chosen by the operator and saves it as `name` |
| `RecipeDelete(book, name[, confirm])` | Deletes the recipe; with a true `confirm`, asks the operator first |
| `RecipeRename(book, name, newName)` | Renames the recipe; refused if `newName` already exists |
| `x = ShowRecipeSelect(book[, x, y, w, h])` | Opens a window listing the book's recipes and returns the chosen name, or `""` for Cancel |

All except ShowRecipeSelect return 1 on success and 0 on failure.
RecipeExport and RecipeImport return 1 once the file window opens.

A typical pair of buttons:

```
RecipeName = ShowRecipeSelect("Plasticizers");
IF RecipeName <> "" THEN
    RecipeLoad("Plasticizers", RecipeName);
ENDIF;
```

```
RecipeSave("Plasticizers", RecipeName);
RecipeDownload("Plasticizers", RecipeName);
```

### ShowRecipeSelect

- **The window:** the book's recipes, sorted, with ▲ and ▼ buttons for touch
  screens, **Select** and **Cancel**. Double-click or Enter selects; Escape or
  the close box cancels.
- **Position:** without `x, y, w, h` it is centered. With them, they are the
  window's outer box in screen pixels, scaled with the running screen.
- **The script waits:** it continues after the operator chooses. So
  ShowRecipeSelect must be a statement on its own or the whole right-hand side
  of an assignment; `IF ShowRecipeSelect("B") <> "" THEN` or
  `x = ShowRecipeSelect("B") + "!"` is a validation error.
- If the Run stops while the window is open, the rest of the script does not
  run.

### Recipe CSV files

```
#Recipe,Plasticizers,High Plast 1
Tag,Value
Edit_TotalVol,12
Edit_Pct0,80
```

- **Values:** numbers, or quoted text for message tags (Excel's quoting).
- **Import:** only the book's Save/Load tags are taken; other tags in the file
  are reported and ignored. A file whose first line names another book is
  refused.

## Errors at run time

A recipe function that fails returns 0, writes the reason to the log, and
shows a **Recipe Error** window to the operator. The window names the call,
e.g. `RecipeLoad("Plasticizers", "")`, and the reason, e.g. "The recipe name
is empty.".

- **After the script:** the window opens when the script finishes or waits, so
  the rest of the script still runs.
- **Several failures** go into one window, up to 10, then "and n more".
  Identical failures are counted, not repeated, so a repeating script cannot
  flood the screen.
- **Choices are not errors:** Cancel in ShowRecipeSelect, No when deleting and
  cancelling a file window return 0 or `""` without one.

The reasons include an empty or unknown book or recipe name, a recipe that
already exists (rename), tags with bad quality ("the PLC may not be
connected"), refused tag writes, a book without Upload/Download tags, tags
missing from the project, and files that cannot be written or read or are not
recipes for the book.

## The Recipe List object

**Recipe List** in the HMI palette shows a book's recipes at Run. Its settings
are in the Animation tab:

| Setting | Meaning |
|---|---|
| Recipe book | The book listed |
| Title | The heading; empty for the book's name |
| SelectedRecipe | A message tag: the highlighted recipe follows it, and touching a row writes the row's name to it |
| SelectUp, SelectDown | Discrete tags: each change from 0 to 1 moves the selection up or down. With none selected, Down selects the first recipe and Up the last |
| Up/Down arrows | Draws ▲ and ▼ buttons in the heading that do the same |

Buttons that load or delete the selected recipe use the same tag:
`RecipeLoad("Plasticizers", SelectedRecipe);`.

## Where recipes are kept

The project holds the books and their starting recipes. Recipes saved during a
Run (RecipeSave, RecipeRename, RecipeDelete, RecipeImport) are kept on the PC
that runs the application, book by book: once a PC saves a recipe in a book,
its list replaces that book's starting recipes there.

| Running in | File |
|---|---|
| Append HMI Studio (Run) | `userData/recipes/<project>.json` |
| A published package | `%APPDATA%\<Product>\recipes\<Product>.json` |
| Append HMI Desktop, Append HMI Web | `recipes/<application>.json` in their settings folder |

- **Undo:** in the studio, **HMI > Clear Runtime Recipes** (with the Run
  stopped) forgets the editor's saved recipes. To reset a published PC, delete
  its `recipes` folder.
- **Append HMI Web:** all browsers share the server's recipes. A recipe saved
  in one browser appears in the others within a few seconds; saving merges, so
  no browser overwrites what another saved.
- **Files:** in Append HMI Web, RecipeExport downloads the CSV and RecipeImport
  opens the browser's file picker.

## Command line

In a spec for `--hmi-build`:

```json
"recipeBooks": [
  {"name": "Plasticizers", "uploadDownload": true,
   "items": [{"tag": "Edit_TotalVol", "ioTag": "Rcp_TotalVol"}, {"tag": "Edit_Pct0", "ioTag": "Rcp_Pct0"}],
   "recipes": {"Standard": {"Edit_TotalVol": 10, "Edit_Pct0": 60}}}
]
```

- `items` entries may be plain tag names when the book has no Upload/Download
  tags: `"items": ["Speed", "Temp"]`.
- `ioTag` needs `"uploadDownload": true`.
- Recipe List objects are `{"type": "recipeList", "links": {"recipeList":
  {"book": "Plasticizers", "selectedTag": "RecipeName", "upTag": "", "downTag":
  "", "arrows": true, "title": ""}}}`.

See [HMI_AUTOMATION.md](HMI_AUTOMATION.md).
