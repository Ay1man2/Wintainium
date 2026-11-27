namespace Wintainium.Tests.Helpers;

/// <summary>A test that needs Windows (DPAPI, registry, NTFS streams). Skipped elsewhere; CI runs it on windows-latest.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows";
        }
    }
}
