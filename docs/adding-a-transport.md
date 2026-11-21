# Adding a transport (GPU or OS backend)

A transport moves DDC/CI bytes to a monitor. Subclass `DdcTransport`
(`windows/src/MonitorSwitcher.Core/Transports/DdcTransport.cs`). The macOS equivalent lives in
`macos/Sources/MonitorSwitcherMac`.

## Contract

| Member | What it must do |
|---|---|
| `Id`, `DisplayName` | Short id (`nvapi`) and a human name. |
| `SupportsRawI2C` | `true` if it can send any source byte; `false` for OS VCP APIs (standard MCCS only). |
| `Priority` | Lower wins when several transports reach the same monitor. Vendor raw-I2C transports use 10–30, OS APIs 50. |
| `Probe()` | Never throws. Reports `Unavailable` with a reason and hint when the driver library is missing. |
| `Enumerate()` | Returns one `DisplayEndpoint` per reachable monitor, with a `MonitorIdentity` from EDID and, if known, the OS display name. |
| `SendCore(endpoint, frame)` | Delivers an already validated frame. Called only from the non-virtual `Send`. |
| `SupportsReads` / `TryReadVcp` / `TryReadCapabilities` | Optional. Reads enable diagnostics and the "is the monitor showing another input" check. |
| `Describe` / `TryRestore` | Optional. Lets the app reach a monitor the OS stopped listing. |

## Rules

1. **Device address.** Never accept a device address from a caller. Use
   `DdcConstants.DisplayDeviceAddress` (0x6E) and call `DdcGuard.EnsureDeviceAddress` right
   before the native write. If the API takes the whole message, run `DdcGuard.EnsureMessage` on it.
2. **Driver libraries.** Load them from System32 only:
   `NativeLibrary.TryLoad(name, assembly, DllImportSearchPath.System32, ...)`. A DLL planted next
   to the app must never be used.
3. **Interop.** Use function pointers or `LibraryImport`, not runtime marshalling. The core and
   Windows layers must stay Native-AOT safe for the CLI.
4. **Layouts.** Pin every native struct size, and the offsets of address fields, in
   `InteropLayoutTests`, with the C header as the source.
5. **Errors.** Map driver failures to `DdcException` with a `DdcErrorKind` and a one-sentence
   hint (for example `DdcHints.DdcCiDisabled`).
6. **One monitor.** Address exactly that monitor: the PoC's "send to every output" approach is
   not allowed.
7. **Registration.** Register the transport in `WindowsPlatform.Create`. If it is untested on real
   hardware, keep it opt-in like IGCL (`MONITORSWITCHER_ENABLE_IGCL`) until someone confirms it works.
