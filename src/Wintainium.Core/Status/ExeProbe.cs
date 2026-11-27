using System.Diagnostics;

namespace Wintainium.Core.Status;

/// <summary>What the status engine needs to know about an app's program file.</summary>
public sealed record ExeInfo(bool Exists, string ProductVersion, DateTime? LastWriteUtc)
{
    public static readonly ExeInfo Missing = new(false, "", null);
}

/// <summary>Reads a program's version and date (Get-Item ... .VersionInfo.ProductVersion, LastWriteTimeUtc).</summary>
public interface IExeProbe
{
    ExeInfo Probe(string path);
}

public sealed class FileExeProbe : IExeProbe
{
    public ExeInfo Probe(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return ExeInfo.Missing;
        }

        try
        {
            if (File.Exists(path))
            {
                string version = "";
                try
                {
                    version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
                }
                catch (FileNotFoundException)
                {
                    version = "";
                }

                return new ExeInfo(true, version, File.GetLastWriteTimeUtc(path));
            }

            // Test-Path is also true for a folder
            if (Directory.Exists(path))
            {
                return new ExeInfo(true, "", Directory.GetLastWriteTimeUtc(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ExeInfo.Missing;
        }

        return ExeInfo.Missing;
    }
}
