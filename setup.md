# OCTAVE - Installation & Setup Guide

Welcome to **OCTAVE**, a high-performance, high-fidelity music player for Windows built with WinUI 3, BASS Audio Engine, and SQLite.

This guide provides step-by-step instructions for installing and running OCTAVE on your Windows PC.

---

## 1. System Requirements

- **Operating System**: Windows 10 (version 1809 / build 17763 or later) or Windows 11 (all versions including 24H2 / build 26100+).
- **Architecture**: 64-bit (x64 / AMD64).
- **Prerequisites**: **None!** OCTAVE is self-contained. You do **not** need to install the .NET 10 runtime or the Windows App SDK separately. All required runtimes, decoders, and libraries are bundled in the package.

---

## 2. Installation Options

You can install OCTAVE using any of the following methods depending on your preference:

### Method 1: 1-Click Automated Setup (Recommended)

The automated setup installs OCTAVE to your user profile directory without requiring administrator/UAC permissions, adds Start Menu and Desktop shortcuts, and registers OCTAVE in Windows Settings.

1. Open the repository root folder or the `Octave_Package/` folder.
2. Double-click **`setup.cmd`** (or right-click and select **Run**).
   - *Alternatively, in PowerShell run*: `.\setup.ps1`
3. Setup will automatically:
   - Deploy OCTAVE to `%LOCALAPPDATA%\Programs\Octave`.
   - Create a **Start Menu** shortcut (`OCTAVE`).
   - Create a **Desktop** shortcut (`OCTAVE`).
   - Register OCTAVE in **Windows Settings > Apps > Installed apps** (with full uninstaller support).
   - Offer to launch OCTAVE immediately.

---

### Method 2: Portable / Standalone Mode (Zero Installation)

If you prefer a portable music player that doesn't install anything to system directories:

1. Locate the **`Octave_Package/`** folder.
2. Move or copy this folder wherever you want (e.g. `C:\Tools\Octave`, `D:\Apps\Octave`, or a USB flash drive).
3. Open the folder and double-click **`Octave.Desktop.exe`** to start playing music immediately!
4. *(Optional)* Right-click `Octave.Desktop.exe` and choose **Pin to Start** or **Pin to taskbar** for quick access.

---

### Method 3: Build & Package from Source (For Developers)

If you have the source code and want to build the distribution package yourself:

1. Ensure the **.NET 10 SDK** is installed (`dotnet --version` $\ge$ 10.0.100).
2. Open PowerShell in the repository root.
3. Run the following command:

```powershell
dotnet publish Octave.Desktop/Octave.Desktop.csproj -c Release -r win-x64 --self-contained true -o Octave_Package
```

4. Once published, your standalone package is ready in `Octave_Package/`. Run `setup.cmd` or launch `Octave_Package\Octave.Desktop.exe`.

---

## 3. What Gets Installed & Where Files Are Stored

| Item | Location | Purpose |
| :--- | :--- | :--- |
| **Application Binaries** | `%LOCALAPPDATA%\Programs\Octave\` | Core executable, BASS decoders, WinUI 3 runtime |
| **Start Menu Shortcut** | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\OCTAVE.lnk` | Windows Search & Start Menu launch icon |
| **Desktop Shortcut** | `%USERPROFILE%\Desktop\OCTAVE.lnk` | Desktop shortcut |
| **User Database** | `%LOCALAPPDATA%\Octave\octave.db` | SQLite track library, playlists, favorites, EQ settings |
| **Artwork Disk Cache** | `%LOCALAPPDATA%\Octave\ArtworkCache\` | SHA-256 cached album artwork images |
| **Application Logs** | `%LOCALAPPDATA%\Octave\logs\` | Crash and error diagnostic logs (if needed) |

---

## 4. Supported Audio Formats

OCTAVE natively bundles unmanaged BASS decoders supporting **23 audio formats**:

- **Lossless**: FLAC (`.flac`), Apple Lossless (`.alac`, `.m4a`), WAV (`.wav`), AIFF (`.aiff`, `.aif`), WavPack (`.wv`), Monkey's Audio (`.ape`), OptimFROG (`.ofr`), TTA (`.tta`).
- **High-Resolution DSD**: Direct Stream Digital (`.dsf`, `.dff`).
- **Lossy**: MP3 (`.mp3`), AAC (`.aac`, `.m4a`), Opus (`.opus`), Ogg Vorbis (`.ogg`), WMA (`.wma`), Musepack (`.mpc`), Speex (`.spx`), AC3 (`.ac3`), DTS (`.dts`), MP2 (`.mp2`), MP1 (`.mp1`).

---

## 5. How to Uninstall OCTAVE

You can cleanly remove OCTAVE at any time:

- **Via Windows Settings**: Open **Windows Settings** $\to$ **Apps** $\to$ **Installed apps** $\to$ Find **OCTAVE** $\to$ Click the three dots $\to$ **Uninstall**.
- **Via Uninstaller Script**: Open `%LOCALAPPDATA%\Programs\Octave\` and run **`uninstall.cmd`**.
- **Portable Mode**: If you used the portable folder, simply delete the `Octave_Package/` folder.
- *(Optional)* To completely delete your saved library database and artwork cache, remove the `%LOCALAPPDATA%\Octave` directory.

---

## 6. Frequently Asked Questions (FAQ)

### Q: Windows Defender SmartScreen showed "Windows protected your PC"?
**A**: Because OCTAVE is a newly compiled standalone application without an expensive commercial EV code-signing certificate, Windows SmartScreen may show an informational prompt on first run:
1. Click **More info**.
2. Click **Run anyway**.
OCTAVE will start normally and this prompt will not appear again.

### Q: Do I need administrator permissions to install OCTAVE?
**A**: **No.** OCTAVE installs into the per-user `%LOCALAPPDATA%\Programs\Octave` directory, meaning it never triggers a UAC elevation prompt and can be installed on PCs without administrator rights.

### Q: Can I run multiple copies or put OCTAVE on a USB drive?
**A**: **Yes.** OCTAVE is completely portable. Copy the `Octave_Package` folder to any drive and double-click `Octave.Desktop.exe`.

### Q: Where do I add my music folders?
**A**: Launch OCTAVE $\to$ Navigate to **Settings** (gear icon in the bottom-left sidebar) $\to$ **Music Library** $\to$ Click **Add Folder** to select your music directories. OCTAVE will index your tracks with real-time file-system watching.
