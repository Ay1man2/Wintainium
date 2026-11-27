using System.Security.AccessControl;
using System.Security.Principal;

namespace Wintainium.Tests.Helpers;

/// <summary>
/// Denies the current Windows user read access to a file until disposed, so reading it throws
/// UnauthorizedAccessException, as a locked-down or ACL-protected file does. Windows only.
/// </summary>
public sealed class DeniedFile : IDisposable
{
    private readonly FileSystemAccessRule rule;

    public DeniedFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Path = path;
        rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new FileInfo(path);
        FileSecurity sec = info.GetAccessControl();
        sec.AddAccessRule(rule);
        info.SetAccessControl(sec);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new FileInfo(Path);
        FileSecurity sec = info.GetAccessControl();
        sec.RemoveAccessRule(rule);
        info.SetAccessControl(sec);
    }
}
