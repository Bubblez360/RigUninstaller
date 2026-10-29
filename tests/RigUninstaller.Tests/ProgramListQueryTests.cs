using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramListQueryTests
{
    private static InstalledProgram P(
        string name,
        string? publisher = null,
        string? version = null,
        long? sizeKb = null,
        DateTime? date = null) =>
        new()
        {
            Name = name,
            Publisher = publisher,
            Version = version,
            EstimatedSizeKb = sizeKb,
            InstallDate = date,
            RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name,
            KeyName = name,
        };

    private static string[] Names(IEnumerable<InstalledProgram> programs) => programs.Select(p => p.Name).ToArray();

    // ---- filter ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_matches_everything(string? query)
    {
        Assert.True(ProgramListQuery.Matches(P("Anything"), query));
    }

    [Fact]
    public void Matches_name_ignoring_case()
    {
        Assert.True(ProgramListQuery.Matches(P("Java Development Kit"), "jAVA"));
        Assert.False(ProgramListQuery.Matches(P("Notepad"), "java"));
    }

    [Fact]
    public void Matches_publisher_and_version()
    {
        var program = P("Tool", publisher: "Oracle Corporation", version: "21.0.7");

        Assert.True(ProgramListQuery.Matches(program, "oracle"));
        Assert.True(ProgramListQuery.Matches(program, "21.0"));
    }

    [Fact]
    public void Every_word_must_match_somewhere()
    {
        var program = P("Java Development Kit", publisher: "Oracle");

        Assert.True(ProgramListQuery.Matches(program, "java oracle"));
        Assert.True(ProgramListQuery.Matches(program, "oracle   kit"));
        Assert.False(ProgramListQuery.Matches(program, "java microsoft"));
    }

    // ---- sort ----

    [Fact]
    public void Name_sort_ignores_case_in_both_directions()
    {
        var programs = new[] { P("beta"), P("Alpha"), P("Gamma") };

        Assert.Equal(new[] { "Alpha", "beta", "Gamma" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Name, false)));
        Assert.Equal(new[] { "Gamma", "beta", "Alpha" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Name, true)));
    }

    [Fact]
    public void Size_sort_puts_unknown_sizes_last_in_both_directions()
    {
        var programs = new[]
        {
            P("Unknown"),
            P("Big", sizeKb: 5000),
            P("Small", sizeKb: 10),
        };

        Assert.Equal(new[] { "Small", "Big", "Unknown" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Size, false)));
        Assert.Equal(new[] { "Big", "Small", "Unknown" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Size, true)));
    }

    [Fact]
    public void Date_sort_puts_unknown_dates_last_in_both_directions()
    {
        var programs = new[]
        {
            P("NoDate"),
            P("New", date: new DateTime(2025, 6, 1)),
            P("Old", date: new DateTime(2020, 1, 1)),
        };

        Assert.Equal(new[] { "Old", "New", "NoDate" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.InstallDate, false)));
        Assert.Equal(new[] { "New", "Old", "NoDate" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.InstallDate, true)));
    }

    [Fact]
    public void Publisher_sort_uses_name_as_tiebreaker_in_ascending_name_order()
    {
        var programs = new[]
        {
            P("Zeta", publisher: "Acme"),
            P("Alpha", publisher: "Acme"),
            P("Mid", publisher: "Beta Inc"),
            P("Anon"),
        };

        Assert.Equal(new[] { "Alpha", "Zeta", "Mid", "Anon" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Publisher, false)));
        Assert.Equal(new[] { "Mid", "Alpha", "Zeta", "Anon" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Publisher, true)));
    }

    [Fact]
    public void Version_sort_compares_numbers_not_text()
    {
        var programs = new[]
        {
            P("v10", version: "10.0"),
            P("v2", version: "2.0"),
            P("v1_10", version: "1.10"),
            P("v1_2_3", version: "1.2.3"),
        };

        Assert.Equal(new[] { "v1_2_3", "v1_10", "v2", "v10" }, Names(ProgramListQuery.Apply(programs, null, SortColumn.Version, false)));
    }

    [Fact]
    public void Apply_filters_then_sorts()
    {
        var programs = new[]
        {
            P("Java Kit B", publisher: "Oracle", sizeKb: 200),
            P("Notepad", sizeKb: 999),
            P("Java Kit A", publisher: "Oracle", sizeKb: 500),
        };

        var result = ProgramListQuery.Apply(programs, "java", SortColumn.Size, descending: true);

        Assert.Equal(new[] { "Java Kit A", "Java Kit B" }, Names(result));
    }
}

public class VersionTextComparerTests
{
    [Theory]
    [InlineData("2.0", "10.0", -1)]
    [InlineData("10.0", "2.0", 1)]
    [InlineData("1.2.3", "1.10", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0", "1.0.1", -1)]
    [InlineData("21.0.7.0", "24.0.1.0", -1)]
    public void Compares_dotted_numbers_naturally(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(VersionTextComparer.Instance.Compare(a, b)));
    }

    [Fact]
    public void Falls_back_to_text_when_there_are_no_digits()
    {
        Assert.True(VersionTextComparer.Instance.Compare("alpha", "beta") < 0);
    }
}
