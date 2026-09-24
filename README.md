# Deskspan - One to Multi PC Keyboard Mouse Control

Deskspan shares one keyboard and mouse between two Windows PCs. Hold Ctrl and click the mouse wheel to move control to the other PC. Do it again to come back.

The link is encrypted after pairing. Mouse movement, clicks, keys, and clipboard text travel on that link. Pointer moves and key presses also go as fast UDP packets when a direct path exists, and the receiver puts every key and click back in order and drops duplicates.

## What the free version includes

- Two Windows PCs
- Wi-Fi, Ethernet, or the internet
- Keyboard, mouse buttons, wheel, and pointer
- The `Ctrl + Mouse scroll click` shortcut
- Shared clipboard text
- A 9-digit pairing code that works once, checked with SPAKE2 so nobody in the middle can swap keys

More than two PCs, custom shortcuts, and admin controls are part of Deskspan Pro, which is not in this repository. See [deskspan.bdebtech.in](https://deskspan.bdebtech.in).

## Use it

1. Install and open Deskspan on both PCs. It stays in the tray.
2. On the same Wi-Fi, the other PC shows up under **On this Wi-Fi**. Tap it, then choose **Allow** on that PC.
3. Over the internet, choose **Create a code** on one PC, type that code on the other PC, and choose **Connect**.
4. Wait until the status says connected.
5. Hold Ctrl and click the mouse wheel to control the other PC. Do it again to return. The controlled PC can use the same shortcut to take itself back.
6. Choose **Only mouse**, **Only keyboard**, or **Keyboard + mouse**.

A small notice appears for about two seconds when control moves, then fades out.

Clipboard text copies across while the PCs are connected. It is text only, capped around 60 KB.

Windows asks the first time whether Deskspan may listen on private networks. Allow it.

## Internet connections

Pairing over the internet uses the Deskspan server at `deskspan.bdebtech.in` to find the other PC. The two PCs then try a direct UDP path. If that fails, packets go through the server. The server only sees encrypted packets. The current path is written to `%AppData%\Deskspan\network.log`.

## Limits

- Windows 10 or 11, 64-bit.
- The Windows sign-in screen, UAC prompts, and some games do not accept this input.
- Both PCs need the app running.
- Some Wi-Fi networks block device-to-device discovery. Use a pairing code there.

## Install

Download `Deskspan.exe` from the latest [release](https://github.com/bdeb-technology/Deskspan/releases) and run it. Windows may say the publisher is unrecognized. Choose More info, then Run anyway.

## Build

Requires the .NET 8 SDK.

```bash
dotnet test
dotnet run --project src/Deskspan.App
```

## Privacy

Keystrokes, mouse movement, and clipboard text stay between the two paired PCs on the encrypted link. The app does not log them. The pairing secret stays in your Windows user profile.

## License

MIT. Copyright (c) 2026 bdeb-technology.
