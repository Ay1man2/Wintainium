using System.Text.Json.Nodes;
using Wintainium.Core.Json;

namespace Wintainium.Core.Data;

/// <summary>
/// One app in wintainium-apps.json. Backed by the JSON object itself, so fields this version
/// doesn't know about (and their order) survive a save. Field rules follow the PowerShell app
/// (see <see cref="JsonObjectExtensions"/>).
/// </summary>
public sealed class AppEntry
{
    public AppEntry(JsonObject json)
    {
        Json = json ?? throw new ArgumentNullException(nameof(json));
    }

    public AppEntry()
        : this([])
    {
    }

    /// <summary>The underlying JSON object (shared, not copied).</summary>
    public JsonObject Json { get; }

    public string Name { get => Get("name"); set => Set("name", value); }

    /// <summary>The program (or, for mods, a file) whose version/date tells what is installed. May hold %VARS%.</summary>
    public string Exe { get => Get("exe"); set => Set("exe", value); }

    /// <summary>github, gitlab, gitea, itch, dolphin or manual.</summary>
    public string Source { get => Get("source"); set => Set("source", value); }

    public string Repo { get => Get("repo"); set => Set("repo", value); }

    public string Tag { get => Get("tag"); set => Set("tag", value); }

    public bool Prerelease { get => Json.IsTruthy("prerelease"); set => SetBool("prerelease", value); }

    public string Api { get => Get("api"); set => Set("api", value); }

    public string Url { get => Get("url"); set => Set("url", value); }

    /// <summary>Regex the release file name must match.</summary>
    public string Asset { get => Get("asset"); set => Set("asset", value); }

    /// <summary>Download link template with {version}.</summary>
    public string DownloadUrl { get => Get("downloadUrl"); set => Set("downloadUrl", value); }

    /// <summary>Update methods in order; at least "page".</summary>
    public IReadOnlyList<string> Update
    {
        get
        {
            List<string> list = Json.GetStringList("update");
            return list.Count == 0 ? ["page"] : list;
        }

        set => Json.SetField("update", new JsonArray([.. value.Select(v => (JsonNode)JsonValue.Create(v))]));
    }

    public string EmudeckFn { get => Get("emudeckFn"); set => Set("emudeckFn", value); }

    public string Hint { get => Get("hint"); set => Set("hint", value); }

    public string Launch { get => Get("launch"); set => Set("launch", value); }

    /// <summary>Per-app grace days, or null to use the default.</summary>
    public int? GraceDays => Json.GetPositiveInt("graceDays");

    public string InstallDir { get => Get("installDir"); set => Set("installDir", value); }

    /// <summary>The release installed, for apps tracked by release (mods, data packs).</summary>
    public string InstalledTag { get => Get("installedTag"); set => Set("installedTag", value); }

    public bool HasInstalledTag => Json.IsTruthy("installedTag");

    public string Note { get => Get("note"); set => Set("note", value); }

    /// <summary>Wintainium's own entry (it can track its own releases).</summary>
    public bool IsSelf => string.Equals(Repo, AppInfo.Repo, StringComparison.OrdinalIgnoreCase);

    public static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);

    public string ExpandedExe => Expand(Exe);

    public string ExpandedLaunch => Expand(Launch);

    public string ExpandedInstallDir => Expand(InstallDir);

    public override string ToString() => Name;

    private string Get(string field) => Json.GetString(field);

    private void Set(string field, string value) => Json.SetField(field, JsonValue.Create(value));

    private void SetBool(string field, bool value) => Json.SetField(field, JsonValue.Create(value));
}
