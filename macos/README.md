# Monitor Switcher for macOS

Menu bar app and command-line tool for Apple Silicon Macs. Same features, profiles and safety rules
as the Windows app; see the main [README](../README.md) for what the app does.

## Requirements

- An Apple Silicon Mac on macOS 13 (Ventura) or later. Intel Macs are not supported yet.
- The monitor connected over **USB-C or DisplayPort**. DDC/CI does not work through the built-in HDMI
  port of M1 and entry-level M2 Macs; it does on newer Macs' HDMI ports and on most USB-C docks that
  pass DisplayPort through.
- Xcode 15 or later (Swift 5.9) to build.

## Build and run

```sh
cd macos
scripts/sync-shared.sh          # copies ../profiles and ../spec into the package (run after changing them)
swift test                      # unit tests (packets, guard, parsers, profiles, toggle, setup planner)
swift run monitorswitcher --list
scripts/build-app.sh            # dist/Monitor Switcher.app and dist/MonitorSwitcher-macOS.zip
```

`swift run MonitorSwitcherApp` also works for development, but notifications and "launch at login"
need the bundled app from `build-app.sh`.

The CLI is inside the app bundle. To use it from Terminal:

```sh
ln -s "/Applications/Monitor Switcher.app/Contents/MacOS/monitorswitcher" /usr/local/bin/monitorswitcher
monitorswitcher --input hdmi2
```

## Layout

| Path | What it is |
|---|---|
| `Sources/MonitorSwitcherCore` | Port of the Windows core: packets, safety guard, protocols, profiles, switching, setup planner. Foundation only. |
| `Sources/MonitorSwitcherMac` | IOAVService transport, IORegistry display discovery, file lock, rotating log, composition root. |
| `Sources/monitorswitcher` | CLI with the same flags and exit codes as Windows. |
| `Sources/MonitorSwitcherApp` | SwiftUI menu bar app, settings window, setup wizard, Carbon global hotkeys. |
| `Tests/MonitorSwitcherCoreTests` | XCTest suite; reuses `spec/packet-vectors.json` so both platforms send identical bytes. |

Files live in `~/Library/Application Support/MonitorSwitcher` (`settings.json`, `state.json`,
`profiles/` for your own profiles, `logs/monitorswitcher.log`). Set `MONITORSWITCHER_HOME` to use
another folder.

## Notes

- IOAVService is a private Apple API (the same one MonitorControl and m1ddc use). The app is
  therefore distributed outside the Mac App Store.
- Some monitors, like the LG 32UN880, only obey the computer they are showing. Each computer can
  then switch the monitor away from itself, not back: run Monitor Switcher on both computers.
- Hotkeys use Carbon's `RegisterEventHotKey`, so no Accessibility permission is needed.
