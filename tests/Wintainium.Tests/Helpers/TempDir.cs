namespace Wintainium.Tests.Helpers;

/// <summary>A fresh folder under the system temp folder, deleted afterwards. Tests never touch real folders.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wintainium-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    public string Write(string relative, string text)
    {
        string full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, text);
        return full;
    }

    public string WriteBytes(string relative, byte[] bytes)
    {
        string full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, bytes);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
