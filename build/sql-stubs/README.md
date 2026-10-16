# Stub packages

The SQL Server driver eagerly requires `@azure/identity` and
`@azure/keyvault-keys`, for Azure AD authentication and Key Vault column
encryption. This application offers neither: a data source authenticates with
a user name and password, or with none.

Those two packages and their dependencies are about 43 MB — a third again on
top of a whole installer — for code that can never run here. The `overrides`
block in `package.json` points both at the stubs beside this file, so npm never
installs the real ones, in a development tree or a package.

Each stub exports the names the driver looks up, and every one of them throws
if it is ever constructed. If a future version of the driver reaches for a name
that is not here, it fails loudly at startup with a missing-export error rather
than quietly doing the wrong thing, and the fix is to add the name.
