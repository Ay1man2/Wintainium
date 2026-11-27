using Wintainium.Core.Logging;

namespace Wintainium.Core.Install;

/// <summary>Everything downloads and installs need from outside: network, user, Windows and folders.</summary>
public sealed class InstallContext
{
    public required HttpClient Http { get; init; }

    public required ILog Log { get; init; }

    public required IUserPrompts Prompts { get; init; }

    public required IShell Shell { get; init; }

    public IFileMark Mark { get; init; } = new ZoneIdentifierMark();

    public IThreatInfo? Threats { get; init; }

    public IProcessRunner Processes { get; init; } = new ProcessRunner();

    /// <summary>Where downloads are saved.</summary>
    public required string DownloadDir { get; init; }

    /// <summary>EmuDeck's own program, opened for apps EmuDeck manages.</summary>
    public string EmuDeckExe { get; init; } = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\EmuDeck\EmuDeck.exe");

    /// <summary>EmuDeck's data folder (%APPDATA%\EmuDeck): its backend, temp and logs.</summary>
    public string EmuDeckData { get; init; } = Environment.ExpandEnvironmentVariables(@"%APPDATA%\EmuDeck");

    /// <summary>EmuDeck's 7-Zip, used for .7z (and .zip) when present.</summary>
    public string SevenZip => Path.Combine(EmuDeckData, "backend", "wintools", "7z.exe");

    /// <summary>Folder for unpacking before files are copied into place.</summary>
    public string TempDir { get; init; } = Path.GetTempPath();

    /// <summary>Waits; tests pass a no-op.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>How many update backups of one folder to keep (X-12).</summary>
    public int BackupsToKeep { get; init; } = 2;
}
