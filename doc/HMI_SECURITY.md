# Users and security

Append HMI Studio has InTouch-style access-level security:

- users log in with a name and password;
- each user has an **access level** from 0 to 9999;
- animations read the logged-in user through two system tags and allow or
  block touch with the **Enable** animation.

## Defining users

**HMI > Users** lists the project's users.

- **Add a user:** enter a name, an access level and the password twice, then
  **Add**.
- **Change a user:** select **Edit**, change the fields, then **Update**. Leave
  the password empty to keep it.
- **Save:** **OK** saves the list into the project.

Names:
- 1 to 32 letters, digits, spaces or `. _ - @`;
- case-insensitive;
- cannot be `None`, which is reserved for the logged-out state.

Passwords are never stored. The project keeps a random salt and a
PBKDF2-SHA256 hash (20,000 iterations) for each user.

**Application Settings > Security > Log out after (min)** logs the user out
after that many minutes without a touch or key press. 0 (the default) never
logs out.

## System tags

| Tag | Type | Value |
|---|---|---|
| `_Username` | Message | The logged-in user's name, or `None` |
| `_AccessLevel` | Integer | The logged-in user's access level, or 0 |

Both are read-only, and objects that use them repaint on login and logout.
For example:
- a Value Display on `_Username`;
- a Visibility link on `_AccessLevel >= 500`.

## Script functions

These work only in scripts (Action Scripts and window scripts), not in
animation expressions.

| Function | What it does |
|---|---|
| `ShowLogin()` | Opens the built-in login window: user name, masked password, on-screen keyboard. The login window checks the password itself |
| `Login(name, password)` | Logs in; returns 1, or 0 if the name or password is wrong |
| `Logout()` | Logs out (`_Username` becomes `None`, `_AccessLevel` 0) |
| `ChangePassword(old, new)` | Changes the logged-in user's password; returns 1 or 0 |
| `ShowUserManager()` | Opens the Users window at run time (see below) |

### A custom login screen

1. **Tags:** make two memory message tags, e.g. `LoginName` and
   `LoginPassword`.
2. **Entry objects:** give each a User Input (String) link. Tick **Masked
   input** on the password's link, so the entry shows dots and not the current
   value.
3. **Log In button:** give it an Action Script:

```
IF Login(LoginName, LoginPassword) THEN
    LoginPassword = "";
    LoginMessage = "Welcome " + _Username;
ELSE
    LoginPassword = "";
    LoginMessage = "Invalid user name or password";
ENDIF;
```

Clear the password tag after use, as above: a memory tag keeps its value
while the application runs. Never make a password tag retentive.

## Enabling and disabling touch

The **Enable** animation, under Display, uses an expression like Visibility
does:

- **Sense Enabled while true:** touch works only while the expression is
  true, e.g. `_AccessLevel >= 500`.
- **Sense Disabled while true:** touch is blocked while the expression is
  true.

A disabled object ignores all of its touch links: pushbuttons, action
scripts, user input, sliders and show/hide window. If the expression has bad
quality, the object is disabled.

The older **Disable** animation still works in existing projects but is no
longer offered.

## Changing users at run time

`ShowUserManager()` (e.g. on a button enabled by `_AccessLevel >= 9000`) opens
the Users window in the running application.

- **Where changes go:** changes, and passwords changed with
  `ChangePassword`, are saved on that PC:
  - published runtime: `%APPDATA%\<Product>\users\<Product>.json`;
  - editor: `userData/users/<project>.json`.
- **Precedence:** once saved, that list replaces the project's users on that
  PC, including after a reinstall of the same product.
- **Self-removal:** the logged-in user cannot remove themselves.
- **Undo:** in the studio, **HMI > Clear Runtime User Changes** (with the Run
  stopped) forgets the editor's saved list. To reset a published PC, delete
  its `users` folder.

## Command line

In a spec for `--hmi-build`:

```json
"settings": {"security": {"autoLogoutMin": 10}},
"users": [
  {"name": "Operator", "level": 100, "password": "op"},
  {"name": "Supervisor", "level": 900, "password": "super"}
]
```

- **Passwords:** hashed at build time.
- **Dumps:** `--hmi-dump` writes `salt`, `hash` and `iterations` in place of
  the password, so dump → build keeps them.

## Limits

This protects against casual use of the operator screens. It is not a
replacement for Windows security:
- the project file and the PC's user list can be edited by anyone with access
  to the files;
- the PLC does not know who is logged in.
