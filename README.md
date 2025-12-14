this is an older version that still uses the old tcp/ip method of wireless connecting

pls consider donating to my kofi
https://ko-fi.com/snailboi

# Phone Control GUI (C# WPF)

this is primarily a GUI for **scrcpy** and **ADB**, with some fun features:

## Features

- **Phone screencast**  
  Minimal performance impact on the PC.

- **File syncing service**  
  Sync folders between phone and PC while preserving file dates.  
  *(PC → phone sync is still a bit buggy.)*

- **Simple app manager**  
  Allows APK installations.

- **Notification menu**

- **Customizable button colors**  
  Make your own style. You can also use a background, but it’s not recommended.

- **Current app display**  
  Shows which app is currently running on the connected device.

- **Setup window**  
  Configure multiple devices, name them, and select USB-only or USB + WiFi mode.  
  Automatically switches between USB and WiFi if available.

  - **Music presence**  
  Share the current media playing on your phone to Discord.

- **SMTC**  
  Makes phone media appear as if it’s playing on the PC.`*Needs music presence enabled*`

  Textbox under the current device reflects media playing instead of the active app `*Needs music presence enabled*`

- **Global hotkeys**

| Hotkey                   | Action |
|---------------------------|--------|
| Shift + Volume Up         | Increase phone volume |
| Shift + Volume Down       | Decrease phone volume |
| Shift + Next              | Next track on phone |
| Shift + Previous          | Previous track on phone |
| Shift + Pause             | Pause current media on phone |
| Insert                    | Auto-unlock phone if a PIN is provided and USB is connected |
| PageUp / PageDown / End   | Programmable to open any app |

## Developer Mode Features

*These can be enabled by checking the **DevMode** box in the setup window.*  
Features marked `*DEVONLY*` will only work in DevMode.

- **Expanded app manager**  
  - Send basic YouTube download commands to YTdnlis `*DEVONLY*`  
  - Send a mouse movement to wake up the PC from sleep `*DEVONLY*`  


## License

This software is provided under a **read-only, view-only license**:

- You may **view the source code** in this repository.  
- You may **run the compiled binaries** for personal, non-commercial purposes.  
- You may **not copy, modify, redistribute, or create derivative works** from the source code or binaries without explicit written permission.  
- You may not decompile or reverse-engineer the binaries beyond normal usage.
- See [LICENSE.txt](LICENSE.txt) for full license terms.
- Third-party components are licensed separately (see [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md)).
