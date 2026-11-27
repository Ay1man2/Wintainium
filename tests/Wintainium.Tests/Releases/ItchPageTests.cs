using Wintainium.Core.Releases;

namespace Wintainium.Tests.Releases;

public class ItchPageTests
{
    private const string Real = """
        <div class="upload"><div class="info_column"><div class="upload_name"><strong title="some-game-windows.zip" class="name">some-game-windows.zip</strong> <span class="file_size"><span>308 MB</span></span> <span class="download_platforms"><span aria-hidden="true" class="icon icon-windows8" title="Download for Windows"></span> </span></div><div class="build_row"><span class="version_name">Version a8c30565</span> <span class="version_date"><abbr title="12 October 2026 @ 19:03 UTC"><span aria-hidden="true" class="icon icon-stopwatch"></span> 34 days ago</abbr></span></div></div></div></div></div><section id="devlog" class="game_devlog">

        """;

    private const string Two = """<strong class="name">game-linux.tar.gz</strong><span class="icon icon-tux"></span><span class="version_name">Version L1</span><abbr title="1 November 2026 @ 9:05 UTC">x</abbr>""" + Real;

    [Fact(DisplayName = "itch: version read")]
    public void Version_read() => Assert.Equal("a8c30565", ItchPage.Read(Real, "https://example.itch.io/some-game", "").Tag);

    [Fact(DisplayName = "itch: exact UTC date read")]
    public void Exact_utc_date_read()
    {
        DateTime? d = ItchPage.Read(Real, "p", "").Date;
        Assert.Equal(new DateTime(2026, 10, 12, 19, 3, 0, DateTimeKind.Utc), d);
        Assert.Equal(DateTimeKind.Utc, d!.Value.Kind);
    }

    [Fact(DisplayName = "itch: page kept as link")]
    public void Page_kept_as_link() => Assert.Equal("https://example.itch.io/some-game", ItchPage.Read(Real, "https://example.itch.io/some-game", "").Page);

    [Fact(DisplayName = "itch: prefers the Windows upload")]
    public void Prefers_the_windows_upload() => Assert.Equal("a8c30565", ItchPage.Read(Two, "p", "").Tag);

    [Fact(DisplayName = "itch: asset pattern overrides")]
    public void Asset_pattern_overrides()
    {
        ReleaseInfo r = ItchPage.Read(Two, "p", "LINUX");
        Assert.Equal("L1", r.Tag);
        Assert.Equal(9, r.Date!.Value.Hour);
    }

    [Fact(DisplayName = "itch: no version -> file name")]
    public void No_version_uses_file_name() =>
        Assert.Equal("plain.zip", ItchPage.Read("""<strong class="name">plain.zip</strong><span class="icon icon-windows8"></span><abbr title="3 March 2026 @ 10:00 UTC">x</abbr>""", "p", "").Tag);

    [Fact(DisplayName = "itch: empty page throws (shown as lookup failed)")]
    public void Empty_page_throws() => Assert.Throws<LookupException>(() => ItchPage.Read("<html>nothing</html>", "p", ""));

    [Fact]
    public void Notes_name_the_upload() => Assert.Equal("Latest upload: some-game-windows.zip", ItchPage.Read(Real, "p", "").Notes);
}
