using System.IO.Compression;
using System.Text.RegularExpressions;
using Wintainium.Core.Logging;

namespace Wintainium.Core.Install;

/// <summary>An archive entry that would land outside the folder it is unpacked into (zip-slip).</summary>
public sealed class UnsafeArchiveException : Exception
{
    public UnsafeArchiveException(string entry)
        : base($"The archive contains '{entry}', which would be unpacked outside its folder. Nothing was installed.") => Entry = entry;

    public UnsafeArchiveException()
        : this("")
    {
    }

    public UnsafeArchiveException(string message, Exception innerException)
        : base(message, innerException) => Entry = "";

    public string Entry { get; }
}

/// <summary>Unpacking zip and 7z files (Test-Archive, Expand-ToFolder).</summary>
public static partial class Archives
{
    /// <summary>Programs and scripts that keep the download's mark after unpacking (X-02).</summary>
    private static readonly HashSet<string> RunnableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".lnk", ".url", ".dll", ".jar",
    };

    /// <summary>.zip always; .7z when EmuDeck's 7-Zip is there.</summary>
    public static bool CanUnpack(string file, string sevenZip)
    {
        string ext = Path.GetExtension(file);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) && File.Exists(sevenZip));
    }

    /// <summary>
    /// Unpacks <paramref name="file"/> over <paramref name="dir"/> (created if needed; same-named
    /// files are replaced). A single top folder in the archive is flattened. When
    /// <paramref name="exeName"/> is given and the archive keeps that program in a subfolder that
    /// <paramref name="dir"/> already ends with, the files go one level up instead (X-08).
    /// Returns the folder the files went into.
    /// </summary>
    public static async Task<string> ExpandToFolderAsync(string file, string dir, InstallContext ctx, string? exeName = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        string tmp = Path.Combine(ctx.TempDir, "wintainium-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            if (File.Exists(ctx.SevenZip))
            {
                ProcessOutcome r = await ctx.Processes.RunAsync(ctx.SevenZip, ["x", "-o" + tmp, "-aoa", "-snl-", "--", file], () => { }, TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                if (r.ExitCode != 0)
                {
                    throw new InvalidDataException($"7-Zip could not unpack {Path.GetFileName(file)}: {r.Errors}");
                }

                CheckInside(tmp);
            }
            else
            {
                ExtractZip(file, tmp);
            }

            string src = tmp;
            string[] kids = Directory.GetFileSystemEntries(tmp);
            if (kids.Length == 1 && Directory.Exists(kids[0]))
            {
                src = kids[0];
            }

            string target = TargetFolder(src, dir, exeName);
            string? host = HostUrl(ctx.Mark.Read(file));
            CopyTree(src, target, host is null ? null : f => MarkIfRunnable(f, host, ctx.Mark));
            return target;
        }
        finally
        {
            TryDeleteTree(tmp);
        }
    }

    /// <summary>Copies a folder tree, replacing same-named files.</summary>
    public static void CopyTree(string from, string to, Action<string>? copied = null)
    {
        Directory.CreateDirectory(to);
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        }

        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            File.Copy(f, dest, overwrite: true);
            copied?.Invoke(dest);
        }
    }

    internal static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover temp folder is harmless
        }
    }

    private static void ExtractZip(string file, string tmp)
    {
        string root = Path.GetFullPath(tmp).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive zip = ZipFile.OpenRead(file);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            string dest = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('\\', '/')));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsafeArchiveException(e.FullName);
            }

            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>7-Zip refuses ../ entries itself; links that point outside are refused here.</summary>
    private static void CheckInside(string tmp)
    {
        foreach (string p in Directory.GetFileSystemEntries(tmp, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(p).LinkTarget is not null)
            {
                throw new UnsafeArchiveException(Path.GetRelativePath(tmp, p));
            }
        }
    }

    private static string TargetFolder(string src, string dir, string? exeName)
    {
        if (string.IsNullOrEmpty(exeName) || File.Exists(Path.Combine(src, exeName)))
        {
            return dir;
        }

        string? found = Directory.EnumerateFiles(src, exeName, SearchOption.AllDirectories).FirstOrDefault();
        if (found is null)
        {
            return dir;
        }

        string rel = Path.GetRelativePath(src, Path.GetDirectoryName(found)!);
        string trimmed = dir.TrimEnd('\\', '/');
        string suffix = Path.DirectorySeparatorChar + rel;
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^suffix.Length];
        }

        return dir;
    }

    private static string? HostUrl(string? mark)
    {
        if (mark is null)
        {
            return null;
        }

        Match m = HostLine().Match(mark);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static void MarkIfRunnable(string file, string host, IFileMark mark)
    {
        if (RunnableTypes.Contains(Path.GetExtension(file)))
        {
            try
            {
                mark.Mark(file, host);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // same as for the download itself
            }
        }
    }

    [GeneratedRegex(@"(?m)^HostUrl=(.*)$")]
    private static partial Regex HostLine();
}
