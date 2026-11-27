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
        "Wintainium.ps1",
        "Find-Sources.ps1",
        "known-apps.json",
        "wintainium-apps.example.json",
        "LICENSE",
        Path.Combine("assets", "icon.ico"),
    ];

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
    /// older, or it has no Wintainium.exe; a newer install is never downgraded.
    /// </summary>
    public static StartMode Decide(string here, string installDir, bool singleFile, Version own)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (!singleFile || SameFolder(here, installDir) || File.Exists(Path.Combine(here, PortableMarker)))
        {
            return StartMode.RunHere;
        }

        Version? installed = ReadMarker(installDir);
        bool needed = installed is null || own > installed || !File.Exists(Path.Combine(installDir, ExeName));
        return needed ? StartMode.InstallAndStart : StartMode.StartInstalled;
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

    /// <summary>I-11: the v0.3.0 scripts and their files go; the assets folder too once it's empty.</summary>
    public static void RemoveLegacyFiles(string dir)
    {
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

    /// <summary>
    /// I-06: the cmd.exe line that removes the files once this process has exited. With
    /// <paramref name="deleteData"/> the whole folder goes; otherwise only Wintainium's own files.
    /// </summary>
    public static string RemovalCommand(string dir, bool deleteData)
    {
        var cmd = new StringBuilder("/c ping -n 3 127.0.0.1 >nul");
        if (deleteData)
        {
            cmd.Append(" & rmdir /s /q ").Append(Quote(dir));
            return cmd.ToString();
        }

        foreach (string rel in LegacyFiles.Append(Marker).Append(ExeName))
        {
            cmd.Append(" & del /f /q ").Append(Quote(Path.Combine(dir, rel)));
        }

        cmd.Append(" & rmdir ").Append(Quote(Path.Combine(dir, "assets")));
        return cmd.ToString();
    }
}
