using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;
using Wintainium.Core;
using Wintainium.Core.SelfInstall;

namespace Wintainium.App.SelfSetup;

/// <summary>
/// What the v0.3.0 launcher did before Wintainium opened: install or update the per-user copy,
/// --install-only and --uninstall (I-01 to I-11).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SelfStart
{
    private static Version Own => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// A published Wintainium.exe is one file; a development build has Wintainium.dll beside it and
    /// always runs in place.
    /// </summary>
    private static bool SingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "Wintainium.dll"));

    /// <summary>
    /// Installs or starts the installed copy when needed. Returns null when this process should go
    /// on to open the window here, otherwise the exit code.
    /// </summary>
    public static int? Run(IReadOnlyList<string> args, bool installOnly)
    {
        string here = AppContext.BaseDirectory;
        string dir = SelfInstaller.InstallDir();
        string? ownExe = Environment.ProcessPath;
        switch (SelfInstaller.Decide(here, dir, SingleFile, Own))
        {
            case StartMode.RunHere:
                RefreshInstalledCopy(here, dir, ownExe);
                return installOnly ? 0 : null;
            case StartMode.InstallAndStart:
                if (ownExe is null || !Install(ownExe, dir, installOnly))
                {
                    return 1;
                }

                break;
        }

        return installOnly ? 0 : StartInstalled(dir, args);
    }

    /// <summary>The installed copy, replaced by hand with a newer exe, updates its marker and Settings > Apps entry.</summary>
    private static void RefreshInstalledCopy(string here, string dir, string? ownExe)
    {
        if (ownExe is null || !SelfInstaller.SameFolder(here, dir))
        {
            return;
        }

        Version? mark = SelfInstaller.ReadMarker(here);
        if (mark is null || Own <= mark)
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(here, SelfInstaller.Marker), Own.ToString());
            SelfInstaller.RemoveLegacyFiles(here);
            WindowsIntegration.Register(here, ownExe, Own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Wintainium could not update its files:\n\n" + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>I-01 to I-04: installs or updates the copy in the install folder. False when cancelled or failed.</summary>
    private static bool Install(string ownExe, string dir, bool quiet)
    {
        string target = Path.Combine(dir, SelfInstaller.ExeName);
        while (SelfInstaller.InUse(target))
        {
            if (quiet)
            {
                return false;
            }

            MessageBoxResult r = MessageBox.Show(
                $"Wintainium is open. Close it, then click OK to finish updating to version {SelfInstaller.ShortVersion(Own)}.",
                AppInfo.Name,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (r != MessageBoxResult.OK)
            {
                return false;
            }
        }

        try
        {
            string exe = SelfInstaller.Install(ownExe, dir, Own);
            WindowsIntegration.Register(dir, exe, Own);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!quiet)
            {
                MessageBox.Show($"Wintainium could not be installed to\n{dir}\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
    }

    /// <summary>Starts the installed copy and lets this one exit, so the download can be deleted.</summary>
    private static int StartInstalled(string dir, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(dir, SelfInstaller.ExeName)) { UseShellExecute = false, WorkingDirectory = dir };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(psi);
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            MessageBox.Show($"Wintainium was installed to\n{dir}\nbut could not be started:\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>I-06: removes the installed copy (asks first). The apps it tracks are never touched.</summary>
    public static int Uninstall()
    {
        // only ever the install folder: a marker file elsewhere doesn't make a folder an install
        string dir = SelfInstaller.InstallDir();
        if (SelfInstaller.ReadMarker(dir) is null)
        {
            MessageBox.Show("No installed copy of Wintainium was found in\n" + dir, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        string installedExe = Path.Combine(dir, SelfInstaller.ExeName);
        while (WindowsIntegration.OtherInstanceRunning(installedExe))
        {
            if (MessageBox.Show("Wintainium is open. Close it, then click OK to remove it.", AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            {
                return 1;
            }
        }

        if (MessageBox.Show(
                "Remove Wintainium from this PC?\n\nThe apps it tracks are not touched; only Wintainium itself is removed.",
                AppInfo.Name,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return 1;
        }

        bool deleteData = MessageBox.Show(
            "Also delete your app list, settings, saved GitHub token and cache?\n\n" +
            $"Choose No to keep them in\n{dir}\nso a later install picks up where you left off.",
            AppInfo.Name,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

        WindowsIntegration.RemoveShortcuts(dir);
        WindowsIntegration.Unregister();

        // this exe may be the installed one, so a short-lived cmd.exe removes the files once it has exited
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), SelfInstaller.RemovalCommand(dir, deleteData))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        using (Process.Start(psi))
        {
        }

        MessageBox.Show("Wintainium has been removed.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }
}
