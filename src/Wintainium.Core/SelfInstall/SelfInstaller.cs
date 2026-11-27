using System.Globalization;
using System.Text;

namespace Wintainium.Core.SelfInstall;

/// <summary>What Wintainium.exe does when it starts, before any window opens (the launcher's Main).</summary>
public enum StartMode
{
    /// <summary>Run from this folder: the installed copy, a portable copy, or a development build.</summary>
    RunHere,

    /// <summary>Downloaded on its own: install (or update) the per-user copy, then start that one.</summary>
    InstallAndStart,

    /// <summary>Downloaded on its own, but the installed copy is already this version or newer: just start it.</summary>
    StartInstalled,
}

/// <summary>
/// The per-user install of Wintainium.exe (I-01 to I-11): where it goes, when to install, and which
/// files are Wintainium's own. The app list, settings, token and cache are never written or removed here.
/// </summary>
public static class SelfInstaller
{
    public const string ExeName = "Wintainium.exe";

    /// <summary>Written into the install folder; marks a copy this exe installed (and may update).</summary>
    public const string Marker = "installed-version.txt";

    /// <summary>Next to a portable copy: run in place and never install (I-08).</summary>
    public const string PortableMarker = "portable.txt";

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Wintainium";

    /// <summary>Files a v0.3.0 install carried next to its launcher; replaced by the single exe (I-11).</summary>
    public static readonly IReadOnlyList<string> LegacyFiles =
    [
        "Find-Sources.ps1",
        "known-apps.json",
        "wintainium-apps.example.json",
        "LICENSE",
        Path.Combine("assets", "icon.ico"),
    ];

    /// <summary>
    /// Kept in the install folder in place of the v0.3.0 script: a v0.3.0 Wintainium.exe reinstalls
    /// itself when Wintainium.ps1 is missing, which would downgrade this install.
    /// </summary>
    public const string StubScript = "Wintainium.ps1";

    public const string StubText =
        "# Wintainium is now Wintainium.exe in this folder. This file only tells older versions\r\n" +
        "# that Wintainium is installed here, so they don't replace the newer one.\r\n" +
        "Write-Host 'Start Wintainium.exe instead.'\r\n" +
        "exit 1\r\n";

    /// <summary>A v0.3.0 portable zip, unpacked: it has no portable.txt, but these files say what it is.</summary>
    public static readonly IReadOnlyList<string> V030PortableFiles = ["Wintainium.ps1", "Find-Sources.ps1", "known-apps.json"];

    /// <summary>The user's own files; only deleted when they say so on uninstall.</summary>
    public static readonly IReadOnlyList<string> UserFiles =
    [
        "wintainium-apps.json",
        "wintainium-apps.json.bak",
        "wintainium-settings.json",
        "wintainium-token.xml",
        "wintainium-cache.json",
    ];

