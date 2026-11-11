# NotchWin

A macOS-style "Dynamic Island" notch for Windows 11. It sits at the top-centre of your primary screen as a small glassy pill and grows into a compact panel when you hover it. It follows your Windows light/dark mode, accent colour and transparency setting.

Built with C# / .NET 8 / WPF. Personal tool, Windows 11 only.

## Install on Windows 11

### 1. Download

**From Releases (recommended):** open the repository's **Releases** page and download `NotchWin.exe` from the latest release.

**Or the latest dev build:**

1. Sign in to GitHub and open this repository's **Actions** tab.
2. Click the newest successful **build** run.
3. Under **Artifacts**, download **NotchWin-win-x64** (a `.zip`).
4. Right-click the zip → **Extract All…**. Inside is a single `NotchWin.exe` (self-contained; no .NET install needed).

> **Chrome says "dangerous download"?** Unsigned exes that nobody has downloaded before get blocked. Either open Chrome's Downloads (`Ctrl+J`) → **Keep**, or build from source (below). Releases are code-signed once signing is configured; see [docs/SIGNING.md](docs/SIGNING.md).

### 2. Put it somewhere permanent

Move `NotchWin.exe` to a folder only you can write to, for example:

```
%LocalAppData%\Programs\NotchWin\NotchWin.exe
```

(Paste `%LocalAppData%\Programs` into the File Explorer address bar, create a `NotchWin` folder and move the exe there.) Avoid running it from `Downloads`, because *Start with Windows* points at wherever the exe lives.

### 3. First launch

1. Double-click `NotchWin.exe`.
2. If the exe isn't code-signed, Windows SmartScreen may say *"Windows protected your PC"*. Click **More info → Run anyway**. You only need to do this once.
3. The notch appears at the top-centre of your main screen. Hover it to expand.

### 4. Start automatically (optional)

Right-click the notch → **Start with Windows**. This adds a per-user entry (no admin rights) that you can also toggle in **Settings → Apps → Startup**.

### 5. For the glass look

Turn on **Settings → Personalization → Colors → Transparency effects**. With it off, the notch uses a solid colour instead.

### Update

Right-click the notch → **Exit NotchWin**, replace `NotchWin.exe` with the new one, and start it again. Your settings and history are kept.

### Uninstall

1. Right-click the notch → untick **Start with Windows**, then **Exit NotchWin**.
2. Delete the `NotchWin` folder you put the exe in.
3. Optional: delete your data, `%AppData%\NotchWin` (clipboard history) and `Pictures\Screenshots\NotchWin` (screenshots).

### Build from source instead

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
git clone https://github.com/latishkumeriya/Windows-Notch.git
cd Windows-Notch
dotnet publish src/NotchWin -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
.\publish\NotchWin.exe
```

## Features

**Media**: works with anything that shows up in the Windows media flyout (Spotify, YouTube/any browser, Apple Music, VLC…)
- Album art, title, artist, seek bar, previous / play-pause / next
- While playing, the collapsed notch shows the cover and animated bars tinted with the album colour
- Click the source pill to switch between apps that are playing

**Clipboard history**
- Records text, links, images and files (up to 60 + unlimited pinned), saved across restarts
- Click an entry to **paste it straight into the app you were using**; Shift+Click to only copy
- Search, pin, delete, clear; links get an "open" button
- Right-click the notch → **Pause clipboard history** to stop recording temporarily

**Screenshots**
- `Ctrl+Alt+S` region (drag, or click for whole screen), `Ctrl+Alt+F` full screen of the monitor under the cursor
- Saved to `Pictures\Screenshots\NotchWin` and copied to the clipboard (as image *and* file)
- Shelf of recent shots: drag a thumbnail into any app, click to open, copy, edit, delete

**Behaviour**
- Never steals focus when you hover or click it; hides automatically in fullscreen games/videos/presentations
- Hidden from the taskbar and Alt+Tab; right-click the notch for settings and *Exit*

## Shortcuts

| Keys | Action |
|---|---|
| `Ctrl+Alt+S` | Region screenshot |
| `Ctrl+Alt+F` | Full-screen screenshot |
| `Ctrl+Alt+V` | Open clipboard and search (Enter pastes the top match) |
| `Ctrl+Alt+N` | Toggle the notch |
| `Esc` | Close the notch |

## Privacy & security

- **Encrypted history**: clipboard history and clipped images in `%AppData%\NotchWin` are encrypted with Windows DPAPI, so only your Windows account can read them. Nothing is stored in plain text.
- **Passwords are skipped**: content marked private by password managers (1Password, Bitwarden, KeePassXC, etc., via the standard `CanIncludeInClipboardHistory` / `ExcludeClipboardContentFromMonitorProcessing` formats) is never recorded.
- **Offline**: the app makes no network connections and has no telemetry.
- **Safe links**: the "open link" button only opens `http`/`https` URLs, never file paths or other protocols.
- **No admin rights** needed; only writes to your own user profile and the per-user startup key.
- **Hardened native calls**: Windows DLLs are only loaded from `System32`.
- Very large clipboard items (text over 256 K characters, images over 40 MP) aren't kept in history.
- Errors go to `%AppData%\NotchWin\error.log`, capped at 512 KB, with no clipboard contents.

## Layout

```
src/NotchWin/
  MainWindow.xaml(.cs)        the notch: animations, tabs, hotkeys, focus handling
  Views/RegionSelectWindow    drag-to-select screenshot overlay
  Services/MediaService       Windows global media session (GSMTC)
  Services/ClipboardService   WM_CLIPBOARDUPDATE listener + history
  Services/SecureStore        DPAPI encryption + atomic writes
  Services/ScreenshotService  GDI capture, save, clipboard
  Services/ThemeService       light/dark, accent colour, transparency
  Native/NativeMethods        Win32 interop
```

## Ideas for next

File shelf (drop any file on the notch), volume/brightness/battery live activities, timer, calendar, notifications, quick toggles, settings window.
