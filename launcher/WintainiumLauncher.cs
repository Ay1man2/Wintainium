// Wintainium.exe - lets people download one file, double-click it, and start using Wintainium.
//
// All of Wintainium's logic stays in the PowerShell scripts, which anyone can read. This exe
// carries a copy of them (the same files as the repo, added by build\Build-Release.ps1) and:
//
//   * When it sits in a Wintainium folder it can trust (the installed copy, a git clone, or an
//     untouched portable zip), it starts the Wintainium.ps1 there in Windows PowerShell with the
//     window ("gui"). A Wintainium.ps1 that merely sits next to it, say in Downloads, is ignored.
//   * Otherwise (downloaded on its own), it installs itself for the current user into
//     %LOCALAPPDATA%\Programs\Wintainium (no admin rights needed), adds an entry to
//     Settings > Apps so it can be uninstalled, and starts the installed copy. Running a newer
//     Wintainium.exe later updates that copy. Your app list, settings and token are never touched.
//
//   Wintainium.exe --uninstall      remove the installed copy (asks first)
//   Wintainium.exe --install-only   install or update, without starting it
//
// Built by build\Build-Release.ps1 with the C# compiler that ships with Windows (.NET Framework 4),
// so no SDK is needed. Written for C# 5 on purpose.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Wintainium")]
[assembly: AssemblyProduct("Wintainium")]
[assembly: AssemblyDescription("Obtainium-style app manager for Windows")]
[assembly: AssemblyCompany("Ay1man2")]
[assembly: AssemblyCopyright("GPL-3.0 - https://github.com/Ay1man2/Wintainium")]
[assembly: AssemblyVersion("0.0.0.0")]
[assembly: AssemblyFileVersion("0.0.0.0")]

static class Launcher
{
    const string Title = "Wintainium";
    const string PayloadPrefix = "payload/";
    // Written into the install folder; marks a copy this exe installed (and may update).
    const string Marker = "installed-version.txt";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Wintainium";

    static bool quiet;   // --install-only: no message boxes, report through the exit code

    static string Quote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    static void Say(string text, MessageBoxIcon icon)
    {
        if (!quiet) MessageBox.Show(text, Title, MessageBoxButtons.OK, icon);
    }

