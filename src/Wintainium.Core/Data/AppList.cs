using System.Text.Json;
using System.Text.Json.Nodes;
using Wintainium.Core.Json;
using Wintainium.Core.Logging;

namespace Wintainium.Core.Data;

/// <summary>wintainium-apps.json: the list of tracked apps.</summary>
public sealed class AppList
{
    private readonly List<AppEntry> entries;

    private AppList(string path, List<AppEntry> entries)
    {
        FilePath = path;
        this.entries = entries;
    }

    public string FilePath { get; }

    public IReadOnlyList<AppEntry> Entries => entries;

    /// <summary>
    /// Loads the list. A missing file starts an empty one (D-01). A file that can't be read is
    /// left alone and the previous copy (.bak) is used instead, with a message (X-11); when there
    /// is no usable copy either, <see cref="AppListLoadException"/> says what is wrong.
    /// </summary>
    public static AppList Load(string path, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!File.Exists(path))
        {
            log.Write($"No app list yet. Starting an empty one at {path}", LogColor.Yellow);
            log.Write("(Use Add app or Scan to add apps.)", LogColor.Yellow);
            try
            {
                JsonFiles.WriteTextAtomic(path, "[]" + Environment.NewLine, keepBackup: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AppListLoadException(path, ex.Message);
            }

            return new AppList(path, []);
        }

        string firstError;
        try
        {
            return new AppList(path, ParseEntries(JsonFiles.ReadText(path)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            firstError = ex.Message;
        }

        string bak = path + ".bak";
        if (File.Exists(bak))
        {
            try
            {
                List<AppEntry> fromBackup = ParseEntries(JsonFiles.ReadText(bak));
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                string broken = $"{path}.broken-{stamp}";
                File.Copy(path, broken, overwrite: true);
                var list = new AppList(path, fromBackup);

                // no backup of the broken file: .bak must stay the last good copy
                list.Save(keepBackup: false);
                log.Write($"{Path.GetFileName(path)} could not be read ({firstError}).", LogColor.Red);
                log.Write($"Restored the previous copy from {Path.GetFileName(bak)}. The unreadable file was kept as {Path.GetFileName(broken)}.", LogColor.Yellow);
                return list;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // fall through to the error below
            }
        }

        throw new AppListLoadException(path, firstError);
    }

    /// <summary>Parses the file's text. Windows PowerShell 5.1 may hand back one entry as an object, not a list.</summary>
    internal static List<AppEntry> ParseEntries(string text)
    {
        JsonNode? root = JsonFiles.Parse(text);
        return root switch
        {
            null => [],
            JsonArray arr => [.. arr.Select(n => ToEntry(n?.DeepClone()))],
            JsonObject obj => [new AppEntry(obj)],
            _ => throw new InvalidDataException("the file holds no list of apps"),
        };

        static AppEntry ToEntry(JsonNode? node) =>
            node is JsonObject o ? new AppEntry(o) : throw new InvalidDataException("an entry in the list is not an object");
    }

    /// <summary>Writes the list; the previous file becomes .bak unless <paramref name="keepBackup"/> is false.</summary>
    public void Save(bool keepBackup = true)
    {
        var arr = new JsonArray([.. entries.Select(e => e.Json.DeepClone())]);
        JsonFiles.WriteFileAtomic(FilePath, arr, keepBackup);
    }

    public AppEntry? Find(string name) =>
        entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string name) => Find(name) is not null;

    public void Add(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entries.Add(entry);
    }

    /// <summary>Removes this exact entry (not others with the same name).</summary>
    public bool RemoveEntry(AppEntry entry) => entries.Remove(entry);

    /// <summary>Removes every entry with that name (PowerShell's -ne is case-insensitive). Returns how many.</summary>
    public int Remove(string name) =>
        entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A name not used yet: "Name", else "Name (2)", "Name (3)"...</summary>
    public string UniqueName(string name)
    {
        string candidate = name;
        int n = 2;
        while (Contains(candidate))
        {
            candidate = $"{name} ({n})";
            n++;
        }

        return candidate;
    }
}

/// <summary>The app list exists but can't be read, and there is no usable .bak.</summary>
public sealed class AppListLoadException : Exception
{
    public AppListLoadException(string path, string reason)
        : base($"{Path.GetFileName(path)} could not be read ({reason}). Fix or remove it, then start Wintainium again: {path}")
    {
        FilePath = path;
        Reason = reason;
    }

    public AppListLoadException()
    {
        FilePath = "";
        Reason = "";
    }

    public AppListLoadException(string message)
        : base(message)
    {
        FilePath = "";
        Reason = message;
    }

    public AppListLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
        FilePath = "";
        Reason = message;
    }

    public string FilePath { get; }

    public string Reason { get; }
}
