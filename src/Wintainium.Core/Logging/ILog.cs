namespace Wintainium.Core.Logging;

/// <summary>Colours of log lines; the window maps them to the palette (yellow #F9E2AF, red #F38BA8, ...).</summary>
public enum LogColor
{
    Default,
    Yellow,
    Red,
    Green,
    Cyan,
    DarkCyan,
    DarkGray,
    Gray,
    White,
}

/// <summary>Where the app's messages go: the log pane in the window, the console for --list, a list in tests.</summary>
public interface ILog
{
    void Write(string text, LogColor color = LogColor.Default);

    /// <summary>A progress line that replaces the previous one (the status bar in the window).</summary>
    void Progress(string text);
}

/// <summary>Collects messages; used by tests and anywhere output should be inspected.</summary>
public sealed class MemoryLog : ILog
{
    private readonly List<(string Text, LogColor Color)> lines = [];

    public IReadOnlyList<(string Text, LogColor Color)> Lines => lines;

    public string? LastProgress { get; private set; }

    public string AllText => string.Join("\n", lines.Select(l => l.Text));

    public void Write(string text, LogColor color = LogColor.Default) => lines.Add((text, color));

    public void Progress(string text) => LastProgress = text;
}

/// <summary>Discards everything.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(string text, LogColor color = LogColor.Default) { }

    public void Progress(string text) { }
}
