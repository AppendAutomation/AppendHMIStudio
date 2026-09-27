Append HMI Studio
=================

**Append HMI Studio** designs operator screens (HMIs) for PLCs, runs them against live equipment, and publishes them as Windows installers for the target PC.

- **Design** screens on a diagram canvas, with tags, animation links, window properties and scripts.
- **Run** them in the editor against real devices through the bundled comms server:
  - EtherNet/IP ControlLogix/CompactLogix;
  - SLC 5/05 and MicroLogix;
  - Modbus TCP;
  - a built-in simulator.
- **Alarms**: analog and discrete alarms with acknowledgement, the `_AlarmsActive`, `_AlarmsUnacked` and `_AckAll` system tags, Alarm List and Alarm History objects, and daily CSV history. See [doc/HMI_ALARMS.md](doc/HMI_ALARMS.md).
- **Retentive memory tags** keep their last value from one run to the next.
- **Users and security**: users with access levels 0–9999, a built-in login window, the `_Username` and `_AccessLevel` system tags, login script functions, masked password entry and the Enable animation. See [doc/HMI_SECURITY.md](doc/HMI_SECURITY.md).
- **Automation**: build, check, render and publish projects from the command line or a script, from a plain JSON spec. See [doc/HMI_AUTOMATION.md](doc/HMI_AUTOMATION.md).
- **Publish** a project as a Windows installer (HMI > Publish). It installs a locked, run-only copy of the app with the comms server, built on any platform. See [doc/HMI_PUBLISH.md](doc/HMI_PUBLISH.md).

Projects are saved as `.ahmi` files. Files saved as `.drawio-hmi` by earlier builds still open.

Download
--------

Releases are published at [github.com/AppendAutomation/append-hmi-studio/releases](https://github.com/AppendAutomation/append-hmi-studio/releases):

- `Append-HMI-Studio-<version>-Setup.exe`
  - Windows 10/11 x64 installer, for all users (administrator rights).
  - Silent install: `/S`. Silent uninstall: `"C:\Program Files\Append HMI Studio\Uninstall.exe" /S`.
- `Append-HMI-Studio-<version>-x86_64.AppImage`: Linux, runs without installing.
- `Append-HMI-Studio-<version>-amd64.deb`: Debian and Ubuntu (`sudo apt install ./Append-HMI-Studio-*.deb`).

The installers are not code-signed yet, so Windows SmartScreen asks for confirmation on first run.

There is no automatic update: install a newer version over the old one.

Settings and logs are kept in `%APPDATA%\Append HMI Studio` (Windows) and `~/.config/Append HMI Studio` (Linux).

Security
--------

The app works offline. Diagram and project data never leave the machine, and the Content Security Policy forbids remotely loaded code. The only network traffic is to the PLCs a project is configured to talk to, through the local `hmi-comms` server.

A diagram can still reference external media (an image or font by URL). That media is fetched when the diagram is opened, so the remote server sees the request, but no diagram content is sent.

Building
--------

See [doc/BUILDING.md](doc/BUILDING.md). In short, with Node.js 22.12+ and the .NET 8 SDK:

```
git clone --recursive https://github.com/AppendAutomation/append-hmi-studio.git
cd append-hmi-studio
npm install
npm start                 # run from source
npm run dist-win          # Windows installer (on Linux too, no wine)
npm run dist-linux        # AppImage and deb
```

License and attribution
-----------------------

Append HMI Studio is © 2026 Append Automation and is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)).

It is built on the [draw.io](https://github.com/jgraph/drawio) diagram editor and [drawio-desktop](https://github.com/jgraph/drawio-desktop) by JGraph Ltd, used and modified under the Apache License 2.0. [NOTICE](NOTICE) lists the third-party works and the changes made. Append HMI Studio is not affiliated with or endorsed by JGraph Ltd; "draw.io" is a trademark of its owner.
