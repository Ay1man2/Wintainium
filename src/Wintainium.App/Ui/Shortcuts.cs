using System.IO;
using System.Runtime.Versioning;
using Wintainium.Core.Logging;

namespace Wintainium.App.Ui;

/// <summary>Desktop and Start menu shortcuts to this Wintainium.exe (Complete-Wizard).</summary>
[SupportedOSPlatform("windows")]
public static class Shortcuts
{
    public const string Description = "Wintainium: keep hand-installed apps up to date";

    /// <summary>Creates the ticked shortcuts. A failure is logged and the rest still happens (N-07).</summary>
    public static void Create(bool desktop, bool startMenu, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var places = new List<string>();
        if (desktop)
        {
            places.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        }

        if (startMenu)
        {
            places.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        }

        foreach (string folder in places)
        {
            try
            {
                Write(Path.Combine(folder, "Wintainium.lnk"), exe);
            }
#pragma warning disable CA1031 // a shortcut that can't be made is reported, not fatal
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.Write($"Could not create a shortcut in {folder}: {ex.Message}", LogColor.Yellow);
            }
        }
    }

    private static void Write(string lnk, string exe)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = exe;
            link.Arguments = "";
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            link.Description = Description;
            link.IconLocation = exe + ",0"; // the exe's own icon, never a loose icon.ico (H-02)
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
