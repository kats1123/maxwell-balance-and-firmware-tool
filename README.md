# Audeze Maxwell Firmware Tool

A self-contained community tool to flash/downgrade Audeze Maxwell headset firmware. Works even when Audeze's official rollback feature is disabled.

**Why?** Firmware v1.0.1.74 introduced an L/R audio balance issue for some Maxwell users. Audeze disabled the rollback feature in their official app, leaving affected users stuck. Audeze has also stopped releasing updates for the original Maxwell since Maxwell V2 launched. This tool lets you flash any firmware version directly.

## What's Included

- **GUI Flasher** (`MaxwellFlasherGUI.exe`) — simple Windows app: pick platform, target, version, click Flash
- **Firmware files** — v1.0.1.61, v1.0.1.63, v1.0.1.74 for both Xbox and PlayStation (dongle + headset variants)
- **Airoha SDK** — bundled `AirohaHidCoreLib.dll` so the tool works without any Audeze software installed
- **Source code** (`src/`) — C# / .NET 8, MIT-licensed, build it yourself if you prefer

## Requirements

- Windows 10/11 (64-bit)
- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) — if you don't already have it, the EXE will tell you on launch

## Usage

Double-click **`MaxwellFlasherGUI.exe`**.

1. Select **Xbox** or **PlayStation**
2. Select **Dongle** or **Headset**
3. Select firmware **version**
4. Click **Flash Firmware**

### Dongle update:
- Plug the USB dongle into your PC (set the switch to PC mode if it has one)
- Turn ON the headset (must be wirelessly paired and connected to the dongle)
- Close any Audeze apps that might be open

### Headset update:
- Connect the headset to your PC directly with a **USB-C cable**
- Close any Audeze apps that might be open

**Always flash the dongle first, then the headset. Both should be on the same firmware version for normal operation.**

## Build from source

Requires .NET 8 SDK.

```
cd src
dotnet publish -c Release -r win-x64 --self-contained false
```

The output will be in `src/bin/Release/net8.0-windows/win-x64/publish/`. Copy that next to the bundled DLLs (`AirohaHidCoreLib.dll`, etc.) and run.

## Bundled Firmware

| Version | Notes |
|---------|-------|
| v1.0.1.74 | Latest (released ~2 years ago). Has L/R balance issue for some users |
| v1.0.1.63 | Recommended stable version — what most rollback users target |
| v1.0.1.61 | Older stable version |

All files include Xbox dongle, Xbox headset, PS dongle, and PS headset variants.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "Could not connect to device" | For dongle: headset must be powered on and wirelessly paired. For headset: connect via USB-C cable directly to PC. Close any Audeze app. |
| Stuck on "Transferring..." | Normal — dongle takes 2–3 minutes, headset takes 3–4 minutes. Be patient, do not unplug. |
| Status `FAIL/TIMEOUT` | Unplug and replug, then try again. If it keeps failing, see "Stuck FOTA" below. |
| Stuck FOTA (won't flash anything) | Unplug USB-C, turn the headset off, wait 10 seconds, turn it back on, wait 20 seconds, then plug USB-C back in and retry. |
| Device shows as generic "USB Device" | Device Manager → right-click the device → Update driver → "Let me pick" → choose **USB Composite Device**. |

## How It Works

Uses the Airoha RACE protocol via `AirohaHidCoreLib.dll` (extracted from the Audeze app installation) to communicate with the Maxwell's internal Airoha AB1568 Bluetooth chip. The SDK handles all USB HID communication, packet fragmentation, and the FOTA (Firmware Over The Air) transfer protocol.

The Maxwell uses dual-bank firmware — a successful flash writes to the inactive bank and the device swaps banks on reboot, so a power loss mid-flash should leave you on the previous firmware rather than bricked. (Don't take this as a guarantee.)

## Credits

- RACE protocol research informed by [auracast-research/race-toolkit](https://github.com/auracast-research/race-toolkit) and [ramikg/airoha-firmware-parser](https://github.com/ramikg/airoha-firmware-parser)
- SDK function signatures reverse-engineered from the Audeze App binary
- Built for the Audeze Maxwell community

## Disclaimer

Use at your own risk. Firmware flashing can potentially brick your device if interrupted or if the firmware files are corrupted. Ensure your headset is sufficiently charged (>50%) before flashing. This project is not affiliated with, endorsed by, or sponsored by Audeze LLC. "Audeze" and "Maxwell" are trademarks of their respective owners.
