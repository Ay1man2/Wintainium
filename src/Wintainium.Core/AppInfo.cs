using System.Reflection;

namespace Wintainium.Core;

/// <summary>Name and version of the running Wintainium build.</summary>
public static class AppInfo
{
    public const string Name = "Wintainium";

    /// <summary>The repository Wintainium tracks itself by (its own row in the list).</summary>
    public const string Repo = "Ay1man2/Wintainium";

    /// <summary>Version as major.minor.patch, from the assembly (set once in Directory.Build.props).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        Version? v = typeof(AppInfo).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }
}
