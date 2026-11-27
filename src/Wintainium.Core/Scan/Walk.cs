using System.Text.RegularExpressions;

namespace Wintainium.Core.Scan;

/// <summary>
/// Read-only folder walking for the scan: unreadable folders are skipped, linked folders
/// (junctions, symlinks) aren't followed, and game libraries, saves and caches are never entered.
/// </summary>
internal static partial class Walk
{
    /// <summary>Files under <paramref name="root"/>, down to <paramref name="depth"/> levels of subfolders.</summary>
    public static IEnumerable<string> Files(string root, int depth, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || HasSkippedPart(root))
        {
            yield break;
        }

        var stack = new Stack<(string Dir, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            (string dir, int level) = stack.Pop();
            foreach (string f in Safe(() => Directory.GetFiles(dir)))
            {
                yield return f;
            }

            if (level >= depth)
            {
                continue;
            }

            foreach (string d in Dirs(dir, followLinks: false))
            {
                if (!SkipDirs().IsMatch(Path.GetFileName(d)))
                {
                    stack.Push((d, level + 1));
                }
            }
        }
    }

    /// <summary>The folders directly inside <paramref name="dir"/>.</summary>
    public static IEnumerable<string> Dirs(string dir) => Dirs(dir, followLinks: true);

    /// <summary>All folders under <paramref name="root"/>, down to <paramref name="depth"/> levels.</summary>
    public static IEnumerable<string> AllDirs(string root, int depth, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var queue = new Queue<(string Dir, int Level)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            (string dir, int level) = queue.Dequeue();
            foreach (string d in Dirs(dir, followLinks: false))
            {
                yield return d;
                if (level < depth)
                {
                    queue.Enqueue((d, level + 1));
                }
            }
        }
    }

    private static IEnumerable<string> Dirs(string dir, bool followLinks)
    {
        foreach (string d in Safe(() => Directory.GetDirectories(dir)))
        {
            if (followLinks || !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                yield return d;
            }
        }
    }

    private static bool HasSkippedPart(string path) =>
        path.Split('\\', '/').Any(p => SkipDirs().IsMatch(p));

    private static string[] Safe(Func<string[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Folders never worth walking: game libraries, saves, caches, package stores.</summary>
    [GeneratedRegex(@"^(roms|saves|states|screenshots|shadercache|shader_cache|cache|node_modules|\.git|downloaded_media|EmuUpdates)$", RegexOptions.IgnoreCase)]
    private static partial Regex SkipDirs();
}
