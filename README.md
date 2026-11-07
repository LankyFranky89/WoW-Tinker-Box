# WoW Tinker Box

A dark, Windows 11-style addon manager for **WoW Forever 1.60**. Runs on Windows and Linux (Avalonia / .NET 8).

## Features
- **Installed addons**: scans `_forever_\Interface\AddOns`, reads each `.toc`, and flags addons not marked for Forever (Interface 16001 or a `_Forever` / `_Camelot` .toc).
- **Install from GitHub or WoWInterface**: paste `owner/repo`, a GitHub link or a WoWInterface link, or search WoWInterface by name. Picks the Forever zip from the latest release when there is one, or the newest commit if the repo has no releases.
- **Updates**: checks all GitHub-installed addons at startup, with one-click update or "Update all".
- **Auto-backup WTF**: zips your WTF folder before every update. Keeps the newest 10, restore with two clicks (current settings are backed up first).
- **Zip your addons**: tick addons (or all) and save them as one zip. "Install from zip" installs those zips, or any addon zip you downloaded yourself. GitHub and WoWInterface addons keep updating after import.
- **Addon packs**: share your whole addon list as a `WTB1:` code. Your friend pastes it to install the same set.
- **Patch-day checker**: notices when the game build changes (from `.build.info`) and tells you which addons have fixes or may break.
- CurseForge and Wago keys can be saved in Settings; those sources are planned for a later version.

## Build
**On your PC:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then double-click `build-windows.bat`. The app ends up in `publish\WoWTinkerBox.exe`.

**On GitHub:** push this folder to a repo. The Actions workflow builds `WoWTinkerBox.exe` (and a Linux build). Download them from the run's Artifacts.

Settings and backups are stored in `%AppData%\WoWTinkerBox` (Linux: `~/.config/WoWTinkerBox`).
