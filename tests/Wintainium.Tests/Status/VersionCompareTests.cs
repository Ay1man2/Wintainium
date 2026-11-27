using System.Text.Json.Nodes;
using Wintainium.Core.Json;
using Wintainium.Core.Status;

namespace Wintainium.Tests.Status;

public class VersionCompareTests
{
    [Theory]
    [InlineData("v2.9.181", "2.9.181")]
    [InlineData("release-1.2", "1.2")]
    [InlineData("nightly", "")]
    [InlineData("2.0", "2.0")]
    public void Tag_number_drops_leading_letters(string tag, string number) =>
        Assert.Equal(number, VersionCompare.TagNumber(tag));

    [Theory]
    [InlineData("2.9.181", "2.9.181", true)]
    [InlineData("2.9.181.0", "2.9.181", true)]
    [InlineData("0.0.38-14567-abc", "0.0.38", true)]
    [InlineData("Build 2.9.181 (qt)", "2.9.181", true)]
    [InlineData("1.0", "1.0", true)]
    [InlineData("12.1.0", "2.1", false)]
    [InlineData("1.2.1", "2.1", false)]
    [InlineData("2.9.1810", "2.9.181", false)]
    [InlineData("2.9.181.5", "2.9.181", false)]
    [InlineData("", "2.9.181", false)]
    [InlineData("2.9.181", "", false)]
    public void Same_release_needs_the_whole_number(string installed, string tag, bool same) =>
        Assert.Equal(same, VersionCompare.IsSameRelease(installed, tag));

    [Theory]
    [InlineData("2.9.179", "2.9.181", true)]
    [InlineData("2.9.190", "2.9.181", false)]
    [InlineData("1.9.5", "2.0.0", true)] // X-09: v0.3.0 gave null (date decided)
    [InlineData("2.9.181", "2.10.0", true)] // X-09
    [InlineData("2025.3.1", "1.4.0", null)]
    [InlineData("12.1.0", "2.1", null)]
    [InlineData("1.0.0.0", "2.5.1", null)] // placeholder versions still go by date
    [InlineData("abc", "1.0", null)]
    [InlineData("1.0", "nightly", null)]
    public void Older_by_number(string installed, string release, bool? older) =>
        Assert.Equal(older, VersionCompare.IsOlder(installed, release));

    [Theory]
    [InlineData("1.9.5", "2.0.0")]
    [InlineData("2.9.181", "2.10.0")]
    public void V030_fell_back_to_the_date_for_scheme_changes(string installed, string release) =>
        Assert.Null(VersionCompare.IsOlderV030(installed, release));

    [Fact]
    public void Numbers_set_from_code_are_read_like_numbers_from_a_file()
    {
        var fromCode = new JsonObject { ["graceDays"] = 30, ["zero"] = 0, ["half"] = 2.5 };
        var fromFile = JsonNode.Parse("""{ "graceDays": 30, "zero": 0, "half": 2.5 }""")!.AsObject();
        foreach (JsonObject o in new[] { fromCode, fromFile })
        {
            Assert.Equal(30, o.GetPositiveInt("graceDays"));
            Assert.Null(o.GetPositiveInt("zero"));
            Assert.Equal(2, o.GetPositiveInt("half"));
            Assert.Equal("30", o.GetString("graceDays"));
        }
    }
}
