# Release process

## 1. Prepare

1. Update `version` in `package.json` (semantic versioning), then run `npm install` so `package-lock.json` follows.
2. Run `npm test` and `npm run test-comms`, plus the in-app self test (`HMI_ENV=dev` with `{"hmitest": 1}`, see `doc/BUILDING.md`).
3. Commit: `Prepare release vX.Y.Z`.

## 2. Tag and build

```
git tag vX.Y.Z
git push origin main vX.Y.Z
```

The tag starts `.github/workflows/release.yml`. It builds on Linux:
- the Windows installer;
- the AppImage;
- the deb.

It then creates a **draft** GitHub release with the three files attached.

To build locally instead, run `npm run dist-win` and `npm run dist-linux` (see `doc/BUILDING.md`).

## 3. Verify before publishing the draft

**Windows (10 or 11, x64)**
1. Install the Setup.exe (and silently with `/S` on a second machine or after uninstalling).
2. Check:
   - Start menu shortcut and icon;
   - Add/Remove Programs entry: name, publisher, version;
   - double-clicking an `.ahmi` file opens it.
3. Open a project, run it against a PLC or the simulator, save it.
4. Publish it (HMI > Publish), install the package on a target PC and run it.
5. Uninstall both; nothing should remain in `C:\Program Files\Append HMI Studio`.

**Linux**
1. Run the AppImage, then install the deb (`sudo apt install ./….deb`).
2. Check:
   - the menu entry and icon;
   - Help > About (versions and licenses);
   - HMI > Run against a device;
   - HMI > Publish produces an installer.

**Both**
- Help > About shows the new version.
- No draw.io branding is visible anywhere (title, menus, dialogs).

## 4. Publish

Edit the draft release notes (changes, known issues) and publish it.
