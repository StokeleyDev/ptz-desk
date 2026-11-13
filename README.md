# PTZ Desk

Windows operator console for PTZ cameras and room audio, with phone control over the local network.

[Русская версия](README-RU.md) · [Download latest release](https://github.com/StokeleyDev/ptz-desk/releases/latest)

![Desktop console](assets/desktop.png)

## Features

- USB (DirectShow/UVC) and ONVIF cameras in one bank; switch with one click or keys 1–9.
- Joystick, direction keys, zoom and speed; 8 presets per camera.
- Speaker and microphone levels for the PC and for paired rooms.
- Phone remote: pair by QR code, control cameras and audio from a browser. No app to install.
- Rooms hub: one PC can control the audio of several room PCs.

## Requirements

- Windows 10/11 x64.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). It ships with Windows 11. Without it the app opens a simpler built-in interface.

## Quick start

1. Download the installer or the portable `.zip` from [Releases](https://github.com/StokeleyDev/ptz-desk/releases/latest).
2. Portable: unpack to any folder. Settings are kept next to the exe in `Data\`.
3. Add a camera: **+** in the camera list → USB or ONVIF (address, login, password).
4. Phone: **Remote control** → enable → scan the QR code from a phone on the same network.

Try it without hardware: `AGL.PTZ.Controller.exe --demo`.

## Security

- The phone server is **off by default**. It listens on one private IPv4 adapter (RFC 1918, Private/Domain network profile) on port 8787.
- Public and CGNAT/Tailscale addresses (100.64.0.0/10) are rejected. There is no internet access by design.
- Pairing uses a one-time QR code with rate-limited attempts. Paired devices can be revoked one by one.
- Only one device steers at a time. The camera stops by itself ~300 ms after the controlling device goes silent or disconnects.
- Camera passwords are encrypted with Windows DPAPI (current user). After moving to another PC, enter them again.
- Passwords, IP addresses and camera IDs are never sent to phones.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Node.js 20+.

```powershell
cd ui
npm ci
node build.mjs
cd ..
dotnet build -c Release -r win-x64 --self-contained true
```

Release build (single-file exe and portable folder in `..\release\v<version>\`):

```powershell
.\build-release.ps1
```

To also build the installer and portable ZIP, install Inno Setup and run `./tools/Build-Distribution.ps1`.

The version is set in `AGL.PTZ.Controller.csproj`.

## Project layout

| Path | Contents |
|---|---|
| `ui/` | React interface (desktop `app.jsx`, phone `phone.jsx`), built by esbuild and embedded in the exe |
| `ReactInterface.cs` | WebView2 host and the bridge between UI and app |
| `CameraBank.cs`, `DirectShowCamera.cs`, `OnvifCamera.cs` | Camera profiles and drivers |
| `Remote*.cs`, `Hub*.cs` | Phone server, pairing, rooms hub |
| `NetworkInterface.cs`, `ConsoleTheme.cs` | Fallback WinForms interface (used without WebView2) |
| `tools/` | Test scripts (`Test-Portable.ps1`, `Test-RemoteSocket.ps1`, …) and the hardware checklist |

## License

See the [MIT License](LICENSE).
