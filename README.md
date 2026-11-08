# Screen Switch

A small Windows app for switching screens and sound in one place.

## What it does

- **Displays:** switch between Screen 1, Screen 2, Extend and Duplicate. Every change asks you to Keep or Cancel, and goes back on its own after 15 seconds if you don't answer, so a black screen fixes itself.
- **Settings:** night light, colour profile, HDR, brightness, scale, resolution and orientation for each display.
- **Audio:** pick your speakers and microphone, and set volume, mute and balance.
- **Audio shortcuts:** mute your microphone, push to talk, or mute your sound from anywhere. You choose the keys, and a single key like F13 or a number pad key works too.
- **Bluetooth:** see your paired devices and whether they're connected. Headphones and speakers get a Connect and Disconnect button.
- **Reset:** save your current setup and go back to it later. Your original setup is saved the first time the app runs.
- **Profiles:** save a set of screens, speakers and microphone, and switch to all of them at once.
- **Keyboard shortcuts:** switch screens or profiles from anywhere. You choose the keys.
- **Tray menu:** right-click the icon by the clock to switch screens, profiles, speakers or Bluetooth headphones without opening the window.
- **Appearance:** light mode, dark mode or match Windows, with five colour themes: Ember, Ocean, Forest, Graphite and Arctic.

A setup menu helps you pick shortcuts the first time. Everything can be changed later.

## Install

1. Download `ScreenSwitch.exe` from the latest release.
2. Put it somewhere permanent, for example `C:\Program Files\ScreenSwitch`.
3. Run it.

Needs Windows 10 or 11. Nothing else to install.

### "Windows protected your PC"

The app isn't digitally signed, so Windows may warn you the first time. Click **More info**, then **Run anyway**.

Some antivirus programs flag new unsigned apps that start with Windows and listen for keyboard shortcuts. If yours removes it, add an exception for the folder you put it in.

## Bluetooth

The Connect button appears for headphones and speakers. Controllers, phones and headsets connect from their own end: turn them on and they'll connect. Pairing new devices opens Windows' own Bluetooth settings.

## Privacy

- Screen Switch never goes online.
- Your choices are kept on your PC, for your Windows account only.
- Nothing about you or your PC is built into the app. Device names are read from Windows while the app runs and are never sent anywhere.

## Build it yourself

Uses the C# compiler that comes with Windows, so no extra tools are needed.

```
src\build.bat
```

This writes `ScreenSwitch.exe` next to the `src` folder.

## Licence

MIT. See [LICENSE](LICENSE).

## Disclaimer

Screen Switch is provided "as is", without warranty of any kind. You use it at your own risk. The authors and contributors are not liable for any damage, data loss or other problem that comes from using it. See [LICENSE](LICENSE) for the full terms.