    static Version OwnVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version;
    }

    static string OwnPath()
    {
        return Assembly.GetExecutingAssembly().Location;
    }

    // The default install folder. WINTAINIUM_INSTALL_DIR overrides it; the tests use it, and it
    // also skips the Settings > Apps entry.
    static string InstallDir()
    {
        string over = Environment.GetEnvironmentVariable("WINTAINIUM_INSTALL_DIR");
        if (!string.IsNullOrEmpty(over)) return over;
        return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "Wintainium");
    }

    static List<string> PayloadNames()
    {
        List<string> names = new List<string>();
        foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (n.StartsWith(PayloadPrefix, StringComparison.Ordinal)) names.Add(n);
        return names;
    }

    static Version ReadMarker(string dir)
    {
        try
        {
            string f = Path.Combine(dir, Marker);
            if (!File.Exists(f)) return null;
            return new Version(File.ReadAllText(f).Trim());
        }
        catch { return null; }
    }

    // True when every file this exe carries is in dir with exactly the same bytes: an untouched
    // portable zip. Comparing all of them matters, because Wintainium.ps1 runs Find-Sources.ps1 and
    // reads known-apps.json from its own folder.
    static bool MatchesPayload(string dir)
    {
        List<string> names = PayloadNames();
        if (names.Count == 0) return false;
        Assembly asm = Assembly.GetExecutingAssembly();
        foreach (string name in names)
        {
            string file = Path.Combine(dir, name.Substring(PayloadPrefix.Length).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return false;
            byte[] want;
            using (Stream src = asm.GetManifestResourceStream(name))
            using (MemoryStream mem = new MemoryStream())
            {
                src.CopyTo(mem);
                want = mem.ToArray();
            }
            byte[] have = File.ReadAllBytes(file);
            if (have.Length != want.Length) return false;
            for (int i = 0; i < have.Length; i++) if (have[i] != want[i]) return false;
        }
        return true;
    }

    // Whether the Wintainium.ps1 next to this exe may be run. Only in the installed copy (the install
    // folder, with its marker), a git clone, or an untouched portable zip. Anything else, like a
    // Wintainium.ps1 a website dropped into Downloads, is not run, even with a marker file beside it.
    static bool TrustedScriptFolder(string here)
    {
        if (SameFolder(here, InstallDir()) && ReadMarker(here) != null) return true;
        if (IsGitCheckout(here)) return true;
        return MatchesPayload(here);
    }

    // A git clone has a .git folder with HEAD in it. A .git *file* (git worktrees and submodules
    // use one, saying "gitdir: <path>") is not trusted: a website can drop a file named .git into
    // Downloads, and the path inside it could be a network share or any other repo on this PC.
    // Worktrees and submodules start the installed copy instead.
    static bool IsGitCheckout(string here)
    {
        return LooksLikeGitDir(Path.Combine(here, ".git"));
    }

    static bool LooksLikeGitDir(string dir)
    {
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "HEAD"));
    }

    static bool SameFolder(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // True while that Wintainium.exe is running (Windows keeps a running exe locked).
    static bool InUse(string exe)
    {
        if (!File.Exists(exe)) return false;
        try
        {
            using (FileStream fs = File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // Writes the scripts this exe carries into dir. Files that aren't part of Wintainium
    // (your app list, settings, token, cache) are never written or removed.
    static void WritePayload(string dir)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        foreach (string name in PayloadNames())
        {
            string rel = name.Substring(PayloadPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using (Stream src = asm.GetManifestResourceStream(name))
            using (FileStream dst = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                src.CopyTo(dst);
            }
        }
    }

    // Installs or updates the copy in dir. Returns false if the user cancelled or it failed.
    static bool Install(string dir)
    {
        string target = Path.Combine(dir, "Wintainium.exe");
        while (InUse(target))
        {
            if (quiet) return false;
            DialogResult r = MessageBox.Show(
                "Wintainium is open. Close it, then click Retry to finish updating to version " + Short(OwnVersion()) + ".",
                Title, MessageBoxButtons.RetryCancel, MessageBoxIcon.Information);
            if (r != DialogResult.Retry) return false;
        }
        try
        {
            Directory.CreateDirectory(dir);
            WritePayload(dir);
            // Written as a new file rather than copied, so the "downloaded from the internet"
            // mark on the file you downloaded isn't carried over to the installed copy.
            File.WriteAllBytes(target, File.ReadAllBytes(OwnPath()));
            File.WriteAllText(Path.Combine(dir, Marker), OwnVersion().ToString());
            Register(dir, target);
            return true;
        }
        catch (Exception ex)
        {
            Say("Wintainium could not be installed to\n" + dir + "\n\n" + ex.Message, MessageBoxIcon.Error);
            return false;
        }
    }

    static string Short(Version v)
    {
        return v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
    }

    // Settings > Apps entry (per user, so no admin rights are needed). Skipped for test installs.
    static void Register(string dir, string exe)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINTAINIUM_INSTALL_DIR"))) return;
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Wintainium");
                k.SetValue("DisplayVersion", Short(OwnVersion()));
                k.SetValue("Publisher", "Ay1man2");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", Quote(exe) + " --uninstall");
                k.SetValue("URLInfoAbout", "https://github.com/Ay1man2/Wintainium");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = 0;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) kb += new FileInfo(f).Length / 1024;
                k.SetValue("EstimatedSize", (int)Math.Max(kb, 1), RegistryValueKind.DWord);
            }
        }
        catch { /* the entry is a convenience; Wintainium works without it */ }
    }

    // Removes the installed copy. Your apps themselves are never touched.
    static int Uninstall()
    {
        // Only ever the install folder: a marker file elsewhere doesn't make a folder an install.
        string dir = InstallDir();
        if (ReadMarker(dir) == null)
        {
            Say("No installed copy of Wintainium was found in\n" + InstallDir(), MessageBoxIcon.Information);
            return 1;
        }
        string installedExe = Path.Combine(dir, "Wintainium.exe");
        while (OtherInstanceRunning(installedExe))
        {
            DialogResult r = MessageBox.Show("Wintainium is open. Close it, then click Retry to remove it.",
                                             Title, MessageBoxButtons.RetryCancel, MessageBoxIcon.Information);
            if (r != DialogResult.Retry) return 1;
        }
        DialogResult ok = MessageBox.Show(
            "Remove Wintainium from this PC?\n\nThe apps it tracks are not touched; only Wintainium itself is removed.",
            Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return 1;
        DialogResult data = MessageBox.Show(
            "Also delete your app list, settings, saved GitHub token and cache?\n\n" +
            "Choose No to keep them in\n" + dir + "\nso a later install picks up where you left off.",
            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        RemoveShortcuts(dir);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey); } catch { }

        // This exe is running, so a short-lived cmd.exe removes the files once it has exited.
        StringBuilder cmd = new StringBuilder("/c ping -n 3 127.0.0.1 >nul");
        if (data == DialogResult.Yes)
        {
            cmd.Append(" & rmdir /s /q " + Quote(dir));
        }
        else
        {
            foreach (string name in PayloadNames())
            {
                string rel = name.Substring(PayloadPrefix.Length).Replace('/', '\\');
                cmd.Append(" & del /f /q " + Quote(Path.Combine(dir, rel)));
            }
            cmd.Append(" & del /f /q " + Quote(Path.Combine(dir, Marker)));
            cmd.Append(" & del /f /q " + Quote(Path.Combine(dir, "Wintainium.exe")));
            cmd.Append(" & rmdir " + Quote(Path.Combine(dir, "assets")));
        }
        ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd.ToString());
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = Path.GetTempPath();
        Process.Start(psi);
        Say("Wintainium has been removed.", MessageBoxIcon.Information);
        return 0;
    }

    // True if another process is running that exe (an open Wintainium window).
    static bool OtherInstanceRunning(string exe)
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (Process p in Process.GetProcessesByName("Wintainium"))
        {
            try
            {
                if (p.Id != me && string.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return false;
    }

    // Deletes the Desktop and Start menu shortcuts that point into dir (the ones Setup made).
    static void RemoveShortcuts(string dir)
    {
        string[] folders = {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        object shell = Activator.CreateInstance(shellType);
        foreach (string folder in folders)
        {
            string lnk = Path.Combine(folder, "Wintainium.lnk");
            if (!File.Exists(lnk)) continue;
            try
            {
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                if (!string.IsNullOrEmpty(target) && SameFolder(Path.GetDirectoryName(target), dir)) File.Delete(lnk);
            }
            catch { }
        }
    }

    // Starts Wintainium.ps1 from dir in Windows PowerShell and waits, showing any startup error.
    static int Run(string dir, string[] args)
    {
        string script = Path.Combine(dir, "Wintainium.ps1");
        string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        StringBuilder rest = new StringBuilder();
        if (args.Length == 0) rest.Append("gui");
        foreach (string a in args) { if (rest.Length > 0) rest.Append(' '); rest.Append(Quote(a)); }

        ProcessStartInfo psi = new ProcessStartInfo(ps,
            "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File " + Quote(script) + " " + rest);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = dir;
        psi.RedirectStandardError = true;

        StringBuilder err = new StringBuilder();
        try
        {
            Process p = Process.Start(psi);
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0 && err.Length > 0)
            {
                string text = err.ToString();
                if (text.Length > 1500) text = text.Substring(0, 1500) + "...";
                Say("Wintainium stopped with an error:\n\n" + text, MessageBoxIcon.Error);
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Say("Could not start Windows PowerShell:\n\n" + ex.Message, MessageBoxIcon.Error);
            return 1;
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        List<string> rest = new List<string>(args);
        bool installOnly = rest.Remove("--install-only");
        quiet = installOnly;
        if (rest.Remove("--uninstall")) return Uninstall();

        string here = AppDomain.CurrentDomain.BaseDirectory;
        bool carriesScripts = PayloadNames().Count > 0;

        // 1. In a Wintainium folder it can trust (installed copy, git clone, untouched portable zip):
        //    run the scripts there. A launcher without scripts inside can't check, so it runs them as before.
        if (File.Exists(Path.Combine(here, "Wintainium.ps1")) && (!carriesScripts || TrustedScriptFolder(here)))
        {
            // The installed copy refreshes its own scripts if this exe is newer than them
            // (only where this exe installed them, never in a zip folder or a git clone).
            Version mark = ReadMarker(here);
            if (carriesScripts && mark != null && OwnVersion() > mark)
            {
                try { WritePayload(here); File.WriteAllText(Path.Combine(here, Marker), OwnVersion().ToString()); Register(here, OwnPath()); }
                catch (Exception ex) { Say("Wintainium could not update its files:\n\n" + ex.Message, MessageBoxIcon.Warning); }
            }
            if (installOnly) return 0;
            return Run(here, rest.ToArray());
        }

        // 2. Downloaded on its own (or next to files it doesn't trust): install (or update) the
        //    per-user copy from the scripts inside this exe, then start it.
        if (!carriesScripts)
        {
            Say("Wintainium.ps1 was not found next to Wintainium.exe.\n\n" +
                "Keep Wintainium.exe in the same folder as the other files from the zip,\n" +
                "or download the single-file Wintainium.exe from the releases page.", MessageBoxIcon.Error);
            return 1;
        }
        string dir = InstallDir();
        Version installed = ReadMarker(dir);
        bool needed = installed == null || OwnVersion() > installed || !File.Exists(Path.Combine(dir, "Wintainium.ps1"));
        if (needed && !Install(dir)) return 1;
        if (installOnly) return 0;

        // Start the installed copy and let this one exit, so the download can be deleted.
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(dir, "Wintainium.exe"), string.Join(" ", rest.ConvertAll(Quote).ToArray()));
            psi.UseShellExecute = false;
            psi.WorkingDirectory = dir;
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Say("Wintainium was installed to\n" + dir + "\nbut could not be started:\n\n" + ex.Message, MessageBoxIcon.Error);
            return 1;
        }
    }
}
