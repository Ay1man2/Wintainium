using Wintainium.Core.Releases;

namespace Wintainium.Tests.Releases;

public class LinksTests
{
    [Fact(DisplayName = "https link accepted")]
    public void Https_link_accepted() => Assert.True(Links.IsHttps("https://example.org/x.zip"));

    [Theory(DisplayName = "link refused even in tests")]
    [InlineData("http://example.org/x.zip")]
    [InlineData("ftp://example.org/x.zip")]
    [InlineData(@"\\host\share\x.zip")]
    [InlineData("file://host/share/x.zip")]
    [InlineData("file:///C:/Users/x/Tool-v1.2-Windows-x64.zip")]
    [InlineData("x.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void Link_refused(string? url) => Assert.False(Links.IsHttps(url));

    [Fact]
    public void User_info_is_stripped()
    {
        Assert.Equal("https://github.com/o/r", Links.StripUserInfo("https://user:ghp_secret@github.com/o/r"));
        Assert.Equal("https://github.com/o/r", Links.StripUserInfo("https://github.com/o/r"));
        Assert.Equal("not a link", Links.StripUserInfo("not a link"));
    }
}
