# Changelog

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
