using System.Text.Json;
using System.Text.Json.Nodes;
using Wintainium.Core.Json;

namespace Wintainium.Core.Data;

/// <summary>
/// wintainium-settings.json: the choices made in Setup and a few switches. Unknown keys are kept.
/// A file that can't be read counts as empty, as in v0.3.0 (D-06).
/// </summary>
public sealed class Settings
{
    public const int DefaultGraceDays = 3;
    public const int DefaultCacheMinutes = 60;

    private readonly JsonObject json;

    private Settings(string path, JsonObject json)
    {
        FilePath = path;
        this.json = json;
    }

    public string FilePath { get; }

    public static Settings Load(string path)
    {
        JsonObject obj = [];
        if (File.Exists(path))
        {
            try
            {
                if (JsonFiles.ReadFile(path) is JsonObject o)
                {
                    obj = o;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                obj = [];
            }
        }

        return new Settings(path, obj);
    }

    /// <summary>Folder for apps installed with Add app, when set.</summary>
    public string? InstallRoot => json.IsTruthy("installRoot") ? json.GetString("installRoot") : null;

    /// <summary>Folder for downloads, when set.</summary>
    public string? DownloadDir => json.IsTruthy("downloadDir") ? json.GetString("downloadDir") : null;

    /// <summary>True once the setup wizard was finished or closed.</summary>
    public bool SetupDone => json.IsTruthy("setupDone");

    /// <summary>
    /// Wintainium's own row in gold: on unless turned off. ($settings.goldSelf -ne $false in
    /// PowerShell, so false, "false" and 0 all turn it off.)
    /// </summary>
    public bool GoldSelf
    {
        get
        {
            JsonNode? node = json.GetField("goldSelf");
            if (node is not JsonValue v)
            {
                return true;
            }

            return v.GetValueKind() switch
            {
                JsonValueKind.False => false,
                JsonValueKind.String => !string.Equals(v.GetValue<string>(), "false", StringComparison.OrdinalIgnoreCase),
                JsonValueKind.Number => JsonObjectExtensions.NumberOf(v) != 0,
                _ => true,
            };
        }
    }

    /// <summary>Scan entries hidden with "Don't show again".</summary>
    public IReadOnlyList<string> ScanIgnore => json.GetStringList("scanIgnore");

    /// <summary>Days a release must be newer than the program before it counts as an update (L-06).</summary>
    public int GraceDays => json.GetPositiveInt("graceDays") ?? DefaultGraceDays;

    /// <summary>Minutes a release lookup is reused (L-06).</summary>
    public int CacheMinutes => json.GetPositiveInt("cacheMinutes") ?? DefaultCacheMinutes;

    /// <summary>Folder of EmuDeck.exe, when the user moved it (replaces -EmuDeckExe, L-07).</summary>
    public string? EmuDeckExe => json.IsTruthy("emuDeckExe") ? json.GetString("emuDeckExe") : null;

    public JsonNode? this[string key] => json.GetField(key);

    /// <summary>Sets values and writes the file (Save-Settings).</summary>
    public void Save(IReadOnlyDictionary<string, JsonNode?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (KeyValuePair<string, JsonNode?> kv in values)
        {
            json.SetField(kv.Key, kv.Value?.DeepClone());
        }

        JsonFiles.WriteFileAtomic(FilePath, json);
    }

    public void Save(string key, JsonNode? value) => Save(new Dictionary<string, JsonNode?> { [key] = value });
}
