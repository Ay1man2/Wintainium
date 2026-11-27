using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Status;

namespace Wintainium.Core.Scan;

/// <summary>What a PC scan looks at, and what it already knows.</summary>
public sealed partial class ScanOptions
{
    public const string DefaultEmulationPath = @"C:\Emulation";

    /// <summary>Folders searched for downloaded files with a download mark.</summary>
    public IReadOnlyList<string> DownloadRoots { get; init; } = [];

    /// <summary>Folders whose subfolders (and loose programs) are apps.</summary>
    public IReadOnlyList<string> AppRoots { get; init; } = [];

    /// <summary>Folders searched for mods, texture packs, cheats and patches.</summary>
    public IReadOnlyList<string> ModRoots { get; init; } = [];

    /// <summary>The user's Downloads folder: loose files there are downloads, not installed apps.</summary>
    public string DownloadsFolder { get; init; } = "";

    /// <summary>EmuDeck's Emulation folder, for {EmulationPath} in the catalog.</summary>
    public string EmulationPath { get; init; } = DefaultEmulationPath;

    public IReadOnlyList<JsonObject> Catalog { get; init; } = [];

    /// <summary>Apps already in the list.</summary>
    public IReadOnlyList<AppEntry> Tracked { get; init; } = [];

    /// <summary>Keys hidden with "Don't show again" (scanIgnore).</summary>
    public IReadOnlyList<string> ScanIgnore { get; init; } = [];

    /// <summary>Wintainium's own folder, so it can offer itself.</summary>
    public string SelfFolder { get; init; } = "";

    /// <summary>EmuDeck's backend (functions\all.ps1) exists, so the emudeck method can work.</summary>
    public bool EmuDeckBackend { get; init; }

    public Func<string, string> Expand { get; init; } = Environment.ExpandEnvironmentVariables;

    public IFileMark Marks { get; init; } = new ZoneIdentifierMark();

    public IExeProbe Probe { get; init; } = new FileExeProbe();

    public IRepoIdResolver? RepoIds { get; init; }

    /// <summary>
    /// The folders v0.3.0 searched: Downloads, Desktop, Documents, the Emulation folder, Programs,
    /// EmuDeck's emulators, the install folder from Setup, and any extra folders.
    /// </summary>
    public static ScanOptions ForThisPc(Settings settings, IReadOnlyList<AppEntry> tracked, string selfFolder, IEnumerable<string> extraRoots, IRepoIdResolver? repoIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string downloads = Path.Combine(profile, "Downloads");
        string emuDeck = Path.Combine(appData, "EmuDeck");
        var download = new List<string> { downloads, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(profile, "Documents"), DefaultEmulationPath, Path.Combine(localAppData, "Programs") };
        var apps = new List<string> { Path.Combine(profile, "Wintainium", "Apps"), downloads, Path.Combine(DefaultEmulationPath, "tools"), Path.Combine(emuDeck, "Emulators") };
        var mods = new List<string> { DefaultEmulationPath, Path.Combine(emuDeck, "Emulators") };
        if (settings.InstallRoot is string root)
        {
            apps.Add(root);
            download.Add(root);
        }

        string emulation = ReadEmulationPath([Path.Combine(emuDeck, "settings.ps1"), Path.Combine(profile, "EmuDeck", "settings.ps1")]) ?? DefaultEmulationPath;
        if (!string.Equals(emulation, DefaultEmulationPath, StringComparison.Ordinal))
        {
            download.Add(emulation);
            mods.Add(emulation);
            apps.Add(Path.Combine(emulation, "tools"));
        }

        string[] extra = [.. extraRoots ?? []];
        return new ScanOptions
        {
            DownloadRoots = [.. download, .. extra],
            AppRoots = [.. apps, .. extra],
            ModRoots = [.. mods, .. extra],
            DownloadsFolder = downloads,
            EmulationPath = emulation,
            Catalog = Scan.Catalog.LoadBuiltIn(),
            Tracked = tracked,
            ScanIgnore = settings.ScanIgnore,
            SelfFolder = selfFolder,
            EmuDeckBackend = File.Exists(Path.Combine(emuDeck, "backend", "functions", "all.ps1")),
            RepoIds = repoIds,
        };
    }

    /// <summary>EmuDeck can keep its Emulation folder on another drive; its settings file says where.</summary>
    public static string? ReadEmulationPath(IEnumerable<string> settingsFiles)
    {
        foreach (string f in settingsFiles ?? [])
        {
            if (!File.Exists(f))
            {
                continue;
            }

            try
            {
                foreach (string line in File.ReadLines(f))
                {
                    Match m = EmulationPathLine().Match(line);
                    if (m.Success)
                    {
                        return m.Groups[1].Value.TrimEnd('\\');
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next one
            }
        }

        return null;
    }

    [GeneratedRegex("^\\s*\\$emulationPath\\s*=\\s*[\"']?([^\"'\\r\\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EmulationPathLine();
}
