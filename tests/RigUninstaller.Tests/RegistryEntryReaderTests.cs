using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>Smoke test against the real registry of the machine running the tests (read-only).</summary>
public class RegistryEntryReaderTests
{
    [Fact]
    public void ReadAll_returns_entries_from_the_real_uninstall_keys()
    {
        var reader = new RegistryEntryReader(new FakeLog());

        var entries = reader.ReadAll();

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.True(
                e.KeyPath.StartsWith(@"HKLM\", StringComparison.Ordinal) || e.KeyPath.StartsWith(@"HKCU\", StringComparison.Ordinal),
                e.KeyPath);
            Assert.Contains(@"\Uninstall\", e.KeyPath);
        });
    }

    [Fact]
    public void Real_scan_finds_programs_with_names()
    {
        var result = new ProgramScanner(new RegistryEntryReader(new FakeLog()), new FakeLog()).Scan();

        Assert.NotEmpty(result.Programs);
        Assert.All(result.Programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }
}
