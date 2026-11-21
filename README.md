<p align="center">
  <img src="assets/banner.svg" alt="Wintainium" width="820">
</p>

<p align="center">
  <a href="https://github.com/Ay1man2/Wintainium/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Ay1man2/Wintainium?label=release&color=cba6f7&cacheSeconds=3600"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-89b4fa">
  <img alt="Windows PowerShell 5.1 or PowerShell 7" src="https://img.shields.io/badge/PowerShell-5.1%20%7C%207-89b4fa">
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-a6e3a1"></a>
  <a href="#how-it-was-built"><img alt="Built with Claude" src="https://img.shields.io/badge/built%20with-Claude-d97757"></a>
</p>

<p align="center">
  <img src="assets/screenshot.png" alt="The Wintainium window listing tracked emulators and tools with their update status" width="820">
</p>

> **Shared as-is.** Wintainium is a personal project I'm sharing for others to clone, fork and tinker with. I'm not maintaining it for others: issues are open, but they're my own backlog for developing it with Claude, and I'm not taking feature requests or pull requests; see [Contributing](#contributing). It downloads and runs programs, so please review the code and use it at your own discretion; see [Before you run it](#before-you-run-it).

**Wintainium** keeps the Windows apps you install by hand up to date: emulators, PC ports, mods and tools that live on release pages instead of in a store or package manager. It is inspired by [Obtainium](https://github.com/ImranR98/Obtainium) on Android, but is an independent project with no affiliation to Obtainium, EmuDeck or Microsoft.

It is one PowerShell script with a dark, sortable window (or a console menu if you prefer). It reads each project's release source, compares it with the copy you have, and when something is outdated it starts the right kind of update for that app.

## Features

- **Many release sources:** GitHub (stable, pre-release or rolling tags), GitLab, Gitea/Forgejo (Codeberg and self-hosted), itch.io game pages, Dolphin's update feed, and project download servers through a URL template.
- **The right update method per app:**

  | Method | What happens |
  |---|---|
  | `emudeck` | Runs EmuDeck's own installer function for that emulator (install step only, so your settings stay) |
  | `inapp` | Starts the app (or its launcher) so its built-in updater can do the job |
  | `installer` | Downloads the new installer and asks before running it |
  | `zip` | Downloads the archive, backs up the app folder, then unpacks over it |
  | `download` | Downloads the file and shows it in Explorer |
  | `page` | Opens the release page |

- **Safe downloads:** files are checked against the SHA-256 a release publishes (`file.sha256`, `SHA256SUMS`, `checksums.txt`), and a mismatch deletes the download. Downloads keep Windows' "downloaded from the internet" mark, so SmartScreen still checks them.
- **Honest about antivirus:** if Windows Security removes a download, Wintainium shows the detection name and the published hash, and offers a VirusTotal lookup and Microsoft's false-positive form. It never restores files, allows threats or adds exclusions on its own.
- **Window:** search, an "only show updates" filter, release notes ("What's new"), Launch, Open folder, a right-click menu, double-click to update, and F5 to refresh.
- **Add apps from a URL:** paste a GitHub, GitLab, Forgejo or itch.io link. Wintainium can track a copy you already have, or install a fresh one.
- **Mods and data packs too:** a release with no program inside (a widescreen patch, a texture pack) is tracked by the release you installed, and flagged when a newer one is published.
- **Easy start:** download one file, `Wintainium.exe`, and double-click it. It installs itself for your user (no admin rights), and a short setup wizard asks where new apps should go, walks you through an optional no-permission GitHub token, finds the apps you already have, and adds shortcuts.
- **Scan your PC:** the **Scan** button finds emulators and tools Wintainium recognizes at their usual paths, plus programs whose files came from GitHub, GitLab, Codeberg or itch.io, and adds the ones you tick in one go. Read-only until you click Add.
- **Light on GitHub's rate limit:** lookups are cached for an hour, and an optional token is stored encrypted with Windows DPAPI. If a site is briefly down, the last known version is shown instead of an error.
- **`Find-Sources.ps1`:** a read-only scan that lists everything on your PC that came from a code-hosting site (downloads, git clones, untracked programs, mods), so nothing gets missed.

## Contents

[Features](#features) - [Before you run it](#before-you-run-it) - [How it was built](#how-it-was-built) - [Install](#install) - [Use](#use) - [Your app list](#your-app-list) - [GitHub token](#github-token-optional) - [Find what you have](#find-what-you-have) - [Notes and limits](#notes-and-limits) - [Tests](#tests) - [Contributing](#contributing)

## Before you run it

Wintainium is an **unsigned PowerShell script that downloads and runs other programs**. That deserves caution, so use it at your own discretion, and please read the code before you decide to use it. It's two scripts you can open in any text editor.

What it does to keep that reasonable:

- **Readable:** all the logic is in two PowerShell scripts, with no obfuscation and no telemetry. It only talks to the release sources you add, plus GitHub's API. The only compiled file is `Wintainium.exe`, a short launcher that carries a copy of the scripts, installs them, and starts `Wintainium.ps1`; its source is in [`launcher/`](launcher/WintainiumLauncher.cs), and you can skip it and run the script directly.
- **Verified downloads:** where a release publishes a SHA-256 checksum, the download is checked against it and deleted on a mismatch.
- **SmartScreen stays in the loop:** downloads keep Windows' "downloaded from the internet" mark, so Windows still checks them before they run.
- **Hands off your security settings:** it never restores quarantined files, allows threats, or adds Defender exclusions. When Windows Security removes something, it explains why and leaves the decision to you.
- **Asks first:** installers, zip installs and EmuDeck updates each ask before changing anything, and zip installs back up the app folder first.

A safe way to start is `.\Wintainium.ps1 list`, which only reads and prints the status table.

`Wintainium.exe` isn't code-signed, so the first time you open it Windows SmartScreen may say "Windows protected your PC". Click **More info** and then **Run anyway** if you're comfortable, or run the script yourself instead. Because it writes its scripts to disk and starts PowerShell, an antivirus may also be wary of it; the portable zip and running the script directly are there if you'd rather avoid the exe. Each release lists the SHA-256 of every file.

## Install

Windows 10 or 11. Everything it needs (Windows PowerShell 5.1 and .NET Framework 4) is built in.

**Easiest: one file**

1. Download **[`Wintainium.exe`](https://github.com/Ay1man2/Wintainium/releases/latest/download/Wintainium.exe)** (from the [latest release](https://github.com/Ay1man2/Wintainium/releases/latest), under Assets).
2. Double-click it. If SmartScreen appears, see [Before you run it](#before-you-run-it).
3. It installs itself to `%LOCALAPPDATA%\Programs\Wintainium` for your user only (no admin rights needed) and opens. You can delete the file you downloaded.
4. The setup wizard asks where new apps should go, offers the optional [GitHub token](#github-token-optional), scans for apps you already have, and adds Desktop and Start menu shortcuts. Pin Wintainium by right-clicking its taskbar button while it's open. Reopen the wizard any time with **Setup**.

Your app list, settings and token are saved in the install folder. **Updating:** Wintainium tracks itself, so when a new version is out, update it like any other app (or just run a newer `Wintainium.exe`); your list is kept. **Uninstalling:** Settings > Apps > Installed apps > Wintainium > Uninstall. It asks whether to keep your list for later, and never touches the apps it tracks.

**Portable:** download **`Wintainium-<version>-portable.zip`** instead, unzip it to a folder you'll keep, and double-click `Wintainium.exe` there. It runs from that folder and installs nothing; your list and settings are saved next to it. (It only runs the scripts in that folder while they're exactly as they came in the zip. If you edit them, run `Wintainium.ps1` directly or work in a git clone.)

**From the source:**

```powershell
git clone https://github.com/Ay1man2/Wintainium.git C:\code\Wintainium
cd C:\code\Wintainium
Unblock-File .\*.ps1
.\Wintainium.ps1 setup       # opens the window with the setup wizard
```

## Use

```powershell
.\Wintainium.ps1 gui      # the window (what Wintainium.exe opens)
.\Wintainium.ps1 setup    # the window, starting with the setup wizard
.\Wintainium.ps1          # console menu
.\Wintainium.ps1 list     # print the table and exit
.\Wintainium.ps1 token    # save a GitHub token (see below)
```

Console commands: a number updates that app, `a` updates everything outdated, `w 5` shows what's new, `l 5` launches, `o 5` opens the release page, `n` adds an app, `x 5` removes it from the list, `r` refreshes, `rf` re-checks while ignoring the cache, and `q` quits.

## Your app list

Apps live in `wintainium-apps.json` next to the script. It is created empty on first run and is git-ignored. Add apps from the window, or copy entries from [`wintainium-apps.example.json`](wintainium-apps.example.json). Paths can use environment variables such as `%APPDATA%`.

| Field | Meaning |
|---|---|
| `name` | Display name |
| `exe` | The installed program. Its version and date are compared with the latest release. For a mod, one of its files (used to spot a missing install) |
| `source` | `github`, `gitea` (also Forgejo/Codeberg), `gitlab`, `itch`, `dolphin` or `manual` |
| `repo` | `owner/name` for GitHub |
| `api` | Release API URL for `gitea`, `gitlab` and `dolphin` |
| `url` | Page for `itch` and `manual` |
| `prerelease` | `true` to track nightlies and betas (GitHub) |
| `tag` | A rolling tag such as `latest` or `continuous`. The newest asset date is used |
| `graceDays` | How much newer a release must be before it counts as an update (default 3; raise it for nightlies) |
| `update` | Update methods, in order of preference (see the table above) |
| `asset` | Regex that picks the right download from a release |
| `downloadUrl` | Download URL template with `{version}`, for projects that host files elsewhere |
| `emudeckFn` | EmuDeck install function, such as `PCSX2QT_install` |
| `launch` | Program or script to start for `inapp` updates, if not the exe |
| `hint` | Shown when using `inapp` or after a `zip` update, for example "Help > Check for Updates" |
| `installedTag` | For mods and data packs: the release tag that is installed. Status compares it with the latest tag instead of reading the exe, and `zip` updates record the new one |
| `installDir` | For mods and data packs: the folder `zip` updates unpack into, and that Launch and Open folder use |

## GitHub token (optional)

Most apps are checked on GitHub, which allows 60 lookups an hour without a token, so a list of 30 apps can run out after two refreshes. A free token raises that to 5,000.

Wintainium asks for a **classic token with no scopes** (no boxes ticked). A token like that can't read your private repositories or change anything on your account; it only tells GitHub who is asking. The setup wizard (**Setup** in the window) opens [GitHub's token page](https://github.com/settings/tokens/new?description=Wintainium%20(read-only%2C%20no%20scopes)&scopes=) with nothing ticked, checks the token you paste, refuses one that has permissions, and reads its real expiry date from GitHub. From the console, the same thing is:

```powershell
.\Wintainium.ps1 token
```

The token is saved to `wintainium-token.xml`, encrypted with Windows DPAPI so only your Windows account on this PC can read it. Wintainium reminds you a week before it expires.

## Find what you have

Click **Scan** (or use step 4 of the setup wizard). Wintainium looks for:

- **Recognized apps** at their usual install paths, such as EmuDeck's emulators, ES-DE, Steam ROM Manager and Playnite. These come with the right release source and update method already filled in; the list is [`known-apps.json`](known-apps.json).
- **Programs whose files came from a code-hosting site or an itch.io page**, using Windows' "downloaded from" mark on the files (or on the zip they were unpacked from).
- **Downloads** from those sites where the installed program wasn't found. These have an amber edge: click **Locate .exe** (or tick the box) to pick the installed program, or click **Don't show again** to leave it out of future scans. **Show hidden** brings those back. Files that can't be a Windows program (Android `.apk`, Linux and macOS builds) are skipped.
- **Wintainium itself**, so you hear when a new Wintainium release is out. Its row is shown in gold; right-click it to turn that off.

It only reads files. You tick what to add, can rename entries, and **Scan another folder** covers places like `D:\Games`. Mods and texture packs it finds are mentioned but not added; use **Add app** with the mod's link for those.

For a full report, including cloned git repositories and mods, run the scanner on its own:

```powershell
.\Find-Sources.ps1 -ExtraRoots 'D:\Games', 'C:\MyEmulators'
```

It writes `sources-inventory.csv` to your Desktop and never changes anything.

## Notes and limits

- Wintainium updates programs. It never downloads games, BIOS files, firmware or keys.
- itch.io has no public release API, so the version is read from the game's page and updates open that page. itch.io sometimes refuses automated page requests for a few minutes.
- **site busy** means a release site (anything other than GitHub) didn't answer; try Refresh later. If Wintainium checked that app before, it shows the last known version marked **(cached)** instead, with a note saying when it was fetched. **token rejected** and **rate limited** only ever refer to GitHub.
- The `emudeck` method calls EmuDeck's own scripts from `%APPDATA%\EmuDeck\backend`. Where EmuDeck pins an old build (ScummVM at the time of writing), use `zip` with the project's own releases.
- `zip` updates replace the files a release contains. Your settings normally live outside those files, and the folder is backed up first.
- To track a mod, add its repo and either let Wintainium install it, or give the mod's folder when asked for the .exe. If the mod is loaded from another program's folder (an emulator's mod folder, say), set `installDir` to that folder so updates land where they are used.

## Tests

```powershell
.\tests\Run-Tests.ps1
```

It runs every suite under each PowerShell installed (Windows PowerShell 5.1 and PowerShell 7 on Windows). The suites mock the release servers and Windows-only parts, so they also run under PowerShell 7 on Linux and macOS. They cover status detection, every release source, the cache and its fallback when a site is down, error messages, GitHub token checks, the settings file, scan results and recognized apps, downloads, checksums, quarantine handling, add-app, remove, starting the script the way the desktop shortcut does, and the one-file `Wintainium.exe` (install, update, no downgrade, leaving zip folders and clones alone, ignoring a `Wintainium.ps1` dropped next to it; compiled with Windows' own C# compiler, or Mono's elsewhere). The WPF window, the setup wizard, the scan window, the Settings > Apps entry and uninstall are not covered.

To build the release downloads (runs the tests, compiles `Wintainium.exe` with the scripts inside using the C# compiler built into Windows, and writes `dist\Wintainium.exe`, `dist\Wintainium-<version>-portable.zip` and a `.sha256` for each):

```powershell
.\build\Build-Release.ps1
```

## How it was built

Wintainium was built in one long working session with Claude. I described what I wanted, ran every version on my own PC and reported back what happened. Claude wrote most of the code, the test suites, the logo and these docs, and fixed what my test runs turned up, including a Windows PowerShell 5.1 bug the tests now cover.

Everything here was tested on my machine (Windows 11, about 28 tracked apps) before release. Still, treat it as a personal tool rather than polished software, and read the script before you run it, as you would with any script that downloads programs.

## Contributing

Wintainium is a personal project, shared as-is for anyone who finds it useful. **You're very welcome to fork it** and make it your own: add sources, change the look, rework it for your setup.

I'm not maintaining it for others. Issues are turned on, but they're my own backlog for developing Wintainium with Claude, not a support channel: I'm not taking feature requests or pull requests from others at the moment. If you build something on top of it, a fork is the place for it. More in [CONTRIBUTING.md](CONTRIBUTING.md).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

[GPL-3.0](LICENSE). If you share a modified version, keep it under the GPL and keep the license notice.
