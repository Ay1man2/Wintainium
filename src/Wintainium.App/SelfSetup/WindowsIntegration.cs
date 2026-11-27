using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Wintainium.Core;
using Wintainium.Core.Logging;
using Wintainium.Core.SelfInstall;

namespace Wintainium.App.SelfSetup;

/// <summary>The taskbar ID, shortcuts and the Settings > Apps entry.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsIntegration
{
    /// <summary>The taskbar ID v0.3.0 used, so pinned shortcuts keep working (H-01).</summary>
    public const string AppId = "Ay1man2.Wintainium";

    public const string ShortcutDescription = "Wintainium: keep hand-installed apps up to date";

    /// <summary>H-01: this process and its windows share the shortcuts' taskbar button.</summary>
    public static void SetProcessAppId()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppId);
        }
        catch (EntryPointNotFoundException)
        {
            // very old Windows: one button per process is still fine
        }
    }

    /// <summary>N-07 / H-01 / H-02: Desktop and Start menu shortcuts to this exe, with its icon and taskbar ID.</summary>
    public static void CreateShortcuts(bool desktop, bool startMenu, ILog log)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            log.Write("Could not create shortcuts: Wintainium's own location is unknown.", LogColor.Yellow);
            return;
        }

        foreach (string folder in ShortcutFolders(desktop, startMenu))
        {
            if (folder.Length == 0)
            {
                log.Write("Could not create a shortcut: Windows reports no folder for it.", LogColor.Yellow);
                continue;
            }

            try
            {
                ShellLink.Write(Path.Combine(folder, "Wintainium.lnk"), exe, "", Path.GetDirectoryName(exe) ?? "", ShortcutDescription, exe, AppId);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                log.Write($"Could not create a shortcut in {folder}: {ex.Message}", LogColor.Yellow);
            }
        }
    }

    /// <summary>I-06: deletes the Desktop and Start menu shortcuts that point into the install folder.</summary>
    public static void RemoveShortcuts(string dir)
    {
        foreach (string folder in ShortcutFolders(true, true))
        {
            string lnk = Path.Combine(folder, "Wintainium.lnk");
            if (!File.Exists(lnk))
            {
                continue;
            }

            try
            {
                string? target = ShellLink.Target(lnk);
                if (!string.IsNullOrEmpty(target) && SelfInstaller.SameFolder(Path.GetDirectoryName(target) ?? "", dir))
                {
                    File.Delete(lnk);
                }
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                // a shortcut left behind only points at nothing
            }
        }
    }

    /// <summary>I-05: the Settings > Apps entry, per user. Skipped for test installs.</summary>
    public static void Register(string dir, string exe, Version version)
    {
        if (SelfInstaller.IsTestInstall())
        {
            return;
        }

        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(SelfInstaller.UninstallKey);
            k.SetValue("DisplayName", AppInfo.Name);
            k.SetValue("DisplayVersion", SelfInstaller.ShortVersion(version));
            k.SetValue("Publisher", "Ay1man2");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", SelfInstaller.Quote(exe) + " --uninstall");
            k.SetValue("URLInfoAbout", "https://github.com/" + AppInfo.Repo);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", SelfInstaller.SizeKb(dir), RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // the entry is a convenience; Wintainium works without it
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(SelfInstaller.UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // nothing more to do
        }
    }

    /// <summary>True if another process is running that exe (an open Wintainium window).</summary>
    public static bool OtherInstanceRunning(string exe)
    {
        int me = Environment.ProcessId;
        foreach (Process p in Process.GetProcessesByName("Wintainium"))
        {
            using (p)
            {
                try
                {
                    if (p.Id != me && p.MainModule?.FileName is string f && SelfInstaller.SameFolder(f, exe))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // ended meanwhile, or not ours to look at
                }
            }
        }

        return false;
    }

    private static List<string> ShortcutFolders(bool desktop, bool startMenu)
    {
        var folders = new List<string>();
        if (desktop)
        {
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        }

        if (startMenu)
        {
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        }

        return folders;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
