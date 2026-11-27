using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wintainium.Core.Logging;

namespace Wintainium.App.Ui;

/// <summary>One line in the log pane.</summary>
public sealed record LogLine(string Text, Brush Brush);

/// <summary>
/// The log pane and status bar (the Write-Host override of v0.3.0). Lines can come from any
/// thread; they are added on the window's thread in the order they were written.
/// </summary>
public sealed class WindowLog : ILog
{
    private const int ContextLines = 12;

    private readonly Dispatcher dispatcher;
    private readonly ScrollViewer scroll;
    private readonly TextBlock status;
    private readonly List<string> promptContext = [];

    public WindowLog(Dispatcher dispatcher, ScrollViewer scroll, TextBlock status)
    {
        this.dispatcher = dispatcher;
        this.scroll = scroll;
        this.status = status;
    }

    public ObservableCollection<LogLine> Lines { get; } = [];

    public void Write(string text, LogColor color = LogColor.Default)
    {
        text ??= "";
        if (text.Trim().Length == 0)
        {
            return;
        }

        OnUi(() =>
        {
            string line = text.TrimEnd();
            Lines.Add(new LogLine(line, Palette.For(color)));
            promptContext.Add(line);
            scroll.ScrollToEnd();
        });
    }

    public void Progress(string text) => OnUi(() => status.Text = (text ?? "").Trim());

    /// <summary>Sets the status bar ("Ready" or a busy label).</summary>
    public void SetStatus(string text) => Progress(text);

    /// <summary>
    /// The last 12 lines written since the previous question (or more, so a long numbered list
    /// shows whole), then forgets them (Show-Prompt).
    /// </summary>
    internal string TakePromptContext(int atLeast = ContextLines)
    {
        string ctx = string.Join("\n", promptContext.TakeLast(Math.Max(ContextLines, atLeast)));
        promptContext.Clear();
        return ctx;
    }

    private void OnUi(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            // Invoke, not BeginInvoke: a question asked right after a line must see that line
            dispatcher.Invoke(action);
        }
    }
}

/// <summary>The window's colours for log lines and the summary (v0.3.0 colorMap).</summary>
public static class Palette
{
    public static readonly Brush Default = Make("#CDD6F4");
    public static readonly Brush Green = Make("#A6E3A1");
    public static readonly Brush Amber = Make("#F9E2AF");
    public static readonly Brush Red = Make("#F38BA8");

    private static readonly Dictionary<LogColor, Brush> Map = new()
    {
        [LogColor.Default] = Default,
        [LogColor.Yellow] = Amber,
        [LogColor.Red] = Red,
        [LogColor.Green] = Green,
        [LogColor.Cyan] = Make("#89DCEB"),
        [LogColor.DarkCyan] = Make("#74C7EC"),
        [LogColor.DarkGray] = Make("#6C7086"),
        [LogColor.Gray] = Make("#BAC2DE"),
        [LogColor.White] = Make("#FFFFFF"),
    };

    public static Brush For(LogColor color) => Map.GetValueOrDefault(color, Default);

    private static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
