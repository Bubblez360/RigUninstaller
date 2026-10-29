using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramDeduplicatorTests
{
    private static InstalledProgram Program(
        string name,
        string? version = "1.0",
        string? publisher = "Acme",
        string? uninstall = "u.exe",
        string? location = null,
        string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\k") =>
        new()
        {
            Name = name,
            Version = version,
            Publisher = publisher,
            UninstallString = uninstall,
            InstallLocation = location,
            RegistryKeyPath = key,
            KeyName = key[(key.LastIndexOf('\\') + 1)..],
        };

    [Fact]
    public void Same_name_version_and_publisher_collapse_to_one()
    {
        var a = Program("Acme Editor", key: @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\a");
        var b = Program("Acme Editor", key: @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\a");

        var result = ProgramDeduplicator.Deduplicate(new[] { a, b });

        Assert.Single(result.Programs);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Comparison_ignores_case_and_surrounding_spaces()
    {
        var result = ProgramDeduplicator.Deduplicate(new[]
        {
            Program("Acme Editor", publisher: "Acme"),
            Program("ACME EDITOR ", publisher: "acme"),
        });

        Assert.Single(result.Programs);
    }

    [Fact]
    public void Different_versions_are_kept_separately()
    {
        var result = ProgramDeduplicator.Deduplicate(new[]
        {
            Program("Runtime", version: "1.0"),
            Program("Runtime", version: "2.0"),
        });

        Assert.Equal(2, result.Programs.Count);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Entry_with_an_uninstall_string_wins_over_one_without()
    {
        var broken = Program("Acme Editor", uninstall: null, key: @"HKLM\SOFTWARE\x\broken");
        var good = Program("Acme Editor", uninstall: "u.exe", key: @"HKLM\SOFTWARE\x\good");

        var result = ProgramDeduplicator.Deduplicate(new[] { broken, good });

        Assert.Equal("good", Assert.Single(result.Programs).KeyName);
    }

    [Fact]
    public void On_a_tie_the_first_entry_wins_and_order_is_kept()
    {
        var first = Program("A", key: @"HKLM\SOFTWARE\x\first");
        var other = Program("B", key: @"HKLM\SOFTWARE\x\other");
        var dupe = Program("A", key: @"HKCU\SOFTWARE\x\dupe");

        var result = ProgramDeduplicator.Deduplicate(new[] { first, other, dupe });

        Assert.Equal(new[] { "first", "other" }, result.Programs.Select(p => p.KeyName));
    }

    [Fact]
    public void Empty_input_gives_empty_result()
    {
        var result = ProgramDeduplicator.Deduplicate(Array.Empty<InstalledProgram>());

        Assert.Empty(result.Programs);
        Assert.Equal(0, result.Removed);
    }
}
