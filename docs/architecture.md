# Architecture

Monitor Switcher has three independent layers, so a new monitor, vendor command or GPU can be
added without touching the others. The UIs (Windows tray app, command line, setup wizard, macOS
menu bar app) sit on top and never deal with bytes.

```
 Tray app / CLI / setup wizard / macOS app
        │  SwitchTo(monitor, "usbc")
 SwitchService ──── ProfileCatalog ── profiles/*.json (built in) + user profiles
        │  1. profile says: protocol lg-alternative, usbc = 0xD1
        │  2. refuses anything not in the profile
 Protocol (layer 2)          StandardMccsProtocol, LgAlternativeProtocol, ...
        │  builds SetVcpFrame(source 0x50, vcp 0xF4, value 0xD1)
 DdcTransport.Send           runs DdcGuard: device 0x6E only, source 0x50/0x51 only
        │
 Transport (layer 1)         NvApi, Adl, Igcl, Dxva2 (Windows) · IOAVService (macOS)
        │
 Monitor (layer 3 describes it: profiles/*.json)
```

## Layer 1: transports (how bytes reach the monitor)

`DdcTransport` (`windows/src/MonitorSwitcher.Core/Transports/DdcTransport.cs`)

| Transport | API | Raw I2C | Status |
|---|---|---|---|
| `nvapi` | NVIDIA `NvAPI_I2CWrite` | yes | Verified on an RTX 5070 |
| `adl` | AMD `ADL2_Display_DDCBlockAccess_Get` | yes | Loads and enumerates; not yet verified switching a monitor |
| `igcl` | Intel `ctlI2CAccess` | yes | Experimental, off unless `MONITORSWITCHER_ENABLE_IGCL=1` |
| `dxva2` | Windows `SetVCPFeature` | no (standard MCCS only) | Works with any GPU; also used for reads |
| `ioavservice` | macOS `IOAVServiceWriteI2C` | yes | Apple Silicon |

Each transport:

- `Probe()` reports whether its driver API is present.
- `Enumerate()` lists the monitors it reaches, identified by EDID (maker, product code, serial).
- `Send()` is not virtual: it runs the safety guard, then calls the transport's `SendCore()`.
- Transports never take a device address from callers; 0x6E is a constant.

Endpoints from several transports that reach the same physical monitor are merged into one
`ResolvedMonitor`. The lowest `Priority` transport that can send the protocol is used.

## Layer 2: protocols (what bytes to send)

`InputProtocol` (`windows/src/MonitorSwitcher.Core/Protocols/`)

- **`mccs-standard`**: VESA MCCS Input Select, VCP 0x60 with source 0x51.
- **`lg-alternative`**: LG's vendor command, VCP 0xF4 with source 0x50. It needs a raw-I2C
  transport, because OS VCP APIs always use source 0x51.

A protocol also lists candidate codes per port for the setup wizard, and ranks itself for
monitors that have no profile (LG's ranks first on `GSM` monitors).

## Layer 3: monitor profiles (JSON)

`profiles/<vendor>/<vendor>-<model>.json`, schema `profiles/profile.schema.json`. A profile
matches monitors by EDID and lists the protocol, the input codes and any quirks. Matching order:

1. EDID product code
2. model token in the DDC/CI capabilities string
3. EDID monitor name

User profiles in the app data folder win a tie with built-in ones. A monitor with no match gets
a generated standard-MCCS profile, and the app suggests the setup wizard.

## Safety

- **Device address:** `DdcGuard` refuses any I2C device address other than 0x6E. 0xA0 is the
  EDID EEPROM.
- **Source byte and checksum:** the source byte must be 0x51 or 0x50, and full messages must
  carry a correct checksum.
- **Values:** `SwitchService` only sends codes from the monitor's profile. The setup wizard may
  also send codes from the protocol's own candidate list (`SendCandidate`).
- **One monitor at a time:** an ambiguous `--monitor` is an error, never a broadcast.
- **Pinned layouts:** native struct layouts and field offsets are fixed by unit tests
  (`InteropLayoutTests`), because a shifted field could address the wrong I2C device.

## Monitors that only obey the computer they show

Some monitors, including the LG 32UN880, ignore DDC/CI that arrives on an input they are not
showing. A computer can then switch the monitor *away* from itself but not *back*. Such a profile
sets the quirk `ignoresCommandsWhenInactive`. Then:

- the toggle on this computer always means "go to the other input";
- trying to switch to this computer's own input while the monitor shows another explains why
  that can't work (the monitor stops answering DDC/CI reads, which is how the app can tell);
- the setup wizard detects the quirk: the monitor goes quiet during the test and only answers
  again once the user comes back with the monitor's buttons.

To switch back, run Monitor Switcher on the other computer as well.

## Files at runtime

| File | Windows | macOS |
|---|---|---|
| Settings | `%LOCALAPPDATA%\MonitorSwitcher\settings.json` | `~/Library/Application Support/MonitorSwitcher/settings.json` |
| State (last input, cached capabilities) | `state.json` (same folder) | same |
| User profiles | `profiles\*.json` | `profiles/*.json` |
| Logs (1 MB × 5, rotating) | `logs\monitorswitcher.log` | `logs/monitorswitcher.log` |

Set `MONITORSWITCHER_HOME` to use a different folder, for example when testing.

## Project layout

```
profiles/            shared monitor profiles + JSON schema (embedded in both apps)
spec/                golden DDC packets both test suites must reproduce
windows/src/MonitorSwitcher.Core      layers 2 and 3, switching, setup planner (no OS code, AOT-safe)
windows/src/MonitorSwitcher.Windows   transports, display enumeration, logging (AOT-safe)
windows/src/MonitorSwitcher.Cli       monitorswitcher.com
windows/src/MonitorSwitcher.App       WinForms tray app, settings, setup wizard
windows/tests/MonitorSwitcher.Tests   xUnit
macos/               Swift package: core port, IOAVService transport, CLI, menu bar app
tools/make-icon.ps1  regenerates the icon
poc/                 the original PowerShell proof of concept
```
