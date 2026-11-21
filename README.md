# Monitor Switcher

Switch your monitor's input with a keyboard shortcut. One monitor, two computers: press a key
and the screen goes to the other machine.

Monitor Switcher talks to the monitor over **DDC/CI**, the same channel the monitor's own menu
settings use. It works with any monitor that supports DDC/CI, and it knows the vendor-specific
commands some monitors need. For example, the LG UltraFine Ergo 32UN880 ignores the standard
input command but obeys LG's own.

- **Windows:** system tray app, global shortcuts, a setup wizard, and a `monitorswitcher` command for scripts.
- **macOS (Apple Silicon):** menu bar app with the same features.
- **Small and fast:** no Electron. On Windows the tray app opens in about a third of a second and idles at about 12 MB of private memory.

> **Status:** early (0.1). The Windows NVIDIA path and the LG 32UN880 profile are verified on
> real hardware. The other backends are written but need people with that hardware to confirm
> them; see [Supported hardware](#supported-hardware).

## Quick start (Windows)

1. Get a build:
   - **Download:** open the latest successful
     [CI run](https://github.com/ashrash/monitor-control/actions/workflows/ci.yml) and download
     `MonitorSwitcher-windows-x64`. Downloading artifacts needs a GitHub sign-in.
     - This build needs the [.NET 11 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/11.0)
       (`winget install Microsoft.DotNet.DesktopRuntime.11`).
     - `MonitorSwitcher-windows-x64-standalone` needs nothing installed, but is 130 MB.
   - **Or build it** (needs the .NET 11 SDK):
     ```
     dotnet publish windows/src/MonitorSwitcher.App -c Release -r win-x64 --self-contained false -o out
     ```
2. Run `MonitorSwitcher.exe`. It opens Settings the first time, then lives in the system tray.
3. In Settings:
   - give your inputs names (for example "PC" and "Mac");
   - mark which input **this computer** is plugged into;
   - click a shortcut box and press a key combination.
4. If your monitor isn't recognised, or to confirm it works, use **Set up a monitor…** from the
   tray menu. The wizard tries the input codes for you, switches back after a countdown, asks
   whether it worked, and saves what works.

## Quick start (macOS, Apple Silicon)

Download `MonitorSwitcher-macOS` from the same CI run, or build it yourself:

```
cd macos
./scripts/build-app.sh   # produces dist/Monitor Switcher.app
```

The app is ad-hoc signed, so the first time you open it, right-click it and choose **Open**.
DDC works over USB-C/Thunderbolt and DisplayPort. The built-in HDMI port on M1 and entry-level M2
Macs doesn't pass DDC. See [macos/README.md](macos/README.md).

## Two computers, one monitor: read this

Some monitors (the LG 32UN880 among them) **only obey the computer they are currently
showing**. Your PC can switch the monitor to your Mac, but once the Mac is on screen, the PC's
commands are ignored. Install Monitor Switcher on **both** computers and give each one a
shortcut to the other. The setup wizard detects this behaviour, and the app tells you when it
applies.

## Command line

The Windows package includes `monitorswitcher.com`, which runs when you type `monitorswitcher`
in a terminal (the tray app is `MonitorSwitcher.exe`). The macOS build includes the same
`monitorswitcher` command.

```
monitorswitcher --input usbc            # by id
monitorswitcher --input Mac             # by your own label
monitorswitcher --toggle                # between the two toggle inputs from Settings
monitorswitcher --toggle hdmi2,usbc
monitorswitcher --list                  # monitors, profiles, inputs
monitorswitcher --diagnose              # drivers, DDC/CI health, profile problems
monitorswitcher --read-vcp 0x60         # read-only, for profile authors
monitorswitcher --input usbc --monitor 2   # pick a monitor when several are connected
```

Exit codes:

| Code | Meaning |
|---|---|
| 0 | OK |
| 1 | Error |
| 2 | Bad arguments |
| 3 | Monitor not found, or ambiguous |
| 4 | No supported GPU or driver |
| 5 | The monitor did not accept the command |
| 6 | Unknown input |
| 7 | Not configured |
| 8 | Blocked by a safety check |

`MonitorSwitcher.exe --input usbc` also works without a console, for desktop shortcuts and
launchers such as Stream Deck.

## Supported hardware

### Monitors with a built-in profile

| Monitor | Protocol | Verified |
|---|---|---|
| LG UltraFine Ergo 32UN880 | LG alternative (VCP 0xF4 via source 0x50) | USB-C from an NVIDIA PC. HDMI 2 to be confirmed from the Mac side. |

Any other DDC/CI monitor works with the standard MCCS command. Run the setup wizard to find its
codes, then [share the profile](docs/adding-a-profile.md) so it ships built in.

### Graphics drivers and platforms

| Backend | Sends vendor commands (e.g. LG) | Status |
|---|---|---|
| NVIDIA (NvAPI) | yes | Verified (RTX 5070) |
| AMD (ADL) | yes | Implemented; loads and enumerates, but switching is unverified, so reports are welcome |
| Intel (IGCL) | yes | Experimental and off by default (`MONITORSWITCHER_ENABLE_IGCL=1`); unverified |
| Windows DDC/CI (dxva2) | standard MCCS only | Works with any GPU |
| macOS IOAVService | yes | Apple Silicon; new, needs testing |

## Troubleshooting

Run `monitorswitcher --diagnose` first. Common causes:

- **DDC/CI is turned off** in the monitor's on-screen menu.
- **The cable path doesn't carry DDC.** Some docks, KVM switches and HDMI/USB-C adapters don't
  pass it; try a direct cable.
- **"Monitor is showing another input".** Your monitor only obeys the computer it's showing; see
  [above](#two-computers-one-monitor-read-this).
- **The screen just blinks** with the standard command. The monitor probably needs a vendor
  protocol; run the setup wizard.

Logs are in `%LOCALAPPDATA%\MonitorSwitcher\logs` (tray menu, **Open log folder**) or
`~/Library/Application Support/MonitorSwitcher/logs`.

## How it works

Three independent layers, so new monitors, vendors and GPUs can be added without touching the
rest:

1. **Transports:** how bytes reach the monitor (NvAPI, ADL, IGCL, dxva2, IOAVService).
2. **Protocols:** which bytes switch the input (standard MCCS, LG alternative, …).
3. **Profiles:** JSON files that match monitors by EDID and list their protocol and input codes.

Details are in [docs/architecture.md](docs/architecture.md).

**Safety.**

- **Device address:** the app only ever writes to the DDC/CI address 0x6E, never to the EDID
  chip (0xA0).
- **Values:** it only sends codes from the monitor's profile, or from the wizard's candidate list.
- **One monitor at a time:** it never broadcasts to every monitor.

## Contributing

- [Add a monitor profile](docs/adding-a-profile.md): the most useful contribution, and no code needed.
- [Add a vendor protocol](docs/adding-a-protocol.md)
- [Add a GPU/OS backend](docs/adding-a-transport.md)

Build and test on Windows:

```
dotnet test windows/MonitorSwitcher.slnx
```

On macOS:

```
cd macos && ./scripts/sync-shared.sh && swift test
```

## License

[MIT](LICENSE)
