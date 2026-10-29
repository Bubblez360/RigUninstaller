using System.Globalization;
using RigUninstaller.Helpers;
using Xunit;

namespace RigUninstaller.Tests;

public class SizeFormatterTests
{
    private const long Kb = 1024;
    private const long Mb = 1024 * Kb;
    private const long Gb = 1024 * Mb;

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(Kb, "1 KB")]
    [InlineData(150 * Kb, "150 KB")]
    [InlineData(5 * Mb, "5 MB")]
    [InlineData(Mb + Mb / 2, "1.5 MB")]
    [InlineData(300 * Mb, "300 MB")]
    [InlineData(2 * Gb, "2 GB")]
    [InlineData(Gb + Gb / 4, "1.25 GB")]
    public void Format_picks_a_readable_unit(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_returns_dash_when_size_is_unknown()
    {
        Assert.Equal("—", SizeFormatter.Format(null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_uses_the_decimal_separator_of_the_given_culture()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal("1,5 MB", SizeFormatter.Format(Mb + Mb / 2, german));
    }

    [Fact]
    public void Format_handles_terabytes()
    {
        Assert.Equal("2 TB", SizeFormatter.Format(2 * 1024 * Gb, CultureInfo.InvariantCulture));
    }
}
