using System.Text.Json.Nodes;
using Wintainium.Core.Json;

namespace Wintainium.Core.Scan;

/// <summary>The apps Wintainium recognizes at their usual install paths (known-apps.json, built in).</summary>
public static class Catalog
{
    public const string ResourceName = "Wintainium.known-apps.json";

    public static IReadOnlyList<JsonObject> LoadBuiltIn()
    {
        using Stream s = typeof(Catalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The built-in app catalog is missing.");
        using var reader = new StreamReader(s);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<JsonObject> Parse(string json) =>
        JsonFiles.Parse(json) is JsonArray arr ? [.. arr.OfType<JsonObject>()] : [];
}
