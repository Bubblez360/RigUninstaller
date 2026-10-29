using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramNameMatcherTests
{
    private static InstalledProgram P(string name, string? publisher = null) => new()
    {
        Name = name,
        Publisher = publisher,
        RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name,
        KeyName = name,
    };

    private static ProgramNames Names(string name, string? publisher = null) => ProgramNameMatcher.Describe(P(name, publisher));

    // ---- Describe: product name ----

    [Theory]
    [InlineData("Java(TM) SE Development Kit 21.0.7 (64-bit)", "javasedevelopmentkit")]
    [InlineData("7-Zip 24.08 (x64 edition)", "7zip")]
    [InlineData("Ollama version 0.32.9", "ollama")]
    [InlineData("Java 8 Update 451", "java")]
    [InlineData("LocalSend version 1.17.0", "localsend")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "microsoftvisualcredistributable")]
    [InlineData("Acme Editor", "acmeeditor")]
    public void Core_name_drops_versions_architecture_and_filler(string name, string expectedCore)
    {
        Assert.Equal(expectedCore, Names(name).CoreCompact);
    }

    [Fact]
    public void A_name_made_only_of_numbers_falls_back_to_the_full_name()
    {
        Assert.Equal("2024", Names("2024").CoreCompact);
    }

    [Fact]
    public void Distinctive_tokens_skip_short_and_generic_words()
    {
        var tokens = Names("Acme Editor Pro Setup").DistinctiveTokens;

        Assert.Equal(new[] { "acme", "editor" }, tokens);
    }

    [Fact]
    public void CamelCase_in_a_program_name_is_split_into_words()
    {
        Assert.Contains("localsend", Names("LocalSend").DistinctiveTokens);
    }

    // ---- Describe: publisher ----

    [Theory]
    [InlineData("Oracle Corporation", "oracle")]
    [InlineData("Igor Pavlov", "igorpavlov")]
    [InlineData("Acme Inc.", "acme")]
    [InlineData("Acme Software GmbH", "acmesoftware")]
    public void Publisher_drops_company_suffixes(string publisher, string expected)
    {
        Assert.Equal(expected, Names("X Program", publisher).PublisherCompact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AB")]
    public void Missing_or_too_short_publisher_gives_no_publisher_match(string? publisher)
    {
        Assert.Null(Names("X Program", publisher).PublisherCompact);
    }

    [Theory]
    [InlineData("Microsoft Corporation")]
    [InlineData("Microsoft")]
    public void Microsoft_is_marked_as_a_shared_publisher(string publisher)
    {
        Assert.True(Names("X Program", publisher).PublisherIsShared);
    }

    [Fact]
    public void Other_publishers_are_not_marked_shared()
    {
        Assert.False(Names("X Program", "Acme Inc.").PublisherIsShared);
    }

    // ---- ScoreAsProduct ----

    [Theory]
    [InlineData("Acme Editor")]
    [InlineData("AcmeEditor")]
    [InlineData("acme-editor")]
    [InlineData("ACME_EDITOR")]
    [InlineData("Acme Editor 2")]
    [InlineData("AcmeEditor25")]
    public void Same_name_or_name_plus_version_is_high(string candidate)
    {
        var score = ProgramNameMatcher.ScoreAsProduct(candidate, Names("Acme Editor 2.5"));

        Assert.Equal(Confidence.High, score?.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(score?.Reason));
    }

    [Theory]
    [InlineData("AcmeEditorPro")]
    [InlineData("Acme Editor Logs")]
    public void Name_with_extra_letters_around_it_is_medium(string candidate)
    {
        var score = ProgramNameMatcher.ScoreAsProduct(candidate, Names("Acme Editor"));

        Assert.Equal(Confidence.Medium, score?.Confidence);
    }

    [Theory]
    [InlineData("Acme Editor 3")]
    [InlineData("AcmeEditor12")]
    public void Name_plus_a_different_version_is_only_medium(string candidate)
    {
        // "Python 3.12" must not be rated High when "Python 3.11" is the one that was removed.
        var score = ProgramNameMatcher.ScoreAsProduct(candidate, Names("Acme Editor 2.5"));

        Assert.Equal(Confidence.Medium, score?.Confidence);
        Assert.Contains("different version", score?.Reason);
    }

    [Fact]
    public void The_programs_own_Version_value_also_counts_for_version_suffixes()
    {
        var program = new InstalledProgram { Name = "Python", Version = "3.11.9", RegistryKeyPath = @"HKLM\x\y", KeyName = "y" };
        var names = ProgramNameMatcher.Describe(program);

        Assert.Equal(Confidence.High, ProgramNameMatcher.ScoreAsProduct("Python 3.11", names)?.Confidence);
        Assert.Equal(Confidence.Medium, ProgramNameMatcher.ScoreAsProduct("Python 3.12", names)?.Confidence);
    }

    [Fact]
    public void Letters_after_the_name_must_start_the_candidate_not_just_appear_inside_it()
    {
        // "claude-cli-nodejs" mentions nodejs but is another program's folder.
        Assert.Null(ProgramNameMatcher.ScoreAsProduct("claude-cli-nodejs", Names("Node.js")));

        // Only the shared word "deadlock" links it, which is Low at best.
        Assert.Equal(Confidence.Low, ProgramNameMatcher.ScoreAsProduct("dev.stormix.deadlock-mod-manager", Names("Deadlock"))?.Confidence);
    }

    [Fact]
    public void Partial_names_must_follow_word_boundaries()
    {
        // "games" is only the start of the word "GameSDK", not a word of the name.
        Assert.Null(ProgramNameMatcher.ScoreAsProduct("games", Names("GameSDK Service")));
        Assert.Equal(Confidence.Medium, ProgramNameMatcher.ScoreAsProduct("asus_framework", Names("ASUS Framework Service"))?.Confidence);
        Assert.Null(ProgramNameMatcher.ScoreAsProduct("Input", Names("Microsoft GameInput")));
    }

    [Fact]
    public void The_first_word_alone_is_low_when_it_looks_like_the_vendor()
    {
        var names = Names("Google Chrome", "Google LLC");

        var score = ProgramNameMatcher.ScoreAsProduct("Google", names);

        Assert.Equal(Confidence.Low, score?.Confidence);
        Assert.Contains("vendor", score?.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_first_word_alone_is_medium_when_the_publisher_is_unrelated()
    {
        var score = ProgramNameMatcher.ScoreAsProduct("Composer", Names("Composer - PHP Dependency Manager", "getcomposer.org"));

        Assert.Equal(Confidence.Medium, score?.Confidence);
    }

    [Fact]
    public void The_word_Latest_is_not_part_of_the_name()
    {
        Assert.Equal("hunterpie", Names("HunterPie version Latest").CoreCompact);
    }

    [Fact]
    public void A_shortened_product_name_is_medium()
    {
        var score = ProgramNameMatcher.ScoreAsProduct("Acme Editor", Names("Acme Editor Pro"));

        Assert.Equal(Confidence.Medium, score?.Confidence);
    }

    [Fact]
    public void The_product_part_of_a_vendor_prefixed_name_is_medium()
    {
        var score = ProgramNameMatcher.ScoreAsProduct("VirtualBox", Names("Oracle VM VirtualBox"));

        Assert.Equal(Confidence.Medium, score?.Confidence);
    }

    [Fact]
    public void Sharing_only_one_word_is_low()
    {
        var score = ProgramNameMatcher.ScoreAsProduct("Java", Names("Java SE Development Kit"));

        Assert.Equal(Confidence.Low, score?.Confidence);
        Assert.Contains("java", score?.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CamelCase_candidates_are_split_before_comparing_words()
    {
        var score = ProgramNameMatcher.ScoreAsProduct("MyEditorSettings", Names("Acme Editor Studio Suite"));

        Assert.Equal(Confidence.Low, score?.Confidence);
    }

    [Theory]
    [InlineData("Notepad++")]
    [InlineData("Totally Different")]
    [InlineData("ab")]
    [InlineData("")]
    public void Unrelated_or_tiny_names_do_not_match(string candidate)
    {
        Assert.Null(ProgramNameMatcher.ScoreAsProduct(candidate, Names("Acme Editor")));
    }

    [Fact]
    public void Short_product_names_only_match_exactly_or_with_a_version_suffix()
    {
        var names = Names("7-Zip 24.08");

        Assert.Equal(Confidence.High, ProgramNameMatcher.ScoreAsProduct("7-Zip", names)?.Confidence);
        Assert.Equal(Confidence.High, ProgramNameMatcher.ScoreAsProduct("7zip2", names)?.Confidence);
        Assert.Null(ProgramNameMatcher.ScoreAsProduct("7zipper", names));
    }

    [Fact]
    public void Generic_words_alone_never_match()
    {
        var names = Names("Setup Helper Tool");

        Assert.Null(ProgramNameMatcher.ScoreAsProduct("Helper", names));
        Assert.Null(ProgramNameMatcher.ScoreAsProduct("Setup", names));
    }

    // ---- IsPublisherName ----

    [Fact]
    public void Publisher_folder_is_recognised_by_its_cleaned_name()
    {
        var names = Names("Acme Editor", "Acme Corporation");

        Assert.True(ProgramNameMatcher.IsPublisherName("Acme", names));
        Assert.True(ProgramNameMatcher.IsPublisherName("ACME", names));
        Assert.False(ProgramNameMatcher.IsPublisherName("Acme Editor", names));
        Assert.False(ProgramNameMatcher.IsPublisherName("Other", names));
    }

    [Fact]
    public void No_publisher_means_no_publisher_match()
    {
        Assert.False(ProgramNameMatcher.IsPublisherName("Acme", Names("Acme Editor")));
    }
}
