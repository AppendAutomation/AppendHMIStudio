# Stub packages

The SQL Server driver eagerly requires `@azure/identity` and
`@azure/keyvault-keys`, for Azure AD authentication and Key Vault column
encryption. This application offers neither: a data source authenticates with
a user name and password, or with none.

Those two packages and their dependencies are about 43 MB — a third again on
top of a whole installer — for code that can never run here. Both
electron-builder configs leave them out of the package and map the stubs
beside this file over the top, so a build ships these few lines instead. A
development tree still installs the real ones: npm `overrides` with a `file:`
target resolves the path against the link rather than the project, which comes
out broken, so the substitution belongs in the packaging configs, where a test
pins it (`src/test/publish-packaging.test.js`).

Append HMI Desktop and Append HMI Web map the same two folders through their
own configs, out of the `studio` submodule.

Each stub exports the names the driver looks up, and every one of them throws
if it is ever constructed. If a future version of the driver reaches for a name
that is not here, it fails loudly at startup with a missing-export error rather
than quietly doing the wrong thing, and the fix is to add the name.
