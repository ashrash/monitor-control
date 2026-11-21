# Adding a monitor profile

A profile teaches Monitor Switcher which command and codes switch inputs on one monitor model.
Profiles are plain JSON, shared by the Windows and macOS apps.

## The quick way: the setup wizard

1. Open the tray menu (or the macOS menu bar menu) and choose **Set up a monitor…**.
2. Tell it which input this computer uses and which input to test.
3. The wizard tries codes until the screen switches, then saves a profile for you.
4. On the last page, click **Copy profile to share** and open a pull request (see below) so
   everyone gets it built in.

## By hand

1. Find your monitor's identity:

   ```
   monitorswitcher --list
   ```

   ```
   EDID:      GSM 0x774F "LG HDR 4K", serial 384592
   DDC model: UN880
   ```

2. Create `profiles/<vendor>/<vendor>-<model>.json`. The file name must equal the `id`, and the
   folder is the vendor in lowercase:

   ```json
   {
     "$schema": "../profile.schema.json",
     "id": "lg-32un880",
     "name": "LG UltraFine Ergo 32UN880",
     "match": {
       "manufacturer": "GSM",
       "productCodes": ["0x774F"],
       "capabilitiesModel": "UN880"
     },
     "protocol": "lg-alternative",
     "inputs": [
       { "id": "hdmi1", "name": "HDMI 1", "code": "0x90" },
       { "id": "hdmi2", "name": "HDMI 2", "code": "0x91" },
       { "id": "dp1", "name": "DisplayPort", "code": "0xD0" },
       { "id": "usbc", "name": "USB-C", "code": "0xD1", "verified": true }
     ],
     "quirks": { "ignoresCommandsWhenInactive": true },
     "notes": "What you learned about this model."
   }
   ```

3. Test it before sharing: copy the file into your user profiles folder
   (`%LOCALAPPDATA%\MonitorSwitcher\profiles` or `~/Library/Application Support/MonitorSwitcher/profiles`)
   and run `monitorswitcher --list` and `monitorswitcher --input <id>`.

## Fields

| Field | Notes |
|---|---|
| `match.manufacturer` | Three-letter EDID id: `GSM` LG, `DEL` Dell, `SAM` Samsung, `ACR` Acer, `AUS` ASUS, `BNQ` BenQ, `HWP` HP, `LEN` Lenovo. |
| `match.productCodes` | Most precise key. Several codes are fine when one model ships with different panels. |
| `match.capabilitiesModel` | Model token from the DDC/CI capabilities string. Useful for model families. |
| `match.modelNames` | Weakest key. Avoid generic names: many LG models are all called "LG HDR 4K". |
| `protocol` | `mccs-standard` (VCP 0x60) or `lg-alternative` (VCP 0xF4 via source 0x50). |
| `inputs[].id` | `hdmi1`, `hdmi2`, `dp1`, `dp2`, `usbc`, … Used by the CLI and settings. |
| `inputs[].code` | Hex string. For `mccs-standard`, common values are 0x11/0x12 for HDMI 1/2, 0x0F/0x10 for DisplayPort 1/2, and 0x1B for USB-C on many models. |
| `inputs[].verified` | `true` only when you saw that code switch the monitor. |
| `quirks.ignoresCommandsWhenInactive` | `true` if the monitor ignores commands while showing another input. |
| `quirks.retries`, `retryDelayMs`, `postSwitchDelayMs` | Only if the monitor needs them. |

A profile must name at least one of `productCodes`, `capabilitiesModel` or `modelNames`, so it
can't claim every monitor from a manufacturer. Unknown fields are rejected to catch typos.

## Submitting

1. Fork https://github.com/ashrash/monitor-control and add your file under `profiles/`.
2. Run the Windows tests (they validate every profile): `dotnet test windows/MonitorSwitcher.slnx`.
3. Open a pull request. Include the output of `monitorswitcher --list`, which ports you tested,
   and your GPU.
