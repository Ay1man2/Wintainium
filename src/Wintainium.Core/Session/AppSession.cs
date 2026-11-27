using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Releases;
using Wintainium.Core.Scan;
using Wintainium.Core.Status;
using Wintainium.Core.Token;

namespace Wintainium.Core.Session;

/// <summary>
/// Everything the window (or --list) works with: the data folder's files, the token, and the rows
/// of the last refresh (the script-level state of v0.3.0).
/// </summary>
public sealed class AppSession : IDisposable
{
    private readonly bool ownsHttp;
    private readonly IExeProbe probe;
    private readonly StatusEngine.SelfInfo? self;

    private AppSession(DataFolder data, ILog log, HttpClient http, bool ownsHttp, ISecretProtector protector, IExeProbe probe, StatusEngine.SelfInfo? self)
    {
        Data = data;
        Log = log;
        Http = http;
        this.ownsHttp = ownsHttp;
        this.probe = probe;
        this.self = self;
        Settings = Settings.Load(data.SettingsFile);
        Cache = ReleaseCache.Load(data.CacheFile);
        Token = new TokenStore(data.TokenFile, protector);
        List = AppList.Load(data.AppsFile, log);
    }

    public DataFolder Data { get; }

    public ILog Log { get; }

    public HttpClient Http { get; }

    public AppList List { get; private set; }

    public Settings Settings { get; private set; }

    public ReleaseCache Cache { get; }

    public TokenStore Token { get; }

    /// <summary>The rows of the last refresh, in grid order.</summary>
    public IReadOnlyList<AppRow> Rows { get; private set; } = [];

    /// <summary>
    /// Opens the data folder: copies emu-* files once (D-09), creates an empty app list when there is
    /// none (D-01). Throws <see cref="AppListLoadException"/> when the list can't be read (X-11).
    /// </summary>
    public static AppSession Open(
        string dataFolder,
        ILog log,
        ISecretProtector protector,
        HttpClient? http = null,
        IExeProbe? probe = null,
        StatusEngine.SelfInfo? self = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(protector);
        var data = new DataFolder(dataFolder);
        Directory.CreateDirectory(data.Path);
        data.MigrateLegacyFiles(log);
        bool owns = http is null;
        http ??= NewHttpClient();
        try
        {
            return new AppSession(data, log, http, owns, protector, probe ?? new FileExeProbe(), self);
        }
        catch
        {
            if (owns)
            {
                http.Dispose();
            }

            throw;
        }
    }

    /// <summary>The client every request goes through; each request sets its own shorter timeout.</summary>
    public static HttpClient NewHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Folder for apps installed with Add app (D-07).</summary>
    public string InstallRoot => Settings.InstallRoot ?? Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Wintainium\Apps");

    /// <summary>Folder for downloads (D-07).</summary>
    public string DownloadDir => Settings.DownloadDir ?? Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Downloads\Wintainium");

    /// <summary>EmuDeck.exe: the setting when set (a folder or the exe itself), else the default place (L-07).</summary>
    public string EmuDeckExe
    {
        get
        {
            string? s = Settings.EmuDeckExe;
            if (string.IsNullOrWhiteSpace(s))
            {
                return Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\EmuDeck\EmuDeck.exe");
            }

            s = Environment.ExpandEnvironmentVariables(s);
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : Path.Combine(s, "EmuDeck.exe");
        }
    }

    /// <summary>
    /// Looks every app up again (Get-AppRows). Settings and the app list are read again first, so
    /// changes made by Setup, Scan or by hand show up. The lookup notes come after the table or
    /// grid is shown: call <see cref="WriteLookupNotes"/>.
    /// </summary>
    public async Task<IReadOnlyList<AppRow>> RefreshAsync(bool force, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        Settings = Settings.Load(Data.SettingsFile);
        List = AppList.Load(Data.AppsFile, Log);
        string? token = await Token.GetLookupTokenAsync(Http, Log, cancel).ConfigureAwait(false);
        var lookup = new ReleaseLookup(new ReleaseSources(Http, token), Cache, Settings.CacheMinutes);
        Rows = await new AppRows(lookup, probe, self).BuildAsync(List.Entries, Settings, force, progress, cancel).ConfigureAwait(false);
        return Rows;
    }

    /// <summary>Explains failed or fallback lookups of the last refresh (Show-LookupNotes).</summary>
    public void WriteLookupNotes() => LookupNotes.Write(Rows, Log, Token.Expires, DateTime.Now);

    /// <summary>What updates need: the HTTP client, log, dialogs and Windows, with this session's folders.</summary>
    public InstallContext InstallContext(IUserPrompts prompts, IShell shell, IThreatInfo? threats = null) => new()
    {
        Http = Http,
        Log = Log,
        Prompts = prompts,
        Shell = shell,
        Threats = threats,
        DownloadDir = DownloadDir,
        EmuDeckExe = EmuDeckExe,
    };

    public Updater Updater(IUserPrompts prompts, IShell shell, IThreatInfo? threats = null) =>
        new(InstallContext(prompts, shell, threats), List);

    /// <summary>Add app with this session's folders, looking releases up with the saved token.</summary>
    public AddApp AddApp(IUserPrompts prompts, IShell shell, IThreatInfo? threats = null)
    {
        InstallContext ctx = InstallContext(prompts, shell, threats);
        return new AddApp(ctx, new Updater(ctx, List), List, InstallRoot, async (entry, cancel) =>
        {
            string? token = await Token.GetLookupTokenAsync(Http, Log, cancel).ConfigureAwait(false);
            return await new ReleaseSources(Http, token).GetLatestAsync(entry, cancel).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The PC scan (Find-Sources -PassThru -SkipRepos), run off the calling thread. Extra folders
    /// are searched too; <paramref name="selfFolder"/> is where this Wintainium runs from.
    /// </summary>
    public async Task<ScanResult> ScanAsync(IEnumerable<string> extraRoots, string selfFolder, CancellationToken cancel = default)
    {
        string? token = await Token.GetLookupTokenAsync(Http, Log, cancel).ConfigureAwait(false);
        ScanOptions options = ScanOptions.ForThisPc(Settings, [.. List.Entries], selfFolder, [.. extraRoots], new GitHubRepoIds(Http, token));
        return await Task.Run(() => new PcScanner(options).Run(cancel), cancel).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (ownsHttp)
        {
            Http.Dispose();
        }
    }
}
