using Wintainium.Core.Install;
using Wintainium.Core.Logging;

namespace Wintainium.Tests.Helpers;

/// <summary>Answers questions from a script and records them.</summary>
public sealed class FakePrompts : IUserPrompts
{
    public Queue<bool> Confirms { get; } = new();

    public Queue<int?> Choices { get; } = new();

    public Queue<YesNoStop> YesNoStops { get; } = new();

    public List<string> Asked { get; } = [];

    public bool Confirm(string question)
    {
        Asked.Add(question);
        return Confirms.Count > 0 && Confirms.Dequeue();
    }

    public int? Choose(string title, IReadOnlyList<string> items, string prompt)
    {
        Asked.Add(title + ": " + string.Join(" | ", items));
        return Choices.Count > 0 ? Choices.Dequeue() : null;
    }

    public YesNoStop AskYesNoStop(string question)
    {
        Asked.Add(question);
        return YesNoStops.Count > 0 ? YesNoStops.Dequeue() : YesNoStop.No;
    }
}

/// <summary>Records what would have been handed to Windows.</summary>
public sealed class FakeShell : IShell
{
    public List<string> Opened { get; } = [];

    public List<string> Started { get; } = [];

    public List<string> Shown { get; } = [];

    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void OpenUrl(Uri link) => Opened.Add(link.AbsoluteUri);

    public void Start(string file, string workingDirectory) => Started.Add(file);

    public void ShowInExplorer(string file) => Shown.Add(file);

    public void OpenFolder(string folder) => Shown.Add(folder);

    public bool IsRunning(string processName) => Running.Contains(processName);
}

/// <summary>Download marks kept in memory, so they can be checked on any OS.</summary>
public sealed class FakeMark : IFileMark
{
    public Dictionary<string, string> Marks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Mark(string file, string hostUrl) => Marks[Path.GetFullPath(file)] = $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={hostUrl}\r\n";

    public string? Read(string file) => Marks.GetValueOrDefault(Path.GetFullPath(file));
}

public sealed class FakeThreats(ThreatDetection? detection, bool needsAdmin = false, Exception? fails = null) : IThreatInfo
{
    public ThreatDetection? FindRecent(string fileName) =>
        fails is not null ? throw fails : needsAdmin ? throw new UnauthorizedAccessException() : detection;
}

/// <summary>Records the program and arguments; runs an action in its place.</summary>
public sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessOutcome> run) : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Runs { get; } = [];

    public Task<ProcessOutcome> RunAsync(string file, IReadOnlyList<string> arguments, Action tick, TimeSpan tickEvery, CancellationToken cancel = default)
    {
        Runs.Add((file, arguments));
        tick();
        return Task.FromResult(run(file, arguments));
    }
}

/// <summary>An install context wired to fakes and a temp folder.</summary>
public sealed class InstallRig : IDisposable
{
    public InstallRig(FakeHttp? http = null)
    {
        Http = http ?? new FakeHttp();
        Client = Http.Client();
        Ctx = new InstallContext
        {
            Http = Client,
            Log = Log,
            Prompts = Prompts,
            Shell = Shell,
            Mark = Mark,
            Threats = Threats,
            Processes = Runner,
            DownloadDir = Dir.File("dl"),
            EmuDeckExe = Dir.File("EmuDeck/EmuDeck.exe"),
            EmuDeckData = Dir.File("AppData/EmuDeck"),
            TempDir = Dir.File("tmp"),
            Delay = (_, _) => Task.CompletedTask,
            Now = () => Now,
        };
        Directory.CreateDirectory(Ctx.TempDir);
    }

    public TempDir Dir { get; } = new();

    public FakeHttp Http { get; }

    public HttpClient Client { get; }

    public MemoryLog Log { get; } = new();

    public FakePrompts Prompts { get; } = new();

    public FakeShell Shell { get; } = new();

    public FakeMark Mark { get; } = new();

    public FakeThreats Threats { get; set; } = new(null);

    public FakeRunner Runner { get; set; } = new((_, _) => new ProcessOutcome(0, ""));

    public DateTime Now { get; set; } = new(2026, 11, 27, 14, 30, 0);

    public InstallContext Ctx { get; private set; }

    /// <summary>Rebuilds the context after a fake was swapped.</summary>
    public InstallContext Rebuild()
    {
        Ctx = new InstallContext
        {
            Http = Client,
            Log = Log,
            Prompts = Prompts,
            Shell = Shell,
            Mark = Mark,
            Threats = Threats,
            Processes = Runner,
            DownloadDir = Ctx.DownloadDir,
            EmuDeckExe = Ctx.EmuDeckExe,
            EmuDeckData = Ctx.EmuDeckData,
            TempDir = Ctx.TempDir,
            Delay = Ctx.Delay,
            Now = () => Now,
        };
        return Ctx;
    }

    public void Dispose()
    {
        Client.Dispose();
        Http.Dispose();
        Dir.Dispose();
    }
}
