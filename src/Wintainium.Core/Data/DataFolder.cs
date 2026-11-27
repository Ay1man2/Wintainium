using Wintainium.Core.Logging;

namespace Wintainium.Core.Data;

/// <summary>
/// The folder that holds the user's data files, next to the app (or the folder given with --data).
/// Names are the same as in v0.3.0 so both versions share them.
/// </summary>
public sealed class DataFolder
{
    public const string AppsFileName = "wintainium-apps.json";
    public const string SettingsFileName = "wintainium-settings.json";
    public const string CacheFileName = "wintainium-cache.json";
    public const string TokenFileName = "wintainium-token.xml";

    public DataFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public string AppsFile => System.IO.Path.Combine(Path, AppsFileName);

    public string SettingsFile => System.IO.Path.Combine(Path, SettingsFileName);

    public string CacheFile => System.IO.Path.Combine(Path, CacheFileName);

    public string TokenFile => System.IO.Path.Combine(Path, TokenFileName);

    /// <summary>
    /// Versions before 0.2 used emu-*.json / emu-token.xml; copy them to the new names once,
    /// only when the new file doesn't exist yet (D-09).
    /// </summary>
    public void MigrateLegacyFiles(ILog? log = null)
    {
        (string Old, string New)[] pairs =
        [
            ("emu-apps.json", AppsFileName),
            ("emu-cache.json", CacheFileName),
            ("emu-token.xml", TokenFileName),
        ];
        foreach ((string oldName, string newName) in pairs)
        {
            string oldPath = System.IO.Path.Combine(Path, oldName);
            string newPath = System.IO.Path.Combine(Path, newName);
            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                try
                {
                    File.Copy(oldPath, newPath);
                }
                catch (IOException ex)
                {
                    log?.Write($"Could not copy {oldName} to {newName}: {ex.Message}", LogColor.Yellow);
                }
            }
        }
    }
}
