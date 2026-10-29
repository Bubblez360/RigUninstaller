using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramScannerTests
{
    private const string Uninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string Uninstall32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static RegistryEntry Entry(string path, params (string Name, object? Value)[] values) =>
        new(path, values.ToDictionary(v => v.Name, v => v.Value));

    [Fact]
    public void Scan_filters_dedupes_counts_and_sorts_by_name()
    {
        var reader = new FakeRegistryReader();
        reader.Entries.AddRange(new[]
        {
            Entry(Uninstall + "z", ("DisplayName", "Zeta Tool"), ("UninstallString", "z.exe")),
            Entry(Uninstall + "a", ("DisplayName", "alpha app"), ("UninstallString", "a.exe")),
            Entry(Uninstall32 + "a", ("DisplayName", "Alpha App"), ("UninstallString", "a.exe")), // duplicate
            Entry(Uninstall + "noname", ("UninstallString", "x.exe")),                              // no name
            Entry(Uninstall + "sys", ("DisplayName", "Hidden"), ("SystemComponent", 1)),            // system
            Entry(Uninstall + "kb", ("DisplayName", "Patch"), ("ParentKeyName", "Parent")),         // update
        });
        var scanner = new ProgramScanner(reader, new FakeLog());

        var result = scanner.Scan();

        Assert.Equal(new[] { "alpha app", "Zeta Tool" }, result.Programs.Select(p => p.Name));
        Assert.Equal(6, result.TotalEntries);
        Assert.Equal(1, result.SkippedNoName);
        Assert.Equal(1, result.SkippedSystemComponent);
        Assert.Equal(1, result.SkippedUpdate);
        Assert.Equal(1, result.DuplicatesRemoved);
    }

    [Fact]
    public void Scan_logs_a_summary_line()
    {
        var log = new FakeLog();
        var reader = new FakeRegistryReader();
        reader.Entries.Add(Entry(Uninstall + "a", ("DisplayName", "Alpha")));

        new ProgramScanner(reader, log).Scan();

        Assert.Contains(log.Lines, l => l.StartsWith("INFO") && l.Contains("Scan finished") && l.Contains("1 programs"));
    }

    [Fact]
    public void Scan_with_no_entries_returns_empty_result()
    {
        var result = new ProgramScanner(new FakeRegistryReader(), new FakeLog()).Scan();

        Assert.Empty(result.Programs);
        Assert.Equal(0, result.TotalEntries);
    }
}
