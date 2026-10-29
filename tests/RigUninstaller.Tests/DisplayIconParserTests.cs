using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class DisplayIconParserTests
{
    [Theory]
    [InlineData(@"""C:\Apps\acme.exe"",0", @"C:\Apps\acme.exe", 0)]
    [InlineData(@"C:\Apps\acme.exe,1", @"C:\Apps\acme.exe", 1)]
    [InlineData(@"C:\Apps\acme.exe", @"C:\Apps\acme.exe", 0)]
    [InlineData(@"""C:\Apps\acme.exe""", @"C:\Apps\acme.exe", 0)]
    [InlineData(@"C:\Apps\acme.exe,-101", @"C:\Apps\acme.exe", -101)]
    [InlineData(@"  C:\Apps\acme.ico  ", @"C:\Apps\acme.ico", 0)]
    [InlineData(@"""C:\Program Files\Acme App\acme.exe"" , 2", @"C:\Program Files\Acme App\acme.exe", 2)]
    [InlineData(@"C:\My,Folder\acme.exe", @"C:\My,Folder\acme.exe", 0)]
    public void Parse_splits_path_and_index(string raw, string expectedPath, int expectedIndex)
    {
        var location = DisplayIconParser.Parse(raw);

        Assert.NotNull(location);
        Assert.Equal(expectedPath, location!.Path);
        Assert.Equal(expectedIndex, location.Index);
    }

    [Fact]
    public void Parse_expands_environment_variables()
    {
        var location = DisplayIconParser.Parse(@"%SystemRoot%\system32\shell32.dll,3");

        var expected = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\system32\shell32.dll");
        Assert.Equal(expected, location?.Path);
        Assert.Equal(3, location?.Index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@""""",0")]
    [InlineData(",0")]
    public void Parse_returns_null_when_there_is_no_path(string? raw)
    {
        Assert.Null(DisplayIconParser.Parse(raw));
    }
}
