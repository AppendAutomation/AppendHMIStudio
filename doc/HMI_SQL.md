# Databases

An HMI application can read and write SQL databases from its scripts: log a batch as it finishes, fetch the last ten into a screen, keep a shift report, look a part number up. Two kinds of database are supported — **Microsoft SQL Server** and **SQLite**, a single file that needs no server at all.

Four things are set up in the project, in HMI > Database, and the `SQL*` script functions work through them:

- **data sources** — the databases the application can open;
- **bind lists** — pairs of a tag and a database column, which is how rows become tag values and tag values become rows;
- **table templates** — the shape of a table the application creates for itself;
- **queries** — statements kept in the project by name.

Nothing about a database touches the screens: there is no results grid object. A script fetches rows into tags, and the tags are shown by the animation links the rest of the application uses.

## Data sources

| Field | Applies to | Meaning |
|---|---|---|
| Name | both | What `SQLConnect` names. Up to 64 characters |
| Kind | both | Microsoft SQL Server, or a SQLite file |
| Server, Port, Database, User | SQL Server | The port defaults to 1433 |
| Password | SQL Server | Optional. See below |
| Instance name | SQL Server | A named instance, instead of a port |
| Encrypt the connection | SQL Server | Off by default |
| Accept any certificate | SQL Server | On by default, which is what a plant server with a self-signed certificate needs |
| Allow older TLS versions | SQL Server | For a server that offers only a TLS version this computer refuses by default |
| Timeout (ms) | both | How long a statement may take, from 100 to 600,000. 15,000 by default |
| File name | SQLite | A file name, never a path: `batches.db` |
| Kept in | SQLite | Application data folder (the default) or Documents |

**Test Connection** in the dialog opens the data source and closes it again, and says what went wrong if it could not. A connection that fails over the protocol — an older server and a current PC disagreeing about TLS — says so in those words rather than passing on an encryption library's message; turning encryption off, or allowing older TLS versions, is the usual answer.

A SQLite data source names a **file and a folder, not a path**, because an absolute path names one machine's account and a published application runs on another PC, often as another user. The file is created the first time something writes to it, and it may only be in one of the two folders offered:

