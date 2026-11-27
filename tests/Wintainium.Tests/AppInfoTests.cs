using Wintainium.Core;

namespace Wintainium.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_is_major_minor_patch()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
    }

    [Fact]
    public void Version_is_the_port_release()
    {
        Assert.Equal("0.4.0", AppInfo.Version);
    }
}
