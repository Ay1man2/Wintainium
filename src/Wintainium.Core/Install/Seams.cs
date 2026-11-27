using System.Diagnostics;
using System.Runtime.Versioning;

namespace Wintainium.Core.Install;

/// <summary>Questions to the user (Read-Host in v0.3.0). The window shows them as dialogs.</summary>
public interface IUserPrompts
{
    /// <summary>A yes/no question where closing the dialog means No.</summary>
    bool Confirm(string question);

    /// <summary>A numbered choice. Returns the chosen index, or null when cancelled.</summary>
    int? Choose(string title, IReadOnlyList<string> items, string prompt);

    /// <summary>Yes / No / Stop, for Update all. Closing the dialog means No.</summary>
    YesNoStop AskYesNoStop(string question);
}

public enum YesNoStop
{
    No,
    Yes,
    Stop,
}

/// <summary>Things handed to Windows: links, programs, Explorer.</summary>
public interface IShell
{
    /// <summary>Opens an https link that was already checked.</summary>
    void OpenUrl(Uri link);

    void Start(string file, string workingDirectory);

    /// <summary>Opens Explorer with the file selected.</summary>
    void ShowInExplorer(string file);

    void OpenFolder(string folder);

    bool IsRunning(string processName);
}

/// <summary>Windows' "downloaded from the internet" mark (the Zone.Identifier stream).</summary>
public interface IFileMark
{
    void Mark(string file, string hostUrl);

    /// <summary>The mark's text, or null when the file has none.</summary>
    string? Read(string file);
}

/// <summary>What Windows Security detected in a file.</summary>
public sealed record ThreatDetection(string ThreatName, long ThreatId);

/// <summary>Reads Windows Security's recent detections. Throws UnauthorizedAccessException when it needs admin rights.</summary>
public interface IThreatInfo
{
    ThreatDetection? FindRecent(string fileName);
}

/// <summary>How a started program ended.</summary>
public sealed record ProcessOutcome(int ExitCode, string Errors);

/// <summary>Runs a program and calls <c>tick</c> while it works.</summary>
public interface IProcessRunner
{
    Task<ProcessOutcome> RunAsync(string file, IReadOnlyList<string> arguments, Action tick, TimeSpan tickEvery, CancellationToken cancel = default);
}

/// <summary>The real shell.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShell : IShell
{
    public void OpenUrl(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        using Process? p = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
    }

    public void Start(string file, string workingDirectory)
    {
        using Process? p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = workingDirectory });
    }

    public void ShowInExplorer(string file)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/select," + file);
        using Process? p = Process.Start(psi);
    }

    public void OpenFolder(string folder)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(folder);
        using Process? p = Process.Start(psi);
    }

    public bool IsRunning(string processName)
    {
        Process[] found = Process.GetProcessesByName(processName);
        foreach (Process p in found)
        {
            p.Dispose();
        }

        return found.Length > 0;
    }
}

/// <summary>The mark as an NTFS stream, the way browsers and v0.3.0 write it.</summary>
public sealed class ZoneIdentifierMark : IFileMark
{
    public void Mark(string file, string hostUrl)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(file + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={hostUrl}\r\n");
        }
    }

    public string? Read(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.ReadAllText(file + ":Zone.Identifier");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Starts a program and waits, calling back on a timer.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Told the process ID once it started (for tests).</summary>
    internal Action<int>? Started { get; init; }

    public async Task<ProcessOutcome> RunAsync(string file, IReadOnlyList<string> arguments, Action tick, TimeSpan tickEvery, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(tick);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        // Windows PowerShell must not pick up this process's module path
        psi.Environment.Remove("PSModulePath");
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        Started?.Invoke(p.Id);
        Task<string> errors = p.StandardError.ReadToEndAsync(CancellationToken.None);
        Task<string> output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task exited = p.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (await Task.WhenAny(exited, Task.Delay(tickEvery, cancel)).ConfigureAwait(false) != exited)
            {
                cancel.ThrowIfCancellationRequested();
                tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel stops the work too: 7-Zip, or EmuDeck's PowerShell and whatever it started
            KillTree(p);
            throw;
        }

        await exited.ConfigureAwait(false);
        await output.ConfigureAwait(false);
        return new ProcessOutcome(p.ExitCode, (await errors.ConfigureAwait(false)).Trim());
    }

    internal static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // it ended on its own in the meantime
        }
    }
}