- the application data folder: `%APPDATA%\Append HMI Studio\databases\` (`~/.config/Append HMI Studio/databases/` on Linux), or `%APPDATA%\<Product>\databases\` for a published application;
- the user's Documents folder, which is where a file somebody else will open — a spreadsheet, a report tool — belongs.

A connect string may be given to `SQLConnect` instead of a data source name, for a project brought over from elsewhere: `"Data Source=SERVER;Initial Catalog=Batches;User ID=sa;Password=…"`, or `"File=batches.db"` for SQLite. Only what is needed to reach these two kinds of database is understood and the rest is ignored, so a data source in the project is the better way.

### Passwords

A password typed into the dialog is **obfuscated** in the project file, which keeps it out of plain sight and no more: anyone with the project file and this product can read it back. It is not encryption and the documentation calls it nothing better.

So a site that would rather the password did not travel with the project can leave it empty and give **the computer that runs the application its own**, in the data source's "On this computer" section. That is kept in `userData/datasources/<project>.json`, outside the project, and wins over whatever the project holds. A window may set one and may ask whether one is set; it can never read one back, because nothing there is ever sent to a window. The same arrangement serves the users and the recipes, so one installer runs on every PC and each PC keeps its own secrets.

HMI > Clear Database Passwords forgets what this computer holds.

## Bind lists

A bind list is a list of items, each pairing a tag with a column:

| Column | Meaning |
|---|---|
| Tag | A tag from the dictionary |
| Dotfield | Optional: read something other than the value, such as `TimeDate` or `InAlarm`. A dotfield is read-only, so such an item is filled on an insert and skipped on a fetch |
| Column | The database column |
| Delimiter | Kept for projects brought over from elsewhere and changes nothing |

The order of the items is the order of the columns. `SQLSelect` writes the columns into the tags; `SQLInsert` and `SQLUpdate` read the tags into the columns. Values always travel as **parameters**, never pasted into the statement, so a tag holding a quotation mark cannot rewrite it — and the delimiter, which used to do that wrapping, has nothing left to do.

A bind list names tags from the dictionary, not system tags: a system tag would read on an insert but could not be written on a fetch. To stamp a row with the time, see [Stamping a row](#stamping-a-row).

`SQLInsert` and `SQLUpdate` send the tags' **current** values. The Run subscribes every bind list's tags from the moment it starts, whether or not a window shows them, so a batch log writes points nobody is watching. A tag whose value is bad quality — a device that is down — is written as a null, with a note in the runtime log; a `NOT NULL` column then makes the database refuse the row, which is the schema's decision to make.

A value is converted to what the receiving side can hold. Into a tag: text for a Message tag, 0 or 1 for a Discrete (a column holding `true`, `yes` or `on` counts as 1), a number for an Analog, rounded for an integer type. A null column leaves 0 or no text, and says so once in the runtime log. Out of a tag: a number for a number, text for text, and a tag's `TimeDate` as a date and time.

## Table templates

A template is the shape of a table, so an application can create the table it needs the first time it runs instead of somebody creating it by hand on every PC. `SQLCreateTable(ConnId, "BatchLog", "BatchLogTable")` creates the table and then one index per column that asks for one, named `<table>_<column>_idx`.

| Column setting | Meaning |
|---|---|
| Name | The column name |
| Type | Text, Whole number, Number, Decimal, On or off, Date, Time, Date and time |
| Length | Text: the number of characters. Decimal: the precision |
| Scale | Decimal: the digits after the point |
| Index | No index, Index (repeats allowed), Index (each value unique) |
| Allows null | Whether the column may be empty |

The types are **the studio's own**, and each database gets whatever it wants:

| Type | SQL Server | SQLite |
|---|---|---|
| Text | `NVARCHAR(length)`, 255 by default | `TEXT` |
| Whole number | `INT` | `INTEGER` |
| Number | `FLOAT` | `REAL` |
| Decimal | `DECIMAL(precision, scale)`, 18 and 2 by default | `NUMERIC` |
| On or off | `BIT` | `INTEGER` |
| Date, Time, Date and time | `DATE`, `TIME`, `DATETIME` | `TEXT` |

SQLite has no date type of its own and treats types as affinities, so a date column there holds the ISO 8601 text the product writes into it, which SQLite's own date functions understand.

**A table that is already there is left as it is**, so the ordinary way to use this is to call `SQLCreateTable` in the application's On start script every time it runs: the first run makes the table and every later one finds it. Nothing is altered — a table whose columns have since changed is not brought up to date — and the indexes the template asks for are created only when they are missing.

## Queries

A query is a statement kept in the project by name. `SQLLoadStatement(ConnId, "RecentBatches")` loads it into the connection's statement buffer, where `SQLPrepareStatement` and `SQLExecute` take it from.

A query lives in the project rather than in a `.sql` file beside the installer: a path names one machine's disk, a published application runs on another PC under another account, and a file somebody can edit is a file somebody can edit. Statements of any length and any number of lines are fine, and a `?` in one marks a parameter to be set before it runs.

## The script functions

All of them are for scripts only (application scripts, window scripts and a touch link's action script), and all but `SQLErrorMsg` return a **result code**: 0 for success, a negative number otherwise. The usual shape of a call is therefore

```
Res = SQLInsert(ConnId, "BatchLog", "BatchRow");
IF Res <> 0 THEN
    Status = SQLErrorMsg(Res);
