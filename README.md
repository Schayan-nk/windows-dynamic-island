# Island

A Dynamic Island for Windows 10 and 11, written in C# with WPF on .NET 9. It sits at the top of the screen as a
small pill, opens on hover (or Ctrl+Alt+T) and gives quick access to:

- **Claude Code**: live status of running sessions (current step and elapsed time), and "Ask Claude" to start a
  prompt in a chosen folder
- **Music**: now playing and media controls for any player that reports to Windows (Spotify, browsers)
- **Focus timer** with presets
- **File tray**: drop files on the island to park them; send files from a phone on the same network (QR code,
  one-hour link)
- **Clipboard history** (kept in memory only) and **quick notes**

![Home view](docs/home.webp)

| Music | Focus | Drop a file |
| --- | --- | --- |
| ![Music](docs/music.webp) | ![Focus](docs/focus.webp) | ![Drop](docs/drop.webp) |

## Build

Requires the .NET 9 SDK on Windows.

```powershell
dotnet publish src/Island.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Run

- `Island.exe`: Ctrl+Alt+T toggles the island; quit from the tray icon. Settings and notes live in `%APPDATA%\Island`.
- `Island.exe --shots <folder>`: renders every state to PNG, off screen.
- `Island.exe --selftest <log>`: runs the built-in checks; the exit code is the number of failures.

The phone transfer listens on the computer's local network address; on first start Windows asks to allow it through
the firewall (allow it for private networks).

Animations run at 50 fps and the idle loops at 12 fps, with software rendering, to keep CPU and memory low
(about 65 MB).
