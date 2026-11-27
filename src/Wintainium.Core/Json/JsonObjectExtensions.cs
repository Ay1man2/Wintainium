using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wintainium.Core.Json;

/// <summary>
/// Field access with PowerShell's rules, so entries behave as they did in v0.3.0: property names
/// match case-insensitively, a missing or null field reads as "", and truthiness follows PowerShell
/// (null, "", 0, false and empty lists are false; any other string, including "false", is true).
/// </summary>
public static class JsonObjectExtensions
{
    /// <summary>The value of a field, matching its name case-insensitively (exact match first).</summary>
    public static JsonNode? GetField(this JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out JsonNode? exact))
        {
            return exact;
        }

        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }

    /// <summary>Sets a field, reusing the existing spelling of its name; new fields are added at the end.</summary>
    public static void SetField(this JsonObject obj, string name, JsonNode? value)
    {
        string key = name;
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                key = kv.Key;
                break;
            }
        }

        obj[key] = value;
    }

    public static bool RemoveField(this JsonObject obj, string name)
    {
        string? key = obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return key is not null && obj.Remove(key);
    }

    /// <summary>[string]$obj.name in PowerShell: "" when missing or null; numbers and booleans as text.</summary>
    public static string GetString(this JsonObject obj, string name) => NodeToString(obj.GetField(name));

    public static string NodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.String:
                    return v.GetValue<string>();
                case JsonValueKind.True:
                    return "True";
                case JsonValueKind.False:
                    return "False";
                case JsonValueKind.Number:
                    return v.TryGetValue(out long l) ? l.ToString(CultureInfo.InvariantCulture)
                        : v.GetValue<double>().ToString(CultureInfo.InvariantCulture);
                case JsonValueKind.Null:
                    return "";
            }
        }

        if (node is JsonArray arr)
        {
            // [string] of an array joins its items with spaces
            return string.Join(" ", arr.Select(NodeToString));
        }

        return node.ToJsonString();
    }

    /// <summary>PowerShell truthiness of a field.</summary>
    public static bool IsTruthy(this JsonObject obj, string name) => IsTruthy(obj.GetField(name));

    public static bool IsTruthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonArray arr:
                return arr.Count switch
                {
                    0 => false,
                    1 => IsTruthy(arr[0]),
                    _ => true,
                };
            case JsonObject:
                return true;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.String:
                        return v.GetValue<string>().Length > 0;
                    case JsonValueKind.Number:
                        return v.GetValue<double>() != 0;
                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    /// <summary>@($obj.name | Where-Object { $_ }): the non-empty items of a field that may be one value or a list.</summary>
    public static List<string> GetStringList(this JsonObject obj, string name)
    {
        JsonNode? node = obj.GetField(name);
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (IsTruthy(item))
                {
                    list.Add(NodeToString(item));
                }
            }
        }
        else if (IsTruthy(node))
        {
            list.Add(NodeToString(node));
        }

        return list;
    }

    /// <summary>[int]$obj.name when the field is truthy, else null.</summary>
    public static int? GetPositiveInt(this JsonObject obj, string name)
    {
        JsonNode? node = obj.GetField(name);
        if (!IsTruthy(node))
        {
            return null;
        }

        string s = NodeToString(node);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            // PowerShell's [int] rounds half to even
            return (int)Math.Round(d, MidpointRounding.ToEven);
        }

        return null;
    }
}