ENDIF;
```

The functions that reach a database **pause the script** until it answers, like `ShowRecipeSelect` and a waiting `ShowWindow`: they may be used only as a statement or as the whole right-hand side of an assignment. Everything else — moving through rows already fetched, building a statement, setting a parameter — is immediate.

A failure also shows the operator a **Database Error** window naming the call and what went wrong, so a code alone is never all anybody gets. Running off the end of a selection and a null column go to the runtime log instead: a loop over rows ends by running off the end of them, and a null is something a table holds legitimately.

### Connecting

| Function | What it does |
|---|---|
| `SQLConnect(ConnIdTag, "DataSource")` | Opens the data source and writes the connection's number into `ConnIdTag`, an Analog tag. Every other function takes that number. A connect string may be given instead of a name |
| `SQLDisconnect(ConnId)` | Closes it, with whatever it had prepared |

Up to **8 connections** may be open at once. Numbering starts at 1 and counts up; a number is never used again in that Run.

A connection belongs to **the running application, not to one of its windows**: a connection one window's script opened is the same connection another window's script addresses by that number, and a popup closing takes nothing with it. Everything the application opened is closed as it stops, which rolls back any transaction still open. Where two copies of the application run at once — two project windows in Append HMI Desktop, two browsers in Append HMI Web — each has its own connections and its own numbering.

### Reading rows

| Function | What it does |
|---|---|
| `SQLSelect(ConnId, "Table", "BindList", "WhereExpr", "OrderByExpr")` | Fetches the bind list's columns from the table. Both expressions may be `""`. The rows are held by the application, not by the database |
| `SQLFirst(ConnId)`, `SQLNext(ConnId)`, `SQLPrev(ConnId)`, `SQLLast(ConnId)` | Move the cursor and write the row into the bind list's tags. -5 when there is no such row |
| `SQLGetRecord(ConnId, Number)` | The row at that number, counting from 1 |
| `SQLNumRows(ConnId)` | How many rows the selection found. **This one returns the count**, not a result code |
| `SQLEnd(ConnId)` | Lets the rows go |

The rows are fetched once and kept, so walking them costs nothing and a screen can page back and forth. A selection may bring back at most **10,000 rows**; a wider query is told so, and the rows it did get are still usable. `SQLEnd` is good manners rather than a necessity — the next selection on that connection replaces the rows anyway, and the Run lets them go as it stops.

```
Res = SQLSelect(ConnId, "BatchLog", "BatchRow", "Target > 100", "Stamp DESC");
Res = SQLFirst(ConnId);
WHILE Res == 0
    Count = Count + 1;
    Res = SQLNext(ConnId);
