using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class UninstallEntryParserTests
{
    private static RegistryEntry Entry(string keyName, params (string Name, object? Value)[] values) =>
        new(
            $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}",
            values.ToDictionary(v => v.Name, v => v.Value));

    [Fact]
    public void Parse_reads_all_fields_of_a_normal_program()
    {
        var entry = Entry(
            "{ABC-123}",
            ("DisplayName", "Acme Editor"),
            ("DisplayVersion", "2.5.1"),
            ("Publisher", "Acme Corp"),
            ("InstallDate", "20240115"),
            ("InstallLocation", @"C:\Program Files\Acme Editor"),
            ("EstimatedSize", 20480),
            ("DisplayIcon", @"C:\Program Files\Acme Editor\acme.exe,0"),
            ("UninstallString", @"""C:\Program Files\Acme Editor\unins000.exe"""),
            ("QuietUninstallString", @"""C:\Program Files\Acme Editor\unins000.exe"" /SILENT"));

        var result = UninstallEntryParser.Parse(entry);

        Assert.Equal(SkipReason.None, result.Skip);
        var program = Assert.IsType<InstalledProgram>(result.Program);
        Assert.Equal("Acme Editor", program.Name);
        Assert.Equal("2.5.1", program.Version);
        Assert.Equal("Acme Corp", program.Publisher);
        Assert.Equal(new DateTime(2024, 1, 15), program.InstallDate);
        Assert.Equal(@"C:\Program Files\Acme Editor", program.InstallLocation);
        Assert.Equal(20480L, program.EstimatedSizeKb);
        Assert.Equal(@"C:\Program Files\Acme Editor\acme.exe,0", program.DisplayIcon);
        Assert.Equal(@"""C:\Program Files\Acme Editor\unins000.exe""", program.UninstallString);
        Assert.Equal(@"""C:\Program Files\Acme Editor\unins000.exe"" /SILENT", program.QuietUninstallString);
        Assert.Equal("{ABC-123}", program.KeyName);
        Assert.StartsWith("HKLM", program.RegistryKeyPath);
    }

    [Fact]
    public void Value_names_are_case_insensitive()
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("displayname", "Lower Case")));

        Assert.Equal("Lower Case", result.Program?.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(5)]
    public void Entries_without_a_display_name_are_skipped(object? displayName)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", displayName)));

        Assert.Null(result.Program);
        Assert.Equal(SkipReason.NoName, result.Skip);
    }

    [Fact]
    public void Entries_with_no_values_at_all_are_skipped_as_no_name()
    {
        var result = UninstallEntryParser.Parse(Entry("k"));

        Assert.Equal(SkipReason.NoName, result.Skip);
    }

    [Theory]
    [InlineData(1)]
    [InlineData("1")]
    [InlineData(1L)]
    public void System_components_are_skipped(object flag)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "Hidden"), ("SystemComponent", flag)));

        Assert.Null(result.Program);
        Assert.Equal(SkipReason.SystemComponent, result.Skip);
    }

    [Theory]
    [InlineData(0)]
    [InlineData("0")]
    public void SystemComponent_zero_is_kept(object flag)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "Visible"), ("SystemComponent", flag)));

        Assert.NotNull(result.Program);
    }

    [Fact]
    public void Updates_with_a_parent_key_are_skipped()
    {
        var result = UninstallEntryParser.Parse(
            Entry("KB123", ("DisplayName", "Security Update (KB123)"), ("ParentKeyName", "OperatingSystem")));

        Assert.Null(result.Program);
        Assert.Equal(SkipReason.Update, result.Skip);
    }

    [Fact]
    public void Empty_parent_key_name_does_not_count_as_an_update()
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "Normal"), ("ParentKeyName", "")));

        Assert.NotNull(result.Program);
    }

    [Fact]
    public void No_name_is_reported_before_system_component()
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("SystemComponent", 1)));

        Assert.Equal(SkipReason.NoName, result.Skip);
    }

    [Theory]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData(20240115, 2024, 1, 15)]
    [InlineData("2024/01/15", 2024, 1, 15)]
    [InlineData("2024-01-15", 2024, 1, 15)]
    [InlineData(" 20231231 ", 2023, 12, 31)]
    public void InstallDate_is_parsed(object value, int year, int month, int day)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "X"), ("InstallDate", value)));

        Assert.Equal(new DateTime(year, month, day), result.Program?.InstallDate);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("20241345")]
    [InlineData("")]
    [InlineData(null)]
    public void Bad_InstallDate_becomes_null(object? value)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "X"), ("InstallDate", value)));

        Assert.Null(result.Program?.InstallDate);
    }

    [Theory]
    [InlineData(2048, 2048L)]
    [InlineData("512", 512L)]
    [InlineData(5L, 5L)]
    [InlineData(-1, 4294967295L)] // registry DWORD above int.MaxValue arrives as a negative int
    public void EstimatedSize_is_read_as_kilobytes(object value, long expected)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "X"), ("EstimatedSize", value)));

        Assert.Equal(expected, result.Program?.EstimatedSizeKb);
    }

    [Theory]
    [InlineData(0)]
    [InlineData("abc")]
    [InlineData(null)]
    public void Unknown_EstimatedSize_becomes_null(object? value)
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "X"), ("EstimatedSize", value)));

        Assert.Null(result.Program?.EstimatedSizeKb);
    }

    [Fact]
    public void Strings_are_trimmed_and_blank_strings_become_null()
    {
        var result = UninstallEntryParser.Parse(
            Entry("k", ("DisplayName", "  Padded  "), ("Publisher", "   "), ("DisplayVersion", " 1.0 ")));

        Assert.Equal("Padded", result.Program?.Name);
        Assert.Null(result.Program?.Publisher);
        Assert.Equal("1.0", result.Program?.Version);
    }

    [Fact]
    public void SizeBytes_is_kilobytes_times_1024()
    {
        var result = UninstallEntryParser.Parse(Entry("k", ("DisplayName", "X"), ("EstimatedSize", 10)));

        Assert.Equal(10 * 1024L, result.Program?.SizeBytes);
    }
}
