using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Wintainium.Core;
using Wintainium.Core.Logging;
using Wintainium.Core.Session;
using Wintainium.Core.Status;
using Wintainium.Core.Token;

namespace Wintainium.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            // an error nothing else caught goes to the log pane instead of closing Wintainium
            if (MainWindow is MainWindow w)
            {
                w.WindowLog.Write($"Error: {ex.Exception.Message}", LogColor.Red);
                ex.Handled = true;
            }
        };
        CommandLine cl = CommandLine.Parse(e.Args);
        if (cl.Error is not null)
        {
            if (!ConsoleOut.Attach())
            {
                MessageBox.Show($"{cl.Error}\n\n{CommandLine.Usage}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                Console.Error.WriteLine(cl.Error);
                Console.Error.WriteLine(CommandLine.Usage);
            }

            Shutdown(2);
            return;
        }

        string data = cl.Data is null ? AppContext.BaseDirectory : Path.GetFullPath(Environment.ExpandEnvironmentVariables(cl.Data));
        if (cl.List)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunListAsync(data);
            return;
        }

        if (cl.Uninstall || cl.InstallOnly)
        {
            // ported in PR 10
            MessageBox.Show("This option isn't in this build yet.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        var main = new MainWindow(data, setupFirst: cl.Setup);
        MainWindow = main;
        main.Show();
    }

    /// <summary>L-02: the table to the console Wintainium was started from, then exit.</summary>
    private async Task RunListAsync(string data)
    {
        ConsoleOut.Attach();
        var log = new ConsoleLog();
        int code = 0;
        try
        {
            log.Write("");
            log.Write($"{AppInfo.Name} {AppInfo.Version}", LogColor.Cyan);
            using AppSession s = AppSession.Open(data, log, SecretProtector.ForCurrentUser(), self: new StatusEngine.SelfInfo(AppContext.BaseDirectory, AppInfo.Version));
            IReadOnlyList<AppRow> rows = await Task.Run(() => s.RefreshAsync(false, new Ui.DirectProgress(log.Progress)));
            log.EndProgress();
            foreach ((string text, LogColor color) in RowViews.Table(rows))
            {
                log.Write(text, color);
            }

            s.WriteLookupNotes();
        }
#pragma warning disable CA1031 // any error is printed and gives exit code 1
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.EndProgress();
            log.Write($"Error: {ex.Message}", LogColor.Red);
            code = 1;
        }

        Console.Out.Flush();
        Shutdown(code);
    }
}

/// <summary>The console for --list: coloured lines, and progress on one line that is overwritten.</summary>
internal sealed class ConsoleLog : ILog
{
    private int progressWidth;

    public void Write(string text, LogColor color = LogColor.Default)
    {
        EndProgress();
        ConsoleColor before = Console.ForegroundColor;
        if (color != LogColor.Default && !Console.IsOutputRedirected)
        {
            Console.ForegroundColor = color switch
            {
                LogColor.Yellow => ConsoleColor.Yellow,
                LogColor.Red => ConsoleColor.Red,
                LogColor.Green => ConsoleColor.Green,
                LogColor.Cyan => ConsoleColor.Cyan,
                LogColor.DarkCyan => ConsoleColor.DarkCyan,
                LogColor.DarkGray => ConsoleColor.DarkGray,
                LogColor.Gray => ConsoleColor.Gray,
                _ => ConsoleColor.White,
            };
        }

        Console.WriteLine(text);
        if (!Console.IsOutputRedirected)
        {
            Console.ForegroundColor = before;
        }
    }

    public void Progress(string text)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        text = (text ?? "").Trim();
        Console.Write("\r" + text.PadRight(progressWidth));
        progressWidth = Math.Max(progressWidth, text.Length);
    }

    public void EndProgress()
    {
        if (progressWidth > 0)
        {
            Console.Write("\r" + new string(' ', progressWidth) + "\r");
            progressWidth = 0;
        }
    }
}

/// <summary>A window app has no console of its own; --list writes to the one it was started from.</summary>
internal static partial class ConsoleOut
{
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Attaches to the starting console first: in a window app, Console.IsOutputRedirected can't be
    /// trusted before that. Output redirected to a file or pipe works without a console.
    /// </summary>
    public static bool Attach() => AttachConsole(AttachParentProcess) || Console.IsOutputRedirected;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