ENDWHILE;
Res = SQLEnd(ConnId);
```

### Writing rows

| Function | What it does |
|---|---|
| `SQLInsert(ConnId, "Table", "BindList")` | One new row from the current values of the bind list's tags |
| `SQLUpdate(ConnId, "Table", "BindList", "WhereExpr")` | Sets those columns in the rows the where expression matches. An empty expression is refused, so an update cannot change every row by accident |
| `SQLUpdateCurrent(ConnId)` | Writes the bind list's tags back into the row the cursor is on |
| `SQLDelete(ConnId, "Table", "WhereExpr")` | The rows the expression matches. An empty expression is refused |
| `SQLClearTable(ConnId, "Table")` | Every row, deliberately. The table stays |
| `SQLCreateTable(ConnId, "Table", "Template")` | A table shaped by one of the project's table templates. A table that is already there is left alone |
| `SQLDropTable(ConnId, "Table")` | The table and everything in it |

`SQLUpdateCurrent` has no row identity to work from — a selection is columns and values, not keys — so it finds the row again by matching every column it was fetched with. Rows identical to it are therefore updated too, and a table meant for this should carry a key column in its bind list.

### Statements of your own

A script can run any statement the database accepts. The connection carries one **statement buffer**, built with the three calls below, and a statement can be given a number of its own so that it can be run again with other parameter values.

| Function | What it does |
|---|---|
| `SQLSetStatement(ConnId, "SQL")` | Starts the buffer |
| `SQLAppendStatement(ConnId, "SQL")` | Adds to it. Nothing is put between the pieces, so the spaces are the script's to place |
| `SQLLoadStatement(ConnId, "Query")` | Fills it from one of the project's queries |
| `SQLPrepareStatement(ConnId, StmtIdTag)` | Turns the buffer into a statement with a number, written into `StmtIdTag` |
| `SQLExecute(ConnId, "BindList", StmtId)` | Runs it. Statement 0 is the buffer, run as it stands and emptied afterwards |
| `SQLClearStatement(ConnId, StmtId)` | Lets a prepared statement go. Statement 0 empties the buffer |

A statement that returns rows fills the results, to be walked with `SQLFirst` and the rest through the bind list named in the `SQLExecute` call. The bind list's items must line up with the statement's columns, since nothing but their order connects them. `""` as the bind list says the rows are not for tags — the cursor still moves and `SQLNumRows` still counts them — and is the right thing to pass for an `INSERT` or a `CREATE`.

A statement that returns no rows at all leaves a selection already in hand alone, so a script can run one in the middle of walking another's rows.

At most 32 statements may be prepared at once, each with at most 200 parameters, and a statement may be up to 100,000 characters long.

### Parameters

A `?` in a prepared statement is a parameter, numbered from 1 in the order they appear. Each is given a value before the statement runs:

| Function | Value |
|---|---|
| `SQLSetParamChar(StmtId, N, Value[, MaxLength])` | Text. Calling it again on the same parameter **adds to** what is there, which is how a long value is assembled. `MaxLength` is accepted and ignored: a bound parameter needs no length |
| `SQLSetParamInt(StmtId, N, Value)`, `SQLSetParamLong(StmtId, N, Value)` | A whole number |
| `SQLSetParamFloat(StmtId, N, Value)` | A floating-point number |
| `SQLSetParamDecimal(StmtId, N, Value[, Precision[, Scale]])` | An exact decimal. Said nothing, it keeps the digits the value itself has — worth giving, because a driver told nothing about scale would send 123.456 as 123 |
| `SQLSetParamDate(StmtId, N, Value)` | A date, with no time of day |
| `SQLSetParamTime(StmtId, N, Value)` | A time of day |
| `SQLSetParamDateTime(StmtId, N, Value[, Precision])` | Both. `Precision` is how many characters of `YYYY-MM-DD HH:MM:SS.mmm` to keep: 10 the date alone, 16 to the minute, 19 to the second, 23 with the milliseconds |
| `SQLSetParamNull(StmtId, N, Type[, Precision[, Scale]])` | A null, declared as 0 text, 1 a date and time, 2 a whole number, 3 a floating-point number or 4 a decimal |
| `SQLClearParam(StmtId, N)` | Unsets it |

A date or time value may be a Message tag holding `2026-10-17 14:05:09.270`, `2026-10-17`, `14:05` or `2:05 PM`, or an Analog holding a tag's `TimeDate` (milliseconds since 1970). `_DateTime` is already in the form a date-and-time column takes.

A parameter **keeps the type it was first given**: the statement was prepared for a shape, and a column that took a number last time round does not want text this time. `SQLClearParam` forgets a parameter so that a different type may be set. Every `?` must have a value before the statement runs, and a parameter cannot be added after it has run.

```
Res = SQLSetStatement(ConnId, "INSERT INTO BatchLog (Recipe, Target, Stamp) VALUES (?, ?, ?)");
Res = SQLPrepareStatement(ConnId, StmtId);
Res = SQLSetParamChar(StmtId, 1, Batch_Recipe);
Res = SQLSetParamDecimal(StmtId, 2, Batch_Target, 9, 3);
Res = SQLSetParamDateTime(StmtId, 3, _DateTime);
Res = SQLExecute(ConnId, "", StmtId);
Res = SQLClearStatement(ConnId, StmtId);
```

### A prepared insert

A loop that writes a row per pass can build its insert once:

| Function | What it does |
|---|---|
| `SQLInsertPrepare(ConnId, "Table", "BindList", StmtIdTag)` | Builds the insert the bind list describes and keeps it by number |
| `SQLInsertExecute(ConnId, "BindList", StmtId)` | One row from the current values of that bind list's tags |
| `SQLInsertEnd(ConnId, StmtId)` | Lets it go |

The bind list named in the execute call must be the one it was prepared with, or `""`, which means the same thing: another list would have other columns and the statement cannot take them. Its parameters are the tags, so the `SQLSetParam*` family has nothing to say to a prepared insert.

### Transactions

`SQLTransact(ConnId)` begins one, `SQLCommit(ConnId)` makes it permanent and `SQLRollback(ConnId)` undoes everything since the begin. Everything a script writes between the first and the second reaches the table together, or not at all — which is what a batch record spread over three tables needs.

One transaction at a time per connection. Committing when none is open, or beginning a second, is reported in those words rather than in the database's. A Run that ends with a transaction open rolls it back, because closing the connection does.

### Error messages

`SQLErrorMsg(Code)` returns **the message** for a result code, as text, for a script to put into a Message tag and show on the screen. `SQLErrorMsg(-1)` gives the database's own words for the last failure, which is where a constraint violation or a missing column is explained.

## Result codes

0 is success. The negative numbers are an interface — scripts test them — and they are the same numbers a project from elsewhere would be testing.

| Code | Meaning |
|---|---|
| 0 | No errors occurred |
| -1 | The database reported an error (`SQLErrorMsg(-1)` for its own words) |
| -2 | There is no statement to execute |
| -4 | The value read was null |
| -5 | There are no more rows to fetch |
| -7 | That parameter number is not valid |
| -8 | The statement's parameters have not all been set |
| -9 | That type cannot be used for a null parameter |
| -10 | A parameter's type cannot be changed once it is set |
| -11 | A parameter cannot be added after the statement has run |
| -12 | That is not a valid date or time |
| -13 | That is not a valid decimal number |
| -15 | That operation does not suit this kind of statement |
| -1001 | There is not enough memory to complete this operation |
| -1002 | That connection number is not open |
| -1003 | There is no bind list of that name |
| -1004 | There is no table template of that name |
| -1005 | An internal error occurred |
| -1006 | The string read was null |
| -1008 | Deleting records needs a where expression |
| -1009 | The database could not be reached |
| -1010 | That database does not exist |
| -1011 | No rows have been selected |
| -1013 | The file could not be found |
| -1100 | There is no data source of that name |
| -1101 | The database did not answer in time |
| -1102 | The query returned more rows than the limit allows |
| -1103 | Too many database connections are open |
| -1104 | That kind of database is not supported |
| -1105 | There is no query of that name |
| -1106 | That does not suit the current transaction |
| -1107 | A database file must be in Documents or the application data folder |
| -1108 | The row the cursor is on is no longer in the table |
| -1109 | There is no prepared statement of that number |

-4, -5 and -1006 are not failures and do not show the operator anything: a walk over rows ends with -5, and a null column is a fact about the table.

Message tags have no length limit, so nothing a database holds is ever truncated on its way into one, however long a column is. The code the legacy function set used to warn about that is reserved and never returned.

## Stamping a row

A bind list names dictionary tags, so `_DateTime` cannot be in one. There are three good ways to record when something happened:

- **a tag's `TimeDate`**: a bind item whose dotfield is `TimeDate` records when that tag last changed, which for a batch-complete flag is the moment the batch finished;
- **a Message tag from `_DateTime`**: `Batch_Stamp = _DateTime;` in the same script, just before the insert. The text is exactly what a date-and-time column takes;
- **the database's own clock**: `GETDATE()` on SQL Server, `CURRENT_TIMESTAMP` on SQLite, in a statement of your own — which is when the row landed, and comes from the server rather than from the PC.

`_DateTime`, `_DateString`, `_TimeString` and the numeric date and time tags are described in [HMI_AUTOMATION.md](HMI_AUTOMATION.md); so are the `String*` functions, which is what builds a where expression out of tag values.

**SQL Server's `datetime` keeps about 1/300 of a second**, so a stamp written as `.969` reads back as `.970`. A table template's Date and time column is a `datetime`, which is what every version of SQL Server has; where the exact millisecond matters, create the column as `datetime2` with a statement of your own. SQLite keeps the text it was given, milliseconds and all.

## Validate

HMI > Validate reports what the Database dialog would not let you save and what only the whole project can answer:

- a name that is missing, too long, repeated or holds a quotation mark, in any of the four lists;
- a data source with no server, or a SQLite one with no file name or a file name that is a path;
- a port outside 1 to 65535, or a timeout outside 100 to 600,000 milliseconds;
- a bind list with no items, an item with no tag or no column, a column named twice;
- a bind list naming **a tag that does not exist** — and if the name is a system tag, that a bind list names dictionary tags, so copy it into one first;
- a template with no columns, a text column with no length, a decimal whose scale is more than its precision;
- a column named after a word SQL Server or SQLite reserves, such as `USER` or `ORDER`, or a name that would need square brackets around it in a where expression;
- a query with no statement;
- a `SQL*` call naming a data source, bind list, table template or query **that the project does not have**, and a literal empty where expression on `SQLUpdate` or `SQLDelete`.

Find/Replace Tag References reaches a bind list's tags, and renaming a tag renames it there too. See [HMI_TAG_REFERENCES.md](HMI_TAG_REFERENCES.md).

## Where everything runs

Only the main process talks to a database (`src/main/sql/`): the window's own page may not open a socket, and it never sees a file path or a stored password. So the same project works the same way in all three products:

| | Connections belong to | SQLite files are under |
|---|---|---|
| Append HMI Studio, and a published application | the window that runs the application | its own settings folder, or Documents |
| Append HMI Desktop | each project window | Append HMI Desktop's settings folder, or Documents |
| Append HMI Web | each browser that has the page open | Append HMI Web's settings folder on the server, or the server's Documents |

In Append HMI Web the application runs in the browser and the database work happens on the server, so the browser needs no database access and the credentials never leave the server. Each browser gets its own connections and its own numbering, exactly as it gets its own login and its own PLC session. Several browsers logging to the same table is several clients writing to one table, which is what a database is for; several browsers writing the same SQLite **file** works too, since SQLite locks it, but a server database is the better answer for more than a handful.

The command line builds all four lists from a JSON spec, and `--hmi-dump` gives them back; see [HMI_AUTOMATION.md](HMI_AUTOMATION.md). A dump keeps a password in its wrapped form, so rebuilding a project from one is lossless and does not expose it.

## A worked example: logging batches

`examples/LiquidWeighHMI.ahmi` and `examples/LiquidWeighHMISim.ahmi` write a record of every completed batch to a database and read them back on a Batch History screen. The Sim needs neither a PLC nor a server, so it is the quickest way to see the whole thing work: open it, press Run, press START, and each batch appears on the Batch History screen as it finishes.

What is in the project:

- a data source **BatchDb** — a SQLite file, `LiquidWeighBatches.db`, in the Documents folder of whatever PC runs the application, so there is nothing to set up on site;
- a table template **BatchLogTable** — the stamp, the batch number, the recipe, the volume, the total target and actual, the cycle time, the operator, and a target and an actual for each of the three ingredients;
- a bind list **BatchRow**, pairing each of those with a tag, and **BatchHistoryRow**, pairing the same columns with the `Hist_` tags the history screen shows;
- a query **RecentBatches**, which is what the history screen runs;
- the logging itself in the application scripts: **On start** connects and makes sure of the table, and **While running** watches the batch counter and inserts a row when it moves on, stamped from `_DateTime` and with `_Username` as the operator;
- the **Batch History** screen, reached from the Batch screen. Its On show script runs the query; NEWEST, NEWER, OLDER and OLDEST walk the rows with `SQLFirst`, `SQLPrev`, `SQLNext` and `SQLLast`, each of which writes the row it lands on into the bind list's tags, which is what the boxes show.
