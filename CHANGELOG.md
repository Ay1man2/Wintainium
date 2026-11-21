# Changelog

## v0.3.0 - Unreleased

### Added
- One-file download: `Wintainium.exe` carries the scripts, so you download that one file and double-click it. It installs itself to `%LOCALAPPDATA%\Programs\Wintainium` for the current user (no admin rights), adds a Settings > Apps entry to uninstall it, and starts. Running a newer `Wintainium.exe` updates the installed copy and keeps your list, settings and token; it never downgrades. An installed copy updates itself through Wintainium, using the new `Wintainium.exe` checked against its published SHA-256. Its source is in `launcher/`, and `build\Build-Release.ps1` builds it with the C# compiler that ships with Windows.
- Portable zip (`Wintainium-<version>-portable.zip`): the same files unpacked, running from any folder without installing anything.

### Security

## v0.2.0 - 2026-11-18

### Added
- `Wintainium.exe`: the release zip now has a launcher, so you can unzip and double-click instead of running PowerShell. Its source is in `launcher/` and `build\Build-Release.ps1` builds the zip with the C# compiler that ships with Windows.
- **Scan** button and setup step: finds apps you already have and adds the ones you tick in one go. It recognizes common emulators and tools at their usual paths (`known-apps.json`, with update methods filled in, including EmuDeck's installer when EmuDeck is present) and programs whose files came from GitHub, GitLab, Codeberg or itch.io. Downloads without a found install can be pointed at with Locate .exe. Scan another folder covers your own locations.
- Wintainium can track itself: Scan offers it as a ready-to-add entry, and its own row knows the running version, so a new Wintainium release shows as an update. The row's name is gold; right-click it and untick **Show Wintainium in gold** to turn that off.
- Scan: **Don't show again** on downloads you don't want to add (remembered in `wintainium-settings.json`), and **Show hidden** to bring them back. Downloads that can't be a Windows program (`.apk`, Linux and macOS builds) are skipped.
- Recognized apps added: PPSSPP, RetroArch, shadPS4 QtLauncher, Xenia Canary, Azahar, Cemu, Dolphin, PrimeHack, melonDS and mGBA.
- Setup wizard on first start, and a **Setup** button afterwards: where new apps and downloads go (saved in `wintainium-settings.json`), an optional GitHub token, and Desktop / Start menu shortcuts.
- Guided GitHub token: explains why it helps and that it needs no permissions, opens GitHub's token page with nothing ticked, refuses tokens that have permissions (and fine-grained tokens, whose permissions GitHub doesn't report), and reads the real expiry date from GitHub. `.\Wintainium.ps1 token` uses the same checks.
- Wintainium gets its own taskbar button and icon instead of being grouped with PowerShell. The shortcuts the wizard makes share that button, and Pin to taskbar on the running window pins Wintainium.
- `.\Wintainium.ps1 setup` opens the window with the wizard. `shortcut` points at `Wintainium.exe` when it's there.

### Fixed
- `Find-Sources.ps1` wrote its report to `%USERPROFILE%\Desktop`, which isn't the Desktop you see when OneDrive syncs it; it now uses the real Desktop. It also looks in the folder chosen in Setup, in Downloads for unpacked apps, and in EmuDeck's Emulation folder wherever it is, and recognizes itch.io pages.
- A failed itch.io (or any non-GitHub) lookup was reported as "token rejected" or "rate limited", with messages blaming the GitHub token. Those statuses now only apply to GitHub; other sites show **site busy**.
- When a release site is briefly unavailable, the last known version is shown, marked **(cached)**, instead of an error.
- itch.io pages are requested with browser-style headers (still naming Wintainium), so they're refused less often.
- An installed version behind the release in the same numbering scheme (PCSX2 2.9.179 vs 2.9.181) showed **up to date** until the grace period passed. The version numbers now decide; the release date is only used when they can't be compared.
- Scan: a download with no installed program found had a checkbox that did nothing. Ticking it now opens Locate .exe, the Locate button stands out, and the row says what to do.
- The window and its shortcuts no longer keep `assets\icon.ico` open, which stopped the folder from being replaced while Wintainium or a pinned shortcut was open. Shortcuts use the icon inside `Wintainium.exe`, or a copy in `%LOCALAPPDATA%\Wintainium`.

## v0.1.1 - 2026-11-16

### Added
- Mods and data packs: releases with no program inside can be added and tracked. Add app asks whether a download without an .exe is a mod, or takes a mod's folder in place of an .exe for something already installed.
- New app-list fields `installedTag` (the release that is installed) and `installDir` (where zip updates unpack). Apps with `installedTag` are compared by release tag, and each zip update records the new tag.

### Fixed
- Add app said "Nothing was unpacked" for downloads whose files sit in subfolders with no .exe.
- Launch on a mod opens its folder instead of trying to run a data file, and Open folder uses `installDir`.
- `hint` is also shown after a zip update.

## v0.1.0 - 2026-11-15

First public release.

### Added
- Window (WPF) and console menu showing every tracked app, with its installed and latest version and update status.
- Release sources: GitHub (stable, pre-release and rolling tags), GitLab, Gitea/Forgejo (Codeberg and self-hosted), itch.io game pages, Dolphin's update feed, and download URL templates for projects that host files elsewhere.
- Update methods per app: EmuDeck's own installer, the app's built-in updater, installer download, zip install with a folder backup, plain download, or the release page.
- SHA-256 verification against published checksums (`file.sha256`, `SHA256SUMS`, `checksums.txt`). A mismatch deletes the download.
- Downloads keep Windows' "downloaded from the internet" mark, so SmartScreen still checks them.
- Windows Security handling: when a download is removed, Wintainium shows the detection and published hash and offers a VirusTotal lookup and Microsoft's false-positive form. It never restores, allows or excludes anything itself.
- Add apps from a GitHub, GitLab, Forgejo or itch.io URL, either tracking an existing copy or installing a new one.
- Search, an updates-only filter, release notes, Launch, Open folder, a right-click menu, and remove from list.
- Release lookups cached for an hour. An optional GitHub token is stored encrypted with Windows DPAPI, with an expiry reminder.
- `Find-Sources.ps1`: a read-only scan for downloads, git clones, untracked programs and mods that came from code-hosting sites.
- Test suites covering the above, runnable on Windows PowerShell 5.1 and PowerShell 7.
