using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wintainium.Core.Json;

/// <summary>
/// Reading and writing the JSON data files so that v0.3.0 (PowerShell) and v0.4.0 share them:
/// read UTF-8 with or without BOM (Windows PowerShell 5.1 writes one), keep every field and its
/// order, write UTF-8 without BOM, and replace files atomically with a .bak of the previous copy.
/// </summary>
public static class JsonFiles
{
    public static readonly JsonDocumentOptions ReadOptions = new()
    {
        // PowerShell 7's ConvertFrom-Json accepts comments and trailing commas; so do we.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep paths and apostrophes readable; non-ASCII is escaped afterwards (see Serialize).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a file as text, detecting a UTF-8 (or UTF-16) BOM.</summary>
    public static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Parses JSON text; throws <see cref="JsonException"/> when it isn't valid.</summary>
    public static JsonNode? Parse(string text) => JsonNode.Parse(text, documentOptions: ReadOptions);

    public static JsonNode? ReadFile(string path) => Parse(ReadText(path));

    /// <summary>
    /// Serializes as pure ASCII: anything outside it is written as \uXXXX. v0.3.0 reads these
    /// files with Get-Content and no -Encoding, which Windows PowerShell 5.1 decodes as ANSI, so
    /// raw UTF-8 accented names and paths would come back garbled (and be saved garbled).
    /// </summary>
    public static string Serialize(JsonNode? node) =>
        node is null ? "null" : EscapeNonAscii(node.ToJsonString(WriteOptions));

    // Non-ASCII characters can only appear inside JSON strings, so escaping them here is safe.
    internal static string EscapeNonAscii(string json)
    {
        if (!json.Any(c => c > 0x7F))
        {
            return json;
        }

        var sb = new StringBuilder(json.Length + 32);
        foreach (char c in json)
        {
            if (c > 0x7F)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes text so the file is never half-written: a temp file in the same folder, then a
    /// replace. When <paramref name="keepBackup"/> is set, the previous copy is kept as path.bak.
    /// </summary>
    public static void WriteTextAtomic(string path, string text, bool keepBackup = true)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                File.Replace(temp, full, keepBackup ? full + ".bak" : null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, full);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static void WriteFileAtomic(string path, JsonNode? node, bool keepBackup = true) =>
        WriteTextAtomic(path, Serialize(node) + Environment.NewLine, keepBackup);

    /// <summary>
    /// Reads a date the way the PowerShell app may have stored it: ISO 8601 (written with
    /// ToString('o'), or by PowerShell 7's ConvertTo-Json) or Windows PowerShell 5.1's
    /// "\/Date(1700000000000)\/". Returns UTC, or null when it can't be read.
    /// </summary>
    public static DateTime? ReadUtcDate(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out DateTime dt))
        {
            return ToUtc(dt);
        }

        if (!value.TryGetValue(out string? s) || string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return ParseUtcDate(s);
    }

    public static DateTime? ParseUtcDate(string s)
    {
        s = s.Trim();
        if (s.StartsWith("/Date(", StringComparison.Ordinal) && s.EndsWith(")/", StringComparison.Ordinal))
        {
            string inner = s[6..^2];
            int cut = inner.IndexOfAny(['+', '-'], 1);
            if (cut > 0)
            {
                inner = inner[..cut];
            }

            if (long.TryParse(inner, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ms))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }

            return null;
        }

        if (DateTimeOffset.TryParse(
                s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset dto))
        {
            return dto.UtcDateTime;
        }

        return null;
    }

    public static string? FormatUtcDate(DateTime? utc) =>
        utc is null ? null : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };
}