    /// <summary>
    /// %LOCALAPPDATA%\Programs\Wintainium. WINTAINIUM_INSTALL_DIR overrides it (tests); an
    /// overridden install also skips the Settings > Apps entry.
    /// </summary>
    public static string InstallDir(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string? over = env("WINTAINIUM_INSTALL_DIR");
        if (!string.IsNullOrEmpty(over))
        {
            return over;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Wintainium");
    }

    public static bool IsTestInstall(Func<string, string?>? env = null) =>
        !string.IsNullOrEmpty((env ?? Environment.GetEnvironmentVariable)("WINTAINIUM_INSTALL_DIR"));

    /// <summary>The version in a folder's marker, or null when it isn't an install.</summary>
    public static Version? ReadMarker(string dir)
    {
        try
        {
            string f = Path.Combine(dir, Marker);
            return File.Exists(f) && Version.TryParse(File.ReadAllText(f).Trim(), out Version? v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// I-01 / I-02 / I-08: run here when this is the install folder, a portable copy, or not a
    /// single-file build (a development build). Otherwise install when there is no install, it is
    /// older, or it has no Wintainium.exe; a newer install is never downgraded. With no marker,
    /// <paramref name="exeVersion"/> reads the installed exe's own version instead.
    /// </summary>
    public static StartMode Decide(
        string here,
        string installDir,
        bool singleFile,
        Version own,
        IEnumerable<string>? dropFolders = null,
        Func<string, Version?>? exeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (!singleFile || SameFolder(here, installDir) || IsPortable(here, dropFolders))
        {
            return StartMode.RunHere;
        }

        string installedExe = Path.Combine(installDir, ExeName);
        Version? installed = ReadMarker(installDir) ?? (File.Exists(installedExe) ? exeVersion?.Invoke(installedExe) : null);
        bool needed = installed is null || own > installed || !File.Exists(Path.Combine(installDir, ExeName));
        return needed ? StartMode.InstallAndStart : StartMode.StartInstalled;
    }

    /// <summary>
    /// I-08 / N1: a portable copy has portable.txt beside it, or the files of an unpacked v0.3.0
    /// portable zip. Never in a folder browsers save downloads into (<paramref name="dropFolders"/>:
    /// Downloads, Desktop, temp), where a website could put such files next to the exe.
    /// </summary>
    public static bool IsPortable(string here, IEnumerable<string>? dropFolders = null)
    {
        if (dropFolders is not null && dropFolders.Any(d => !string.IsNullOrEmpty(d) && SameFolder(d, here)))
        {
            return false;
        }

        return File.Exists(Path.Combine(here, PortableMarker)) || V030PortableFiles.All(f => File.Exists(Path.Combine(here, f)));
    }

    /// <summary>True while that exe is running (Windows keeps a running exe locked).</summary>
    public static bool InUse(string exe)
    {
        if (!File.Exists(exe))
        {
            return false;
        }

        try
        {
            using FileStream fs = File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// I-01 / I-04 / I-11: writes this exe into the folder as a new file (so the download mark
    /// isn't copied), records the version, and removes the files a v0.3.0 install carried.
    /// User files are never touched. Returns the installed exe.
    /// </summary>
    public static string Install(string ownExe, string dir, Version own)
    {
        ArgumentNullException.ThrowIfNull(own);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, ExeName);
        string temp = target + ".new";
        using (FileStream src = File.OpenRead(ownExe))
        using (FileStream dst = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            src.CopyTo(dst);
        }

        File.Move(temp, target, overwrite: true);
        File.WriteAllText(Path.Combine(dir, Marker), own.ToString(), new UTF8Encoding(false));
        RemoveLegacyFiles(dir);
        return target;
    }

    /// <summary>
    /// I-11: the v0.3.0 scripts and their files go, and the assets folder too once it's empty.
    /// Wintainium.ps1 becomes a stub that does nothing (see <see cref="StubScript"/>).
    /// </summary>
    public static void RemoveLegacyFiles(string dir)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, StubScript), StubText, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a v0.3.0 exe started later may then reinstall itself; Wintainium still works
        }

        foreach (string rel in LegacyFiles)
        {
            string f = Path.Combine(dir, rel);
            try
            {
                if (File.Exists(f))
                {
                    File.Delete(f);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left behind; it does nothing without PowerShell starting it
            }
        }

        string assets = Path.Combine(dir, "assets");
        try
        {
            if (Directory.Exists(assets) && !Directory.EnumerateFileSystemEntries(assets).Any())
            {
                Directory.Delete(assets);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an empty folder is harmless
        }
    }

    /// <summary>Total size in KB for the Settings > Apps entry (at least 1).</summary>
    public static int SizeKb(string dir)
    {
        long kb = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length / 1024);
        return (int)Math.Clamp(kb, 1, int.MaxValue);
    }

    /// <summary>Major.minor.patch, as shown in Settings > Apps.</summary>
    public static string ShortVersion(Version v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}");
    }

    /// <summary>Standard Windows quoting for one command-line argument (X-07).</summary>
    public static string Quote(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length > 0 && s.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return s;
        }

        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (slashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', slashes);
            }

            slashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>True when cmd.exe reads the path as written inside double quotes.</summary>
    public static bool SafeForCmd(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Length > 0 && path.IndexOfAny(['%', '"', '!']) < 0 && !path.Any(char.IsControl);
    }

    /// <summary>The files uninstall removes when the user keeps their data.</summary>
    public static IEnumerable<string> OwnFiles =>
        LegacyFiles.Append(StubScript).Append(Marker).Append(ExeName + ".new");

    /// <summary>
    /// I-06: the cmd.exe arguments that remove the files once this process has exited. The exe stays
    /// locked until then, so its removal is retried for up to a minute. With
    /// <paramref name="deleteData"/> the whole folder goes; otherwise only Wintainium's own files.
    /// Every path is double-quoted; a folder cmd.exe can't quote safely is refused.
    /// </summary>
    public static string RemovalCommand(string dir, bool deleteData)
    {
        if (!SafeForCmd(dir))
        {
            throw new ArgumentException("The folder name has characters cmd.exe can't take safely.", nameof(dir));
        }

        static string Q(string p) => "\"" + p + "\"";
        static string Retry(string what, string remove) =>
            $"for /l %i in (1,1,60) do @if exist {what} ({remove} 2>nul & if exist {what} ping -n 2 127.0.0.1 >nul)";

        var cmd = new StringBuilder("/d /v:off /c ");
        if (deleteData)
        {
            string d = Q(dir.TrimEnd('\\', '/'));
            return cmd.Append(Retry(d, "rmdir /s /q " + d)).ToString();
        }

        cmd.Append("del /f /q");
        foreach (string rel in OwnFiles)
        {
            cmd.Append(' ').Append(Q(Path.Combine(dir, rel)));
        }

        string exe = Q(Path.Combine(dir, ExeName));
        cmd.Append(" 2>nul & ").Append(Retry(exe, "del /f /q " + exe));
        cmd.Append(" & rmdir ").Append(Q(Path.Combine(dir, "assets"))).Append(" 2>nul");
        return cmd.ToString();
    }
}
