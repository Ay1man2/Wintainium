# Parity checklist: PowerShell v0.3.0 -> C# v0.4.0

This is the contract for the v0.4.0 port. It lists everything the PowerShell app (v0.3.0) does that a
user can see or rely on. The C# app must do all of it the same way, except the items under
[Intentional changes](#intentional-changes) and [Dropped on purpose](#dropped-on-purpose).

How to use it:

- Every item has an ID. A port PR names the IDs it covers.
- An item is ticked only when its **proof** is filled in: the C# test that checks it, or "manual" plus
  the PR where it was checked by hand (UI looks, real installs).
- "Source" is where the behaviour lives in v0.3.0: a function in `Wintainium.ps1` unless another file
  is named (`FS` = `Find-Sources.ps1`, `L` = `launcher/WintainiumLauncher.cs`).
- Message texts (log lines, prompts, tooltips, labels) are ported word for word unless a line here
  says otherwise.
- The release that ships v0.4.0 can't be made while any box is unticked.

---

## 1. Data files and settings

- [x] **D-01** App list `wintainium-apps.json` next to the app; created as `[]` with a notice when missing. Source: startup block. Proof: `AppListTests.Missing_list_is_created_empty_with_a_notice`
- [x] **D-02** App list read as an array also when it holds one entry. Source: startup block. Proof: `AppListTests.One_entry_written_as_an_object_reads_as_a_list`
- [ ] **D-03** App fields understood: `name`, `exe`, `source` (`github`, `gitlab`, `gitea`, `itch`, `dolphin`, `manual`), `repo`, `tag`, `prerelease`, `api`, `url`, `asset`, `downloadUrl`, `update`, `emudeckFn`, `hint`, `launch`, `graceDays`, `installDir`, `installedTag`, `note`. Source: whole script. Proof:
- [ ] **D-04** Environment variables in `exe`, `launch`, `installDir` are expanded (`%APPDATA%` etc.). Source: `Get-AppRows`, `Start-App`, `Get-AppFolder`. Proof:
- [ ] **D-05** App list written after Add app, Remove, scan add, and installedTag updates; other fields of every entry kept. Source: `Add-App`, `Remove-AppEntry`, `Add-ScanSelection`, `Save-InstalledTag`. Proof:
- [x] **D-06** Settings `wintainium-settings.json`: `installRoot`, `downloadDir`, `setupDone`, `goldSelf`, `scanIgnore`; unreadable file treated as empty. Source: settings block, `Save-Settings`. Proof: `SettingsTests` (all)
- [ ] **D-07** Default folders: installs `%USERPROFILE%\Wintainium\Apps`, downloads `%USERPROFILE%\Downloads\Wintainium`; settings override defaults. Source: `param`, settings block. Proof:
- [x] **D-08** Release cache `wintainium-cache.json`, keyed by app name: `Tag`, `Date`, `Page`, `Assets` (name + url), `Notes` (cut at 20,000 chars), `Fetched`; unreadable file treated as empty. Source: `Get-LatestCached`, `Save-Cache`. Proof: `ReleaseCacheTests` (all)
- [x] **D-09** Legacy files `emu-apps.json`, `emu-cache.json`, `emu-token.xml` copied once to the new names when those don't exist. Source: startup block. Proof: `AppListTests.Legacy_files_are_copied_once`
- [x] **D-10** Files written by Windows PowerShell 5.1 (UTF-8 with BOM) and 7 (without) are both read. Source: n/a (compatibility). Proof: `AppListTests.Reads_utf8_with_and_without_bom`, `ReleaseCacheTests.Reads_what_PowerShell_wrote`

## 2. GitHub token

- [x] **T-01** Token stored in `wintainium-token.xml` (DPAPI, current user) with its expiry date; existing v0.3.0 files are read. Source: `Get-StoredToken`, `Save-GitHubToken`. Proof: `TokenTests.Token_file_round_trips_in_the_Clixml_format`, `Reads_a_file_written_by_Export_Clixml`, `Real_DPAPI_round_trip` and `Reads_a_token_saved_by_Windows_PowerShell` (Windows CI)
- [ ] **T-02** `GITHUB_TOKEN` environment variable used when set (see I-06 for the added check). Source: `Get-StoredToken`. Proof:
- [x] **T-03** Unreadable token file: warning that says how to save it again; app continues without a token. Source: `Get-StoredToken`. Proof: `TokenTests.Unreadable_token_warns_and_continues_without_one` (message now points to Setup, since the `token` command is dropped)
- [x] **T-04** Token check against `https://api.github.com/rate_limit`: accepted / rejected, limit, scopes from `X-OAuth-Scopes`, expiry from `github-authentication-token-expiration` (both formats: offset and UTC). Source: `Test-GitHubToken`, `ConvertFrom-GitHubExpiry`. Proof: `TokenTests` expiry and check tests
- [x] **T-05** Safe only when classic with no scopes; fine-grained (`github_pat_`) and scoped tokens get their own messages. Source: `Test-GitHubToken`. Proof: `TokenTests.Scoped_token_flagged`, `Fine_grained_not_safe`
- [x] **T-06** Token sent only to api.github.com (never to itch.io, GitLab, Gitea, Dolphin or download hosts). Source: `$ghHeaders` use. Proof: `ReleaseSourcesTests.GitHub_token_goes_only_to_api_github_com`, `Itch_request_never_carries_the_token`, `Gitea_Forgejo_source_resolves`, `TokenTests.Token_is_only_sent_to_the_GitHub_API`
- [ ] **T-07** Expiry warning when 7 days or fewer are left. Source: `Show-LookupNotes`. Proof:
- [ ] **T-08** Token page link opens GitHub's new-token page with the description filled and no scopes. Source: `$script:TokenPageUrl`. Proof:

## 3. Release sources and cache

- [x] **S-01** GitHub latest release (`/releases/latest`), date = published. Source: `Get-LatestRelease`. Proof: `ReleaseSourcesTests.GitHub_latest_release`
- [x] **S-02** GitHub fixed `tag` (rolling builds), date = newest asset update, else published. Source: `Get-LatestRelease`. Proof: `ReleaseSourcesTests.GitHub_rolling_tag_uses_the_newest_asset_date`
- [x] **S-03** GitHub `prerelease`: newest non-draft of the last 10 releases. Source: `Get-LatestRelease`. Proof: `ReleaseSourcesTests.GitHub_prerelease_takes_the_newest_non_draft`
- [x] **S-04** Gitea / Forgejo / Codeberg via `api`. Source: `Get-LatestRelease`. Proof: `ReleaseSourcesTests.Gitea_Forgejo_source_resolves`
- [x] **S-05** GitLab via `api`: tag, released date, page, asset links, description. Source: `Get-LatestRelease`. Proof: `ReleaseSourcesTests.GitLab_source_resolves`
- [x] **S-06** itch.io: game page read with browser-like headers (identifies Wintainium), 20 s timeout; picks the upload matching `asset`, else the Windows one, else the first; version or file name as tag; UTC date; error when no downloads listed. Source: `Read-ItchPage`, `$itchHeaders`. Proof: `ItchPageTests` (all), `ReleaseSourcesTests.Itch_request_*`
- [x] **S-07** Dolphin feed: `shortrev` tag, Windows x64 artifacts, changelog HTML turned into text, fixed download page. Source: `Get-LatestRelease`. Proof: `ReleaseSourcesTests.Dolphin_feed_resolves`
- [ ] **S-08** `manual` source: no lookup, status "check manually". Source: `Get-AppRows`. Proof:
- [x] **S-09** Lookup address must be https, else the lookup fails without a request. Source: `Get-LatestRelease`, `Test-HttpsUrl`. Proof: `ReleaseSourcesTests.Http_address_is_not_requested`, `ReleaseLookupTests.Http_address_is_lookup_failed`
- [x] **S-10** Cache answers reused for 60 minutes; Re-check all ignores the cache. Source: `Get-LatestCached`. Proof: `ReleaseLookupTests.Cache_is_reused_only_inside_the_window`, `Second_run_served_from_cache`
- [x] **S-11** A failed lookup with a cached answer falls back to it, marks Latest "(cached)" and records why and when. Source: `Get-LatestCached`. Proof: `ReleaseLookupTests.Itch_403_falls_back_to_the_saved_answer`
- [x] **S-12** Failure reasons: GitHub 401 / "Bad credentials" = token rejected; GitHub 403/429 / "rate limit" = rate limited; other sites 403/429/502/503/504 or timeouts/connection errors = site busy; anything else = lookup failed. Source: `Get-LookupStatus`. Proof: `ReleaseLookupTests` (status cases), `Unreachable_sites_are_busy_but_GitHub_is_lookup_failed`
- [x] **S-13** Site names in messages: GitHub, itch.io, GitLab, dolphin-emu.org, else the host of `api`/`url`. Source: `Get-SourceSite`. Proof: `ReleaseSourcesTests.Site_names`

## 4. Status engine

- [x] **E-01** Exe missing = "exe not found". Source: `Get-AppRows`. Proof: `StatusTests.Missing_exe_reported`, golden cases "Missing exe", "Manual missing"
- [x] **E-02** Installed = exe ProductVersion, else installedTag, else exe date (yyyy-MM-dd). Source: `Get-AppRows`. Proof: `StatusTests.Installed_is_the_exe_date`, golden (`StatusGoldenTests`)
- [x] **E-03** Mods and data packs (`installedTag`): same tag = up to date, else UPDATE. Source: `Get-AppRows`. Proof: golden cases "Mod ..." (`StatusGoldenTests`)
- [x] **E-04** Version numbers in the same scheme decide (2.9.179 < 2.9.181). Source: `Test-OlderVersion`. Proof: `VersionCompareTests.Older_by_number`, golden
- [x] **E-05** When versions can't be compared, the release date decides, with `graceDays` per app (default 3). Source: `Get-AppRows`. Proof: golden cases "Rolling tag ...", "Own grace days", "Grace days zero means default"; `StatusTests.Rolling_tag_uses_asset_date`
- [x] **E-06** Wintainium's own entry (repo `Ay1man2/Wintainium`) reports the app's own version, also from a clone. Source: `Get-AppRows`, `Test-SelfEntry`. Proof: `StatusTests.Self_reports_own_version`, golden cases "Wintainium"
- [x] **E-07** Row order: UPDATE, then problems (token rejected, rate limited, site busy, lookup failed), exe not found, check manually, up to date; then by name. Source: `Get-AppRows`. Proof: `StatusTests.Rows_in_order`, `StatusGoldenTests.Rows_come_in_the_PowerShell_order_apart_from_the_moved_statuses`
- [x] **E-08** Row fields: App, Status, Installed, Latest, Released (local yyyy-MM-dd), Update via (first method's label), page, notes. Method labels: EmuDeck, in-app, installer, zip install, download, release page. Default method `page`. Source: `Get-AppRows`, `$viaLabel`. Proof: `StatusTests.Via_labels`, `StatusTests.Released_is_local_date`, golden
- [x] **E-09** Lookup notes in the log: token rejected, hourly limit reached (with token hint), token expiring, http address explained, site busy per app, "(cached)" fallback per app with time. Source: `Show-LookupNotes`. Proof: `LookupNotesTests`

## 5. Updating an app

- [x] **U-01** Several methods: choose which (Enter = first, x = cancel). Source: `Update-App`. Proof: `InstallTests.Update_app_asks_which_method_when_there_are_several`
- [x] **U-02** Update all: each outdated app asks Yes / No / Stop, uses its first method, skips the confirmations inside. Source: `Update-All`. Proof: `InstallTests.Update_all_asks_each_outdated_app_and_stops_when_asked`
- [x] **U-03** "Nothing to update." when no app is outdated. Source: `Update-All`. Proof: `InstallTests.Update_all_asks_each_outdated_app_and_stops_when_asked`
- [ ] **U-04** Updating an app that isn't UPDATE asks "Run its update anyway?". Source: `Start-SelectedUpdate`. Proof:
- [x] **U-05** `emudeck`: refuses while the app runs; explains and asks; runs EmuDeck's install function; shows seconds and MB in EmuDeck's temp folder; reports EmuDeck errors; says whether the exe changed (with the log folder if not). Source: `Invoke-EmuDeckInstall`. Proof: `InstallTests.EmuDeck_install_runs_its_function_and_reports_the_new_exe`, `EmuDeck_errors_and_an_unchanged_exe_are_reported`, `EmuDeck_refused_while_the_emulator_runs` (real EmuDeck run: PR 8 side-by-side)
- [x] **U-06** `emudeck` without `emudeckFn` or without EmuDeck's backend: opens EmuDeck with instructions, or says where it wasn't found. Source: `Open-EmuDeck`. Proof: `InstallTests.EmuDeck_without_its_backend_opens_EmuDeck_with_instructions`
- [x] **U-07** `inapp`: starts the program (or `launch`) in its own folder, shows `hint`; "Not found" when missing. Source: `Invoke-Method`. Proof: `InstallTests.Inapp_starts_the_program_or_its_launcher_and_shows_the_hint`
- [x] **U-08** `installer`: downloads, then asks "Run the installer now?". Source: `Invoke-Method`. Proof: `InstallTests.Installer_download_and_page_methods`
- [x] **U-09** `zip`: downloads, backs up, unpacks; saves installedTag for mods. Source: `Invoke-Method`, `Install-Zip`. Proof: `InstallTests.Mod_update`, `Zip_update_backs_up_says_how_to_roll_back_and_shows_the_hint`
- [x] **U-10** `download`: downloads and selects the file in Explorer. Source: `Invoke-Method`. Proof: `InstallTests.Installer_download_and_page_methods`
- [x] **U-11** `page`: opens the release page, or says none is set. Source: `Invoke-Method`. Proof: `InstallTests.Installer_download_and_page_methods`

## 6. Downloads, checksums and Windows Security

- [x] **W-01** Asset choice: `downloadUrl` with `{version}` first; else assets matching `asset`; one = used, several = numbered choice (Enter = cancel); none = release page opened. Source: `Select-Asset`, `Get-AssetFile`. Proof: `DownloadTests.DownloadUrl_fills_version`, `Asset_pattern_picks_one_file_without_asking`, `Several_files_are_a_numbered_choice_and_Enter_cancels`, `No_matching_download_opens_the_release_page`
- [x] **W-02** Safe file name: last part of the name only; refuses empty, `.`/`..`, control and `<>:"|?*` characters, trailing dot/space, device names (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM/LPT 0-9 and superscript 1-3, with or without extension); result must stay in the download folder. Source: `Get-SafeDownloadPath`. Proof: `DownloadTests.Safe_name_*`, `Traversal_name_lands_inside`, `Reserved_name_refused`
- [x] **W-03** Downloads only over https; message and no file touched otherwise. Source: `Save-Download`, `Test-HttpsUrl`. Proof: `DownloadTests.Non_https_download_refused`
- [ ] **W-04** Progress line with MB and seconds in the status bar; window stays responsive. Source: `Save-Download`. Proof:
- [x] **W-05** Download folder created when missing; an older file of the same name replaced. Source: `Get-AssetFile`, `Save-Download`. Proof: `DownloadTests.Download_lands_the_file` (folder created, old file replaced in `Downloader.SaveAsync`)
- [x] **W-06** Downloaded file gets the "from the internet" mark (Zone.Identifier, ZoneId=3, HostUrl). Source: `Save-Download`. Proof: `DownloadTests.Download_is_marked`, `Zone_identifier_round_trips_on_NTFS` (Windows)
- [x] **W-07** File removed by Windows Security right after download or during the checksum is detected and reported. Source: `Save-Download`, `Test-Checksum`. Proof: `DownloadTests.Quarantine_help`, `Late_quarantine_reported`
- [x] **W-08** Checksum found as `<file>.sha256`, else `SHA256SUMS` / `checksums.txt` / `sha256.txt` / `hashes` style lists; must be https. Source: `Get-PublishedHash`. Proof: `DownloadTests.Per_file_sha256_verified`, `Sums_list_verified`, `Http_checksum_not_fetched`
- [x] **W-09** Match = green "SHA-256 matches"; mismatch = file deleted, expected/got shown; locked file = not used. Source: `Test-Checksum`. Proof: `DownloadTests.Bad_checksum_rejected_and_deleted`, `Per_file_sha256_verified`
- [x] **W-10** Windows Security help: detection name and ID when readable (note for generic ML detections), warning against "Allow on device" and exclusions, published SHA-256, offer to look it up on VirusTotal, offer to open Microsoft's false-positive form. Never restores, allows or excludes anything. Source: `Show-QuarantineHelp`. Proof: `DownloadTests.Quarantine_help`, `Quarantine_without_admin_rights_says_so`
- [x] **W-11** Release-page links open only when absolute https; otherwise "Not opening ...". Source: `Open-WebPage`. Proof: `DownloadTests.Page_link_opened_parsed`, `Page_link_refused`

## 7. Archives, zip installs and mods

- [x] **Z-01** `.zip` always unpackable; `.7z` when EmuDeck's 7z.exe exists (7z.exe also used for zips when present); other types saved and shown in Explorer. Source: `Test-Archive`, `Expand-ToFolder`, `Install-Zip`. Proof: `InstallTests.Cannot_unpack_other_types_or_7z_without_EmuDeck`, `Seven_zip_is_used_when_EmuDeck_has_it`, `Other_file_types_are_saved_and_shown`
- [x] **Z-02** A single top-level folder in the archive is flattened. Source: `Expand-ToFolder`. Proof: `InstallTests.Unzip_flattens_single_top_folder`
- [x] **Z-03** Zip update: refuses while the program runs; shows and asks about the backup `<folder>.backup-yyyyMMdd-HHmm`; copies, unpacks over, says how to roll back, shows `hint`. Source: `Install-Zip`. Proof: `InstallTests.Zip_update_refused_while_the_program_runs`, `Zip_update_cancelled_changes_nothing`, `Zip_update_backs_up_says_how_to_roll_back_and_shows_the_hint`
- [x] **Z-04** App folder = `installDir` when set, else the exe's folder. Source: `Get-AppFolder`. Proof: `InstallTests.App_folder_is_installDir`
- [x] **Z-05** installedTag saved after a successful mod update; unchanged when cancelled. Source: `Save-InstalledTag`. Proof: `InstallTests.Mod_update`, `Mod_update_cancelled`

## 8. Add app

- [ ] **A-01** URL recognised: GitHub (URL or `owner/repo`), itch.io game page, gitlab.com project (permalink/latest API), any other host as Gitea/Forgejo (`/api/v1/repos/.../releases/latest`); anything else cancelled with a message. Source: `Add-App`. Proof:
- [ ] **A-02** Name asked, default = repo/game name. GitHub asks about pre-releases. Source: `Add-App`. Proof:
- [ ] **A-03** itch.io: asks for the installed exe; update method `page`. Source: `Add-App`. Proof:
- [ ] **A-04** Existing exe path: tracked with `download`, `page`. Source: `Add-App`. Proof:
- [ ] **A-05** Existing folder (mod): reads the release, refuses an empty folder, tracks the first `.ips` (else first file), asks if the latest release is the one installed (No = `unknown`), methods `zip`, `page`. Source: `Add-App`. Proof:
- [ ] **A-06** Install now: shows the latest release, asks the folder (default `<installRoot>\<name>`), asks before using a non-empty folder, offers only Windows files (zip/7z/exe/msi, not linux/appimage/macos/darwin/android/.sha256/.zsync), downloads with checksum, unpacks or copies. Source: `Add-App`. Proof:
- [ ] **A-07** After installing: picks the main program (numbered choice when several, Enter = 1); no exe = asks if it's a mod/data pack (tracked by release) or uses top-level files; nothing unpacked = not added. Source: `Add-App`. Proof:
- [ ] **A-08** Program gone 2 s after install = Windows Security help, not added. Source: `Add-App`. Proof:
- [ ] **A-09** Saves an `asset` pattern with the version wildcarded, update method `zip` or `download` then `page`. Source: `Add-App`. Proof:

## 9. Other row actions

- [ ] **R-01** Launch: starts `launch` or the exe in its folder; non-program files (mods) open their folder instead; "Not found" messages. Source: `Start-App`. Proof:
- [ ] **R-02** Open folder: opens the app folder in Explorer, or "Folder not found". Source: `BtnFolder`, context menu. Proof:
- [ ] **R-03** Release page: opens the page, or "No release page is set for <app>". Source: `BtnPage`. Proof:
- [ ] **R-04** Remove from list: asks first (files stay), removes the entry and its cache, logs it. Source: `Remove-AppEntry`. Proof:
- [ ] **R-05** No row selected: "Select an app in the list first." Source: `Get-Selected`. Proof:

## 10. Main window

- [ ] **M-01** Title "Wintainium <version>", app icon, 1120x740, minimum 820x480, centred. Source: `$mainXaml`. Proof:
- [ ] **M-02** Header "Wintainium" and summary line: "All N apps are up to date - checked HH:mm" (green) or "N updates available - N need attention - N apps - checked HH:mm" (amber when updates, red when only problems). Source: `Update-Grid`. Proof:
- [ ] **M-03** Toolbar in this order with these icons: Update selected (primary), Update all, What's new, Launch, Release page, Open folder, Add app, Refresh, Re-check all (tooltip), Scan (tooltip), Setup (tooltip). Source: `$mainXaml`. Proof:
- [ ] **M-04** Search box with "Search apps..." hint; filters by name, case-insensitive. Source: `Test-RowVisible`, `Update-Filter`. Proof:
- [ ] **M-05** "Only show updates" hides rows that are up to date. Source: `Test-RowVisible`. Proof:
- [ ] **M-06** Hint text "Double-click to update - right-click for more - F5 to refresh". Source: `$mainXaml`. Proof:
- [ ] **M-07** Grid: columns App, Status, Installed, Latest (ellipsis + tooltip), Released, Update via; single row selection; read-only; alternating row colours; selection kept across refreshes. Source: `$mainXaml`, `Update-Grid`. Proof:
- [ ] **M-08** Status pills: UPDATE amber, up to date green, lookup failed / rate limited / token rejected / exe not found red, site busy orange, others grey. Source: `$mainXaml`. Proof:
- [ ] **M-09** Gold self row: gold, semi-bold, glow, tooltip; on unless `goldSelf` is false. Source: `$mainXaml`, `Get-AppRows`. Proof:
- [ ] **M-10** Log pane: coloured lines (yellow, red, green, cyan, grey...), monospace, wraps, scrolls to the end. Source: `Write-Host` override. Proof:
- [ ] **M-11** Status bar: "Ready", busy labels ("Checking...", "Updating <app>...", ...) and live progress lines. Source: `Set-Busy`, `Write-Host` override. Proof:
- [ ] **M-12** While busy, all toolbar buttons are disabled and a second action is ignored; errors land in the log as "Error: ...". Source: `Set-Busy`, `Invoke-Busy`. Proof:
- [ ] **M-13** On open: setup wizard first if setup was never done (or started with setup), then a refresh. Source: `ContentRendered`. Proof:
- [ ] **M-14** After Setup a refresh; after Scan a refresh only when something was added. Source: `BtnSetup`, `BtnScan`. Proof:
- [ ] **M-15** Palette and styles: base `#1E1E2E`, panels `#181825`, buttons `#313244` with rounded corners and hover/disabled opacity, primary `#CBA6F7`, text `#CDD6F4`, Segoe UI 13. Source: `$styles`, XAML. Proof:

## 11. Mouse, keyboard and context menu

- [ ] **K-01** Double-click a row = update it. Source: `MouseDoubleClick`. Proof:
- [ ] **K-02** F5 = refresh. Source: `KeyDown`. Proof:
- [ ] **K-03** Delete (grid focused) = remove from list (asks first). Source: `KeyDown`. Proof:
- [ ] **K-04** Right-click selects the row under the mouse, then opens the menu. Source: `PreviewMouseRightButtonDown`. Proof:
- [ ] **K-05** Menu: Update, What's new, Launch, separator, Open release page, Open folder, separator, Remove from list... Source: context menu. Proof:
- [ ] **K-06** "Show Wintainium in gold" (checkable) only on the self row; saves `goldSelf` and updates the row at once. Source: context menu. Proof:

## 12. Dialogs

- [ ] **G-01** Prompt dialog: last 12 log lines as context (hidden when none), question, Yes/No buttons for yes/no questions (Stop as a third when offered), text box with OK (default) / Cancel otherwise, focus in the text box. Source: `Show-Prompt`. Proof:
- [ ] **G-02** Every question asked today has a dialog with the same text (prompt list below). Source: `Read-Host` calls. Proof:
- [ ] **G-03** What's new: title "<app>  <latest>", "Released <date> - you have <installed>", notes with markdown `**` and `#` stripped, Open release page, Close (Esc). Fallback texts when there are no notes. Source: `Show-Notes`, `Get-NotesText`. Proof:

Questions asked today (all become dialogs):

| Where | Question |
|---|---|
| Update | "<app> is <status>. Run its update anyway? (y/N)" |
| Update | "how? (Enter = 1, x = cancel)" (method choice) |
| Update all | "Update <app> via <method>? (y/N, q = stop)" |
| Asset | "Which file?" list, "number (Enter to cancel)" |
| EmuDeck | "Continue? (y/N)" |
| Zip | "Continue? (y/N)" (after the backup explanation) |
| Installer | "Run the installer now? (y/N)" |
| Windows Security | "Look this hash up on VirusTotal (no upload needed)? (y/N)" |
| Windows Security | "Open Microsoft's form to report a false positive? (y/N)" |
| Remove | "Remove <app> from the list? Its files stay where they are. (y/N)" |
| Add app | "Repo or release URL (GitHub, GitLab, Gitea/Forgejo, or an itch.io game page)" |
| Add app | "App name (Enter = <name>)" |
| Add app | "Track pre-releases / nightlies too? (y/N)" |
| Add app | "Path to its .exe" (itch.io) |
| Add app | "Path to its .exe (or a mod's folder) if it is already installed (Enter = install it now)" |
| Add app | "Is the latest release (<tag>) the one in that folder? (Y/n)" |
| Add app | "Install folder (Enter = <folder>)" |
| Add app | "<folder> already has files in it. Install into it anyway? (y/N)" |
| Add app | "There is no .exe in it. Is it a mod or data pack, to track by release? (Y/n)" |
| Add app | "Which file is the main program?" list, "number (Enter = 1)" |

## 13. Setup wizard

- [ ] **N-01** Window "Wintainium setup", 660x600, "Step N of 5", Back (hidden on step 1), Next / Finish, Cancel. Source: `$wizardXaml`, `Set-WizardPage`. Proof:
- [ ] **N-02** Step 1 welcome text and the "Before you use it" box (wording updated for the exe, see C-03). Source: `$wizardXaml`. Proof:
- [ ] **N-03** Step 2 folders: install and download boxes filled with current values, Browse... pickers starting at the current folder. Source: `Select-WizardFolder`. Proof:
- [ ] **N-04** Step 3 token: explanation, green "no permissions" box, three steps, Open GitHub, hidden-text box, Check and save; results: saved (limit, expiry), not safe / rejected ("Nothing was saved."); current state text when a token exists or not. Source: `CheckToken`, `Update-WizardTokenStatus`. Proof:
- [ ] **N-05** Step 4 scan: Scan this PC opens the scan window, also scanning the install folder from step 2; result text with the number added. Source: `ScanNow`. Proof:
- [ ] **N-06** Step 5 shortcuts: Desktop and Start menu (both ticked), pin-to-taskbar tip, "You're set" text. Source: `$wizardXaml`. Proof:
- [ ] **N-07** Finish saves folders and `setupDone`, creates the ticked shortcuts (with icon, description and taskbar ID), logs "Setup saved...". Shortcut errors logged, not fatal. Source: `Complete-Wizard`. Proof:
- [ ] **N-08** Cancel or closing still sets `setupDone`, so the wizard doesn't open on every start. Source: `Show-SetupWizard`. Proof:

## 14. Scan window

- [ ] **C-01** Window "Find apps you already have", 820x660 (min 640x420), intro text, scan starts when shown, "Scanning your PC... N s" while it runs, window stays responsive, closing stops the scan. Source: `Show-ScanWindow`, `Start-PcScan`. Proof:
- [ ] **C-02** Row per candidate: tick, editable name, source line, path, note; ticked by default for recognized/found apps. Source: `Add-ScanRow`. Proof:
- [ ] **C-03** Downloads without an installed program: amber edge and text, unticked, Locate .exe... button; ticking one opens Locate; Locate fills the path and ticks it. Source: `Add-ScanRow`, `Invoke-ScanLocate`. Proof:
- [ ] **C-04** Don't show again: hides the row now and in future scans (`scanIgnore`); Show hidden (N) clears the list and rescans. Source: `Hide-ScanRow`, `SHidden`. Proof:
- [ ] **C-05** Scan another folder...: adds a folder and rescans; footer lists extra folders. Source: `SFolder`. Proof:
- [ ] **C-06** Summary: "Found N apps." plus the Locate hint, or "Nothing new found..." in green. Mod count footer. Source: `Update-ScanSummary`, `Show-ScanResults`. Proof:
- [ ] **C-07** Add selected (N): disabled when nothing is ticked or a scan runs; adds ticked rows with a program, makes names unique ("Name (2)"), saves, logs "Added N apps from the scan.", closes. Source: `Add-ScanSelection`, `Update-ScanCount`. Proof:
- [ ] **C-08** Scan failure shows "The scan didn't finish: <why>" in red. Source: scan timer. Proof:

## 15. PC scan engine (Find-Sources)

- [ ] **P-01** Recognized apps from `known-apps.json`: each candidate exe path tried (list allowed, `{EmulationPath}` and env vars expanded); tracked check by exe, repo or name; `emudeck` method dropped (with `emudeckFn`) when EmuDeck's backend is missing; catalog extras kept; portable path kept. Source: FS section 0. Proof:
- [ ] **P-02** Wintainium itself offered when not tracked: exe or script path; installed copy = `installer` + asset `^Wintainium\.exe$` + hint; other copies = `page` + hint. Source: FS section 0. Proof:
- [ ] **P-03** EmuDeck's Emulation folder read from its `settings.ps1` and added to the roots. Source: FS. Proof:
- [ ] **P-04** Downloads (Downloads, Desktop, Documents, Emulation, LocalAppData\Programs, install folder, extra folders; depth 6; archive/program types) with a Zone.Identifier mark: site and repo from ReferrerUrl/HostUrl; skips game/save/cache folders. Source: FS section 1. Proof:
- [ ] **P-05** Source detection: GitHub release downloads and pages, raw.githubusercontent, release-asset hosts (repo ID looked up through the API), GitLab, Codeberg/Forgejo/Gitea, unknown hosts with `/releases/download/`, itch.io game pages (not file servers or the front page); ssh remotes converted; local paths and non-code sites ignored. Source: FS `Get-RepoFromUrl`. Proof:
- [ ] **P-06** Untracked programs in app folders (install folder, Downloads, Emulation\tools, EmuDeck\Emulators, extra folders): one per folder (largest exe), loose exes per file, noise filtered (uninstallers, updaters, redistributables, backups); origin from the file or the archive it came from. Source: FS section 3. Proof:
- [ ] **P-07** Mods, texture packs, cheats and patches counted (container folders, Switch-style title IDs, Dolphin-style layouts, patch files); bundled emulator folders skipped. Source: FS section 4. Proof:
- [ ] **P-08** Candidates: recognized (ticked), found programs from a code host or itch.io (ticked, not when inside a recognized app's folder, not when tracked), downloads with no program (unticked, need Locate; phone/Linux/macOS files left out); each with a hide key; hidden ones counted. Source: FS `Get-ScanCandidates`. Proof:
- [ ] **P-09** New entries: itch = `page`; GitHub / GitLab / Gitea = `download`, `page` with the right API. Source: FS `New-AppDef`. Proof:
- [ ] **P-10** Scan only reads files; the GitHub token is used only for the repo-ID lookup at api.github.com. Source: FS. Proof:

## 16. Shortcuts and taskbar

- [ ] **H-01** Own taskbar button: AppUserModelID `Ay1man2.Wintainium` on the process, window and shortcuts, so pinned icons and the open window share one button; "Pin to taskbar" pins Wintainium. Source: `WintainiumShell`. Proof:
- [ ] **H-02** Shortcut and window icon from the exe (never holds a loose `icon.ico` open). Source: `Get-ShellIcon`. Proof:

## 17. Install, update and uninstall (launcher)

- [ ] **I-01** Run on its own: installs per user to `%LOCALAPPDATA%\Programs\Wintainium` (no admin), writes `installed-version.txt`, starts the installed copy and exits. Source: L `Main`, `Install`. Proof:
- [ ] **I-02** Newer exe updates the installed copy; older never downgrades; user files (list, settings, token, cache) never touched. Source: L `Main`, `Install`. Proof:
- [ ] **I-03** Installed copy open during an update: "close it, then Retry" with Retry/Cancel. Source: L `Install`. Proof:
- [ ] **I-04** Installed exe written fresh, so the download mark isn't copied to it. Source: L `Install`. Proof:
- [ ] **I-05** Settings > Apps entry (HKCU `...\Uninstall\Wintainium`): name, version, publisher, icon, location, uninstall command, about link, no modify/repair, size. Source: L `Register`. Proof:
- [ ] **I-06** `--uninstall`: only the install folder; refuses when no install; waits for an open window (Retry); asks to remove, then whether to delete the list/settings/token/cache; removes Desktop/Start shortcuts that point into it, the registry entry, then the files after exit. Source: L `Uninstall`. Proof:
- [ ] **I-07** `--install-only`: installs or updates without starting, no message boxes, result in the exit code. Source: L `Main`. Proof:
- [ ] **I-08** Portable copy runs in place and installs nothing. Source: L `TrustedScriptFolder`. Proof:
- [ ] **I-09** Startup errors shown in a message box. Source: L `Run`. Proof:
- [ ] **I-10** Self-update: an installed copy updates by downloading `Wintainium.exe`, checking it against `Wintainium.exe.sha256` and running it. Release asset names stay the same, so v0.3.0 installs pick up v0.4.0. Source: P-02, release. Proof:
- [ ] **I-11** A v0.3.0 install upgrades in place to v0.4.0: old script files removed, user files kept, same registry entry. Source: new. Proof:

## 18. Command line

Decided for v0.4.0 (see port plan): keep the window as the main way in, and a few flags for people
who know what they're doing.

- [ ] **L-01** `Wintainium.exe` with no arguments opens the window. Source: L, `gui`. Proof:
- [ ] **L-02** `--list` prints the table (App, Status, Installed, Latest, Released, Update via, number of updates, lookup notes) to the console it was started from. Source: `list`, `Show-Table`. Proof:
- [ ] **L-03** `--setup` opens the window with the setup wizard first. Source: `setup`. Proof:
- [ ] **L-04** `--uninstall` and `--install-only` (I-06, I-07). Source: L. Proof:
- [ ] **L-05** `--data <folder>` uses the data files in that folder (replaces `-Config`). Source: `-Config`. Proof:
- [ ] **L-06** Grace days and cache minutes kept as optional settings keys (defaults 3 and 60), replacing `-GraceDays` and `-CacheMinutes`. Source: `param`. Proof:
- [ ] **L-07** EmuDeck location: default `%LOCALAPPDATA%\Programs\EmuDeck\EmuDeck.exe` (replaces `-EmuDeckExe`). Source: `param`. Proof:

## 19. Release build

- [ ] **B-01** Four assets: `Wintainium.exe`, `Wintainium.exe.sha256`, `Wintainium-<ver>-portable.zip`, `Wintainium-<ver>-portable.zip.sha256` (`<hash>  <name>` format). Source: `build/Build-Release.ps1`. Proof:
- [ ] **B-02** Exe version info (product, description, company, copyright GPL-3.0 link, version) and icon. Source: L assembly attributes. Proof:
- [ ] **B-03** Built by CI from a tag into a draft release with build attestations; the maintainer tests that exe before publishing. Source: new (port plan). Proof:

---

## Intentional changes

The only places where v0.4.0 may behave differently from v0.3.0. Each needs its own test and a
CHANGELOG line. Numbers refer to SECURITY-REVIEW.md.

- [x] **X-01** Archive entries must stay inside the target folder (zip-slip refused). (#2) Proof: `InstallTests.Zip_slip_refused`
- [x] **X-02** Unpacked programs and scripts keep the download mark. (#2) Proof: `InstallTests.Unpacked_programs_keep_the_mark`
- [x] **X-03** Checksum file present but unusable = download refused, not "not verified". Wording "integrity check". (#4) Proof: `DownloadTests.Unusable_checksum_refuses`
- [x] **X-04** `GITHUB_TOKEN` used only if it passes the no-scope check. (#6) Proof: `TokenTests.GITHUB_TOKEN_is_used_only_when_it_has_no_scopes`
- [ ] **X-05** Usernames/passwords removed from any URL shown or saved. (#7) Proof:
- [x] **X-06** `emudeckFn` must match `^[A-Za-z0-9]+_install$`. (#8) Proof: `InstallTests.EmuDeck_function_names_are_checked`, `Bad_EmuDeck_function_not_run`
- [ ] **X-07** Standard Windows argument quoting (trailing backslash). (#9) Proof:
- [x] **X-08** Zip updates unpack into the right folder when the exe sits in a subfolder. (correctness 1) Proof: `InstallTests.Exe_in_subfolder_updates_the_right_folder`
- [x] **X-09** Major-version jumps compared by number, not by date. (correctness 2) Proof: `VersionCompareTests.Older_by_number`, listed in `StatusGoldenTests.Deviations`. Only when both versions use the same kind of numbering (neither or both date-style, majors at most 3 apart); otherwise the date decides as before (`VersionCompareTests.Different_schemes_compare_only_when_alike`, golden "Date-style tags" and "Large major gap" cases).
- [x] **X-10** Exact version match instead of substring (12.1.0 no longer "contains" 2.1). (correctness 3) Proof: `VersionCompareTests.Same_release_needs_the_whole_number`, listed in `StatusGoldenTests.Deviations`
- [x] **X-11** Atomic writes for every data file; a broken app list is restored from `.bak` with a clear message instead of stopping startup. (correctness 4) Proof: `AppListTests.Saves_utf8_without_bom_and_keeps_a_backup`, `Broken_list_is_restored_from_the_backup`, `Broken_list_without_backup_is_reported_and_left_alone`
- [x] **X-12** Old backups pruned; files a new zip release no longer has are reported, not deleted. (correctness 5) Proof: `InstallTests.Old_backups_pruned`, `InstallTests.Leftover_files_are_kept_and_reported` (PR 6). Intentional change (decided by the owner): leftover files are never deleted, because they can be saves or settings; after an update the app lists them with their folder and offers to open it.
- [ ] **X-13** Every request has a timeout; lookups run in parallel off the UI thread. (correctness 6) Proof:
- [ ] **X-14** Closing any yes/no dialog with X or Alt+F4 = No (already true in v0.3.0's window; now true everywhere). (correctness 7) Proof:
- [ ] **X-15** Wizard welcome text describes the exe instead of "an unsigned PowerShell script ... Wintainium.ps1". Proof:

## Dropped on purpose

Decided with the maintainer; listed so nobody ports them by mistake.

- **Interactive console menu** (`menu`: numbered update, `a`, `w n`, `l n`, `o n`, `n`, `x n`, `r`, `rf`, `q`). The window does all of it.
- **`token` command** (clipboard / `GITHUB_TOKEN` prompt). The wizard's token step does it.
- **`shortcut` command**. The wizard's shortcut step does it.
- **Standalone `Find-Sources.ps1` report** (console tables and `sources-inventory.csv` on the Desktop). The Scan window covers it.
- **PowerShell 7 to Windows PowerShell restart** for the window (STA). Not needed in C#.
- **The PowerShell files** themselves (removed at cutover; the v0.3.0 tag and git history keep them).

---

## Test map

Every check in the v0.3.0 PowerShell suites, and the xUnit test that replaces it. A check that no
longer applies says why instead of naming a test.

### Test-Dialogs.ps1

- [ ] Show-Prompt found in the script -> no longer applies (C# dialogs are typed code)
- [ ] Yes/No box closed with X answers No ->
- [ ] yes-default question closed with X answers No ->
- [ ] Stop question closed with X answers No (skip) ->
- [ ] Yes/No box shows Yes and No buttons ->
- [ ] text box closed with X answers nothing ->

### Test-Downloads.ps1

- [x] download via progress loop lands the file -> `DownloadTests.Download_lands_the_file`
- [x] per-file .sha256 verified -> `DownloadTests.Per_file_sha256_verified`
- [x] bad checksum rejected -> `DownloadTests.Bad_checksum_rejected_and_deleted`
- [x] bad download deleted -> `DownloadTests.Bad_checksum_rejected_and_deleted`
- [x] SHA256SUMS list verified -> `DownloadTests.Sums_list_verified`
- [x] no checksum: download kept + noted -> `DownloadTests.No_checksum_kept_and_noted`
- [x] missing file reported as failed download -> `DownloadTests.Missing_file_failed`
- [x] downloadUrl fills {version} -> `DownloadTests.DownloadUrl_fills_version`
- [x] safe name: '<name>' stays in the download folder (each case) -> `DownloadTests.Safe_name_stays_inside`
- [x] safe name: '<name>' refused (each case) -> `DownloadTests.Safe_name_refused`
- [x] safe name: '<name>' allowed (each case) -> `DownloadTests.Safe_name_allowed`
- [x] safe name: normal name unchanged -> `DownloadTests.Safe_name_allowed`
- [x] traversal name: file lands inside the download folder -> `DownloadTests.Traversal_name_lands_inside`
- [x] traversal name: file at the target is not touched -> `DownloadTests.Traversal_name_lands_inside`
- [x] traversal name: nothing started -> `DownloadTests.Traversal_name_lands_inside`
- [x] reserved name: refused with a message, no download -> `DownloadTests.Reserved_name_refused`
- [x] https link accepted -> `LinksTests.Https_link_accepted`
- [x] link refused even in tests: '<url>' (each case) -> `LinksTests.Link_refused`
- [x] file:// refused outside tests -> `LinksTests.Link_refused (file:///C:/... case)`
- [x] http download refused with a message -> `DownloadTests.Non_https_download_refused`
- [x] http download: existing file not deleted -> `DownloadTests.Non_https_download_refused`
- [x] http checksum file not fetched -> `DownloadTests.Http_checksum_not_fetched`
- [x] file:// download refused outside tests -> `DownloadTests.Non_https_download_refused`
- [x] unzip flattens the single top folder -> `InstallTests.Unzip_flattens_single_top_folder`
- [ ] Add-App: Forgejo URL -> gitea api ->
- [ ] Add-App: installed into InstallRoot\name ->
- [ ] Add-App: AppImage filtered out (single choice, no prompt) ->
- [ ] Add-App: checksum verified during install ->
- [ ] Add-App: version wildcarded in asset pattern ->
- [ ] Add-App: update method zip ->
- [ ] Add-App parses <url> (each case) ->
- [x] quarantine: names the detection -> `DownloadTests.Quarantine_help`
- [x] quarantine: warns against Allow on device -> `DownloadTests.Quarantine_help`
- [x] quarantine: shows published SHA-256 -> `DownloadTests.Quarantine_help`
- [x] quarantine: VirusTotal opened for that hash -> `DownloadTests.Quarantine_help`
- [x] quarantine: Microsoft form opened -> `DownloadTests.Quarantine_help`
- [x] quarantine: nothing returned to install -> `DownloadTests.Quarantine_help`
- [x] remove: only that app removed -> `AppListTests.Remove_takes_only_that_app (list part; the dialog is covered in PR 9)`
- [ ] remove: answering No keeps it ->
- [x] late quarantine: no crash -> `DownloadTests.Late_quarantine_reported`
- [x] late quarantine: reported as removed -> `DownloadTests.Late_quarantine_reported`
- [x] page link: https opened -> `DownloadTests.Page_link_opened_parsed`
- [x] page link: opened in its checked, parsed form -> `DownloadTests.Page_link_opened_parsed`
- [x] page link: '<url>' not opened, and said so (each case) -> `DownloadTests.Page_link_refused`
- [x] no matching download: UNC page link not opened -> `DownloadTests.Unc_page_not_opened`
- [x] no Start-Process on a page link outside Open-WebPage -> replaced by `DownloadTests.Only_WebPage_opens_links` (only WebPage.cs calls OpenUrl)

### Test-Itch.ps1

- [x] itch: version read -> `ItchPageTests.Version_read`
- [x] itch: exact UTC date read -> `ItchPageTests.Exact_utc_date_read`
- [x] itch: page kept as link -> `ItchPageTests.Page_kept_as_link`
- [x] itch: prefers the Windows upload -> `ItchPageTests.Prefers_the_windows_upload`
- [x] itch: asset pattern overrides -> `ItchPageTests.Asset_pattern_overrides`
- [x] itch: no version -> file name -> `ItchPageTests.No_version_uses_file_name`
- [x] itch: empty page throws -> `ItchPageTests.Empty_page_throws`
- [x] itch: empty page throws (shown as lookup failed) -> `ItchPageTests.Empty_page_throws`
- [ ] add-app: itch URL -> itch source ->

### Test-Launch.ps1

- [ ] starts with -File from another folder -> no longer applies (PowerShell start-up quirk); replaced by: exe started from another folder finds its data files
- [x] app list created next to the script -> `AppListTests.Missing_list_is_created_empty_with_a_notice`
- [ ] empty list shows 0 updates ->

### Test-Launcher.ps1

- [ ] installs from a lone download ->
- [ ] installs the scripts and the icon -> becomes: installs the exe
- [ ] records the installed version ->
- [ ] newer download updates the installed copy ->
- [ ] update keeps your app list and settings ->
- [ ] installed exe is the new one ->
- [ ] older download does not downgrade ->
- [ ] git clone: runs its own scripts, left untouched -> no longer applies (no scripts); replaced by: portable/clone folder runs in place
- [ ] untouched portable zip: runs in place ->
- [ ] planted script ignored, own copy installed -> no longer applies (no scripts are run)
- [ ] planted script with a planted marker ignored -> no longer applies (no scripts are run)
- [ ] .git file pointing at another repo ignored -> no longer applies (no scripts are run)
- [ ] planted .git file ignored, own copy installed -> no longer applies (no scripts are run)
- [ ] .git file pointing at a network share ignored -> no longer applies (no scripts are run)
- [ ] .git folder without HEAD ignored -> no longer applies (no scripts are run)
- [ ] genuine script beside a changed helper not trusted -> no longer applies (no scripts are run)
- [ ] installed copy refreshes stale scripts -> becomes: v0.3.0 install upgraded, old scripts removed (I-11)
- [ ] launcher without scripts reports an error -> no longer applies

### Test-Lookups.ps1

- [x] itch.io 403 without a saved answer is "site busy", not a GitHub status -> `ReleaseLookupTests.Itch_403_is_site_busy`
- [x] itch.io 403 with a saved answer falls back to it -> `ReleaseLookupTests.Itch_403_falls_back_to_the_saved_answer`
- [x] Codeberg 503 is "site busy" -> `ReleaseLookupTests.Codeberg_503_is_site_busy`
- [x] GitHub 401 is "token rejected" -> `ReleaseLookupTests.GitHub_401_is_token_rejected`
- [x] GitHub lookups still work -> `ReleaseLookupTests.GitHub_lookups_still_work`
- [x] token message shown once for the GitHub app -> `LookupNotesTests.Token_message_once`
- [x] no rate-limit message for an itch.io failure -> `LookupNotesTests.No_rate_limit_message_for_itch`
- [x] site-busy note names itch.io -> `LookupNotesTests.Site_busy_names_itch`
- [x] fallback note says when the saved answer is from -> `LookupNotesTests.Fallback_note_says_when`
- [x] itch.io request looks like a browser and identifies Wintainium -> `ReleaseSourcesTests.Itch_request_looks_like_a_browser`
- [x] itch.io request never carries the GitHub token -> `ReleaseSourcesTests.Itch_request_never_carries_the_token`
- [x] http release address is not requested -> `ReleaseSourcesTests.Http_address_is_not_requested`
- [x] http release address shows "lookup failed" -> `ReleaseLookupTests.Http_address_is_lookup_failed`
- [x] http release address explained -> `LookupNotesTests.Http_address_explained`
- [x] expiry with offset parsed -> `TokenTests.Expiry_with_offset_parsed`
- [x] expiry in UTC parsed -> `TokenTests.Expiry_in_utc_parsed`
- [x] no-scope token accepted and marked safe -> `TokenTests.No_scope_token_accepted`
- [x] token with scopes is flagged, not safe -> `TokenTests.Scoped_token_flagged`
- [x] fine-grained token not treated as safe (permissions unknowable) -> `TokenTests.Fine_grained_not_safe`
- [x] rejected token reported -> `TokenTests.Rejected_token_reported`
- [x] empty token reported -> `TokenTests.Empty_token_reported`
- [x] install folder from settings file -> `SettingsTests.Install_folder_from_settings_file`
- [x] download folder from settings file -> `SettingsTests.Download_folder_from_settings_file`
- [ ] command-line option still wins -> becomes: `--data` folder wins over the default

### Test-Mods.ps1

- [ ] mod: added instead of "Nothing was unpacked" ->
- [ ] mod: unpacked with its own folder kept ->
- [ ] mod: tracked by the .ips file ->
- [ ] mod: installDir is the install folder ->
- [ ] mod: installedTag recorded ->
- [ ] mod: updates by zip, then page ->
- [ ] mod: asset pattern matches the next version ->
- [ ] mod: same release is up to date ->
- [ ] mod: Installed column shows the release tag ->
- [ ] mod: new release flagged UPDATE ->
- [x] mod update: new files in place -> `InstallTests.Mod_update`
- [x] mod update: nothing unpacked into patches\ -> `InstallTests.Mod_update`
- [x] mod update: folder backed up first -> `InstallTests.Mod_update`
- [x] mod update: installedTag saved -> `InstallTests.Mod_update`
- [ ] mod update: up to date afterwards ->
- [x] mod update cancelled: tag unchanged -> `InstallTests.Mod_update_cancelled`
- [ ] mod: Launch opens the folder, does not run the .ips ->
- [x] mod: Get-AppFolder returns installDir -> `InstallTests.App_folder_is_installDir`
- [ ] existing folder: tracked by release ->
- [ ] existing folder, unknown release: shows UPDATE ->
- [ ] not a mod: refused when no top-level program ->
- [ ] plain app: old .exe still flagged UPDATE ->

### Test-Scan.ps1

- [ ] itch.io game page recognized ->
- [ ] itch.io download page keeps the game page ->
- [ ] itch.io front page is not a game ->
- [ ] GitHub entry ->
- [ ] itch.io entry opens the page ->
- [ ] GitLab entry uses the API ->
- [ ] Codeberg entry uses the Forgejo API ->
- [ ] Android download (.apk) left out ->
- [ ] Linux-only download left out ->
- [ ] "Don't show again" item left out and counted ->
- [ ] candidates carry the key used to hide them ->
- [ ] recognized app listed once (not again as a found program) ->
- [ ] program from GitHub listed and ticked ->
- [ ] program from an itch.io page listed ->
- [ ] already tracked repo left out ->
- [ ] program of unknown origin left out ->
- [ ] download for an installed program not repeated ->
- [ ] download with no program found needs Locate, unticked ->
- [ ] recognized apps found (PCSX2, Playnite) ->
- [ ] EmuDeck folder read from its settings (Steam ROM Manager) ->
- [ ] tracked app (xemu) left out ->
- [ ] recognized apps are ticked ->
- [ ] entry keeps the portable %APPDATA% path ->
- [ ] without EmuDeck installed, its update method is dropped ->
- [ ] catalog extras kept (launch, hint) ->
- [ ] with EmuDeck installed, updates go through EmuDeck ->
- [ ] Wintainium offered as a ticked entry ->
- [ ] Wintainium entry points at its script when there is no .exe -> no longer applies (there is always an exe)
- [ ] Wintainium entry prefers Wintainium.exe ->
- [ ] zip or clone copy updates from the release page ->
- [ ] installed copy updates itself from the verified Wintainium.exe ->
- [ ] hidden recognized app left out ->
- [ ] tracked Wintainium not offered again ->
- [ ] console report lists recognized apps -> no longer applies (report dropped)
- [ ] console report still writes the CSV -> no longer applies (report dropped)

### Test-SourceDetection.ps1

- [ ] github release download ->
- [ ] github releases page referrer ->
- [ ] release-assets host: site only ->
- [ ] raw.githubusercontent repo ->
- [ ] gitlab package file ->
- [ ] forgejo self-hosted ->
- [ ] codeberg ->
- [ ] unknown host with /releases/download/ ->
- [ ] scummvm site is not a code host ->
- [ ] gamebanana is not a code host ->
- [ ] local path ignored ->
- [ ] ssh remote converted ->
- [ ] release-assets repo id resolved ->
- [ ] old objects host repo id resolved ->

### Test-Status.ps1

- [x] GhOld flagged UPDATE -> `StatusTests.GhOld_flagged_update`
- [x] GhNew up to date -> `StatusTests.GhNew_up_to_date`
- [x] Rolling tag uses asset date, UPDATE past 30d grace -> `StatusTests.Rolling_tag_uses_asset_date`
- [x] Pre-release tracked -> `StatusTests.Prerelease_tracked`
- [x] Forgejo/Gitea source resolves -> `StatusTests.Gitea_resolves`
- [x] GitLab source resolves + UPDATE -> `StatusTests.GitLab_resolves`
- [x] Dolphin feed resolves -> `StatusTests.Dolphin_resolves`
- [x] Manual shows check manually -> `StatusTests.Manual_check_manually`
- [x] Missing exe reported -> `StatusTests.Missing_exe_reported`
- [x] 404 shows lookup failed -> `StatusTests.NotFound_lookup_failed`
- [x] 403 shows rate limited -> `StatusTests.Forbidden_rate_limited`
- [x] Wintainium reports its own version, up to date -> `StatusTests.Self_reports_own_version`
- [x] Wintainium flags its own newer release -> `StatusTests.Self_flags_newer_release`
- [x] cache file written -> `ReleaseCacheTests.Cache_round_trips`
- [x] second run served from cache (only failed ones retried) -> `ReleaseLookupTests.Second_run_served_from_cache`
