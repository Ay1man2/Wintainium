using Wintainium.Core.SelfInstall;
using Wintainium.Tests.Helpers;

namespace Wintainium.Tests.SelfInstall;

/// <summary>The per-user install that replaces the v0.3.0 launcher (I-01 to I-11, X-07).</summary>
public sealed class SelfInstallerTests : IDisposable
{
    private static readonly Version V040 = new(0, 4, 0, 0);
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private string Install => dir.File("Programs/Wintainium");

    private string Downloads => dir.File("Downloads");

    private void Installed(string version, bool withExe = true)
    {
        dir.Write("Programs/Wintainium/" + SelfInstaller.Marker, version);
        if (withExe)
        {
            dir.Write("Programs/Wintainium/Wintainium.exe", "old exe");
        }
    }

    [Fact]
    public void Install_dir_defaults_to_local_programs_and_can_be_overridden()
    {
        Assert.EndsWith(Path.Combine("Programs", "Wintainium"), SelfInstaller.InstallDir(_ => null), StringComparison.Ordinal);
        Assert.Equal("/x/y", SelfInstaller.InstallDir(n => n == "WINTAINIUM_INSTALL_DIR" ? "/x/y" : null));
        Assert.True(SelfInstaller.IsTestInstall(_ => "/x/y"));
        Assert.False(SelfInstaller.IsTestInstall(_ => null));
    }

    [Fact]
    public void A_development_build_always_runs_in_place() =>
        Assert.Equal(StartMode.RunHere, SelfInstaller.Decide(Downloads, Install, singleFile: false, V040));

    [Fact]
    public void The_installed_copy_runs_in_place()
    {
        Installed("0.4.0.0");
        Assert.Equal(StartMode.RunHere, SelfInstaller.Decide(Install + Path.DirectorySeparatorChar, Install, singleFile: true, V040));
    }

    [Fact]
    public void A_portable_copy_runs_in_place_and_installs_nothing()
    {
        dir.Write("Portable/" + SelfInstaller.PortableMarker, "");
        Assert.Equal(StartMode.RunHere, SelfInstaller.Decide(dir.File("Portable"), Install, singleFile: true, V040));
        Assert.False(Directory.Exists(Install));
    }

    [Fact]
    public void An_unpacked_v030_portable_zip_runs_in_place()
    {
        foreach (string f in SelfInstaller.V030PortableFiles)
        {
            dir.Write("Old/" + f, "x");
        }

        Assert.Equal(StartMode.RunHere, SelfInstaller.Decide(dir.File("Old"), Install, singleFile: true, V040));
    }

    [Fact]
    public void Portable_files_in_a_downloads_folder_are_not_trusted()
    {
        // N1: a website can save portable.txt (and a crafted app list) next to the downloaded exe
        dir.Write("Downloads/" + SelfInstaller.PortableMarker, "");
        Assert.Equal(StartMode.InstallAndStart, SelfInstaller.Decide(Downloads, Install, singleFile: true, V040, [Downloads + Path.DirectorySeparatorChar]));
        foreach (string f in SelfInstaller.V030PortableFiles)
        {
            dir.Write("Downloads/" + f, "x");
        }

        Assert.False(SelfInstaller.IsPortable(Downloads, [Downloads]));
        Assert.True(SelfInstaller.IsPortable(Downloads, [dir.File("Desktop")]));
    }

    [Theory]
    [InlineData("0.5.0.0", StartMode.StartInstalled)]
    [InlineData("0.3.0.0", StartMode.InstallAndStart)]
    [InlineData(null, StartMode.InstallAndStart)]
    public void Without_a_marker_the_installed_exe_version_decides(string? exeVersion, StartMode expected)
    {
        dir.Write("Programs/Wintainium/Wintainium.exe", "exe");
        Version? v = exeVersion is null ? null : Version.Parse(exeVersion);
        Assert.Equal(expected, SelfInstaller.Decide(Downloads, Install, singleFile: true, V040, exeVersion: _ => v));
    }

    [Fact]
    public void A_download_with_no_install_installs()
    {
        Directory.CreateDirectory(Downloads);
        Assert.Equal(StartMode.InstallAndStart, SelfInstaller.Decide(Downloads, Install, singleFile: true, V040));
    }

    [Theory]
    [InlineData("0.3.0.0", true, StartMode.InstallAndStart)] // a v0.3.0 install is upgraded (I-11)
    [InlineData("0.4.0.0", true, StartMode.StartInstalled)]
    [InlineData("0.5.0.0", true, StartMode.StartInstalled)] // never downgrade (I-02)
    [InlineData("0.4.0.0", false, StartMode.InstallAndStart)] // marker but no exe: repair
    [InlineData("garbage", true, StartMode.InstallAndStart)]
    public void A_download_updates_only_an_older_install(string installed, bool withExe, StartMode expected)
    {
        Installed(installed, withExe);
        Assert.Equal(expected, SelfInstaller.Decide(Downloads, Install, singleFile: true, V040));
    }

    [Fact]
    public void Install_writes_the_exe_and_marker_and_keeps_user_files()
    {
        string own = dir.Write("Downloads/Wintainium.exe", "new exe");
        dir.Write("Programs/Wintainium/wintainium-apps.json", "[1]");
        dir.Write("Programs/Wintainium/wintainium-settings.json", "{}");
        dir.Write("Programs/Wintainium/wintainium-token.xml", "t");
        dir.Write("Programs/Wintainium/wintainium-cache.json", "c");
        string exe = SelfInstaller.Install(own, Install, V040);
        Assert.Equal(Path.Combine(Install, "Wintainium.exe"), exe);
        Assert.Equal("new exe", File.ReadAllText(exe));
        Assert.Equal(V040, SelfInstaller.ReadMarker(Install));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Equal("[1]", File.ReadAllText(Path.Combine(Install, "wintainium-apps.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Install, "wintainium-settings.json")));
        Assert.Equal("t", File.ReadAllText(Path.Combine(Install, "wintainium-token.xml")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(Install, "wintainium-cache.json")));
    }

    [Fact]
    public void Upgrading_a_v030_install_removes_its_scripts()
    {
        Installed("0.3.0.0");
        foreach (string f in SelfInstaller.LegacyFiles)
        {
            dir.Write("Programs/Wintainium/" + f.Replace('\\', '/'), "old");
        }

        dir.Write("Programs/Wintainium/wintainium-apps.json", "[]");
        string own = dir.Write("Downloads/Wintainium.exe", "new exe");
        dir.Write("Programs/Wintainium/Wintainium.ps1", "old script");
        SelfInstaller.Install(own, Install, V040);
        Assert.All(SelfInstaller.LegacyFiles, f => Assert.False(File.Exists(Path.Combine(Install, f)), f));

        // a stub stays, so a v0.3.0 exe sees an install and doesn't downgrade it
        Assert.Equal(SelfInstaller.StubText, File.ReadAllText(Path.Combine(Install, SelfInstaller.StubScript)));
        Assert.False(Directory.Exists(Path.Combine(Install, "assets")));
        Assert.True(File.Exists(Path.Combine(Install, "wintainium-apps.json")));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(Install, "Wintainium.exe")));
    }

    [Fact]
    public void An_assets_folder_with_other_files_is_kept()
    {
        dir.Write("Programs/Wintainium/assets/icon.ico", "i");
        dir.Write("Programs/Wintainium/assets/mine.png", "m");
        SelfInstaller.RemoveLegacyFiles(Install);
        Assert.True(File.Exists(Path.Combine(Install, "assets", "mine.png")));
    }

    [Fact]
    public void A_missing_exe_is_not_in_use() => Assert.False(SelfInstaller.InUse(dir.File("none.exe")));

    [Fact]
    public void Size_and_version_for_settings_apps()
    {
        dir.WriteBytes("S/a.bin", new byte[3000]);
        dir.WriteBytes("S/b/c.bin", new byte[2048]);
        Assert.Equal(4, SelfInstaller.SizeKb(dir.File("S")));
        dir.Write("T/tiny.txt", "x");
        Assert.Equal(1, SelfInstaller.SizeKb(dir.File("T")));
        Assert.Equal("0.4.0", SelfInstaller.ShortVersion(V040));
        Assert.Equal("1.2.0", SelfInstaller.ShortVersion(new Version(1, 2)));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\W\Wintainium.exe", "\"C:\\Program Files\\W\\Wintainium.exe\"")]
    [InlineData(@"C:\My Folder\", "\"C:\\My Folder\\\\\"")] // a trailing backslash is doubled (X-07)
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a b\""c", "\"a b\\\\\\\"c\"")]
    public void Arguments_are_quoted_the_windows_way(string arg, string quoted) =>
        Assert.Equal(quoted, SelfInstaller.Quote(arg));

    [Fact]
    public void Removal_keeps_user_files_unless_asked()
    {
        string keep = SelfInstaller.RemovalCommand(@"C:\U\Wintainium", deleteData: false);
        Assert.StartsWith("/d /v:off /c del /f /q \"", keep, StringComparison.Ordinal);
        Assert.Contains("Wintainium.exe\"", keep, StringComparison.Ordinal);
        Assert.Contains("Wintainium.exe.new\"", keep, StringComparison.Ordinal);
        Assert.Contains(SelfInstaller.Marker, keep, StringComparison.Ordinal);
        Assert.Contains("Wintainium.ps1", keep, StringComparison.Ordinal);
        Assert.Contains("for /l %i in (1,1,60) do", keep, StringComparison.Ordinal);
        Assert.DoesNotContain("rmdir /s", keep, StringComparison.Ordinal);
        Assert.All(SelfInstaller.UserFiles, f => Assert.DoesNotContain(f, keep, StringComparison.Ordinal));

        string all = SelfInstaller.RemovalCommand(@"C:\Users\A B\Wintainium", deleteData: true);
        Assert.Equal(
            "/d /v:off /c for /l %i in (1,1,60) do @if exist \"C:\\Users\\A B\\Wintainium\" (rmdir /s /q \"C:\\Users\\A B\\Wintainium\" 2>nul & if exist \"C:\\Users\\A B\\Wintainium\" ping -n 2 127.0.0.1 >nul)",
            all);
    }

    [Fact]
    public void Removal_quotes_every_path_for_cmd()
    {
        // a path with no spaces is still quoted, so & in it can't start a second command
        string cmd = SelfInstaller.RemovalCommand(@"C:\Users\R&D\Wintainium", deleteData: true);
        Assert.DoesNotContain(" C:\\Users", cmd, StringComparison.Ordinal);
        Assert.Contains("\"C:\\Users\\R&D\\Wintainium\"", cmd, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\100%\Wintainium")]
    [InlineData(@"C:\Users\Hi!\Wintainium")]
    [InlineData("C:\\Users\\a\"b\\Wintainium")]
    [InlineData("C:\\Users\\a\nb")]
    public void Removal_refuses_folders_cmd_cannot_quote(string folder)
    {
        Assert.False(SelfInstaller.SafeForCmd(folder));
        Assert.Throws<ArgumentException>(() => SelfInstaller.RemovalCommand(folder, deleteData: false));
    }
}
