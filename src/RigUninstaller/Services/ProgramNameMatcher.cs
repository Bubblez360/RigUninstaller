using System.Text.RegularExpressions;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>
/// The names of a program prepared for matching: a cleaned product name (no versions or "x64"),
/// the words that identify it, and the publisher without "Inc." and the like. All compact forms are
/// lower case letters and digits only, so "Acme-Editor", "Acme Editor" and "AcmeEditor" are equal.
/// </summary>
/// <param name="FirstWord">The first word of a multi-word name; often the vendor ("Google" in "Google Chrome").</param>
/// <param name="NamePrefixes">Compact forms of the first 2..n-1 words together.</param>
/// <param name="NameSuffixes">Compact forms of the last 1..n-1 words together.</param>
/// <param name="VersionDigits">Digits of the program's version numbers, e.g. "3119" for 3.11.9.</param>
public sealed record ProgramNames(
    string CoreCompact,
    string FullCompact,
    IReadOnlyList<string> DistinctiveTokens,
    string? PublisherCompact,
    bool PublisherIsShared,
    string? FirstWord,
    IReadOnlyList<string> NamePrefixes,
    IReadOnlyList<string> NameSuffixes,
    IReadOnlyList<string> VersionDigits);

/// <summary>Decides how closely a folder, file or registry key name matches a removed program.</summary>
public static class ProgramNameMatcher
{
    private const int MinCandidateLength = 3;
    private const int MinTokenLength = 4;
    private const int MinCoreLengthForVersionSuffix = 4;
    private const int MinCoreLengthForContains = 6;
    private const int MinLengthForPartialName = 5;

    private static readonly Regex Brackets = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex VersionLike = new(@"^v?\d[\d._\-]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex CamelBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    // Words that say nothing about which program it is.
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "ver", "edition", "update", "build", "release", "latest",
        "x64", "x86", "x32", "amd64", "arm64", "64-bit", "32-bit", "64bit", "32bit",
    };

    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "bv", "plc",
        "pty", "pvt", "lp", "the",
    };

    // Too common to prove anything on their own ("Setup", "Helper"...).
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "software", "tools", "tool", "setup", "install", "installer", "update", "updater", "service", "services",
        "application", "client", "server", "driver", "drivers", "runtime", "redistributable", "edition",
        "professional", "free", "plus", "manager", "helper", "support", "framework", "development", "program",
        "programs", "microsoft", "windows", "system", "data", "files", "common", "shared", "version",
        "product", "products", "desktop", "online", "global", "general", "launcher",
    };

    // Publishers whose folders hold far more than one program's files.
    private static readonly HashSet<string> SharedPublishers = new(StringComparer.Ordinal) { "microsoft", "windows" };

    public static string Compact(string text)
    {
        var letters = text.Where(char.IsLetterOrDigit).ToArray();
        return new string(letters).ToLowerInvariant();
    }

    public static ProgramNames Describe(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var rawWords = Brackets.Replace(program.Name, " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !FillerWords.Contains(word) && word.Any(char.IsLetterOrDigit))
            .ToList();
        var words = rawWords.Where(word => !VersionLike.IsMatch(word)).ToList();

        var full = Compact(program.Name);
        var core = Compact(string.Concat(words));
        if (core.Length == 0)
        {
            core = full;
        }

        var tokens = words
            .SelectMany(word => NonAlphanumeric.Split(word))
            .Select(part => part.ToLowerInvariant())
            .Where(part => part.Length >= MinTokenLength && !GenericWords.Contains(part))
            .Distinct()
            .ToList();

        // Whole words in order, so partial names can be checked on word boundaries ("games" is not a word of "GameSDK").
        var compactWords = words.Select(Compact).Where(word => word.Length > 0).ToList();
        var count = compactWords.Count;
        var first = count >= 2 ? compactWords[0] : null;
        var prefixes = Enumerable.Range(2, Math.Max(0, count - 2)).Select(k => string.Concat(compactWords.Take(k))).ToList();
        var suffixes = Enumerable.Range(1, Math.Max(0, count - 1)).Select(k => string.Concat(compactWords.Skip(count - k))).ToList();

        // Version numbers come from the name ("Python 3.11") and from the registry's version value.
        var versionDigits = rawWords.Where(word => VersionLike.IsMatch(word)).Append(program.Version ?? string.Empty)
            .Select(text => new string(text.Where(char.IsDigit).ToArray()))
            .Where(digits => digits.Length > 0)
            .Distinct()
            .ToList();

        var (publisher, shared) = DescribePublisher(program.Publisher);
        return new ProgramNames(core, full, tokens, publisher, shared, first, prefixes, suffixes, versionDigits);
    }

    /// <summary>Scores a name against the product name. Null when it does not look related.</summary>
    public static (Confidence Confidence, string Reason)? ScoreAsProduct(string candidateName, ProgramNames names)
    {
        var c = Compact(candidateName ?? string.Empty);
        if (c.Length < MinCandidateLength)
        {
            return null;
        }

        var core = names.CoreCompact;

        if (c == core || c == names.FullCompact)
        {
            return (Confidence.High, "Same name as the program.");
        }

        if (core.Length >= MinCoreLengthForVersionSuffix && c.StartsWith(core, StringComparison.Ordinal) && c[core.Length..].All(char.IsDigit))
        {
            // "Python 3.12" is only High for a removed Python 3.12; for 3.11 it belongs to another version.
            var suffix = c[core.Length..];
            return names.VersionDigits.Any(digits => digits.StartsWith(suffix, StringComparison.Ordinal))
                ? (Confidence.High, "Program name followed by its version number.")
                : (Confidence.Medium, "Program name with a different version number.");
        }

        if (core.Length >= MinCoreLengthForContains && c.StartsWith(core, StringComparison.Ordinal))
        {
            return (Confidence.Medium, "Name starts with the program name.");
        }

        if (c.Length >= MinLengthForPartialName && !GenericWords.Contains(c) && c.Length < core.Length)
        {
            if (names.NamePrefixes.Contains(c) || names.NameSuffixes.Contains(c))
            {
                return (Confidence.Medium, "Name is part of the program name.");
            }

            if (names.FirstWord == c)
            {
                // The first word alone is the product ("Composer") or the vendor ("Google"): the publisher tells which.
                var vendorLike = names.PublisherCompact is { } publisher && (publisher.StartsWith(c, StringComparison.Ordinal) || c.StartsWith(publisher, StringComparison.Ordinal));
                return vendorLike
                    ? (Confidence.Low, "Matches only the first word of the name, which looks like the vendor. May hold their other programs.")
                    : (Confidence.Medium, "Matches the first word of the program name.");
            }
        }

        var shared = CandidateTokens(candidateName ?? string.Empty).FirstOrDefault(token => names.DistinctiveTokens.Contains(token));
        return shared is null ? null : (Confidence.Low, $"Shares the word \"{shared}\" with the program name.");
    }

    /// <summary>True when the name is the publisher's name, e.g. a folder called "Oracle".</summary>
    public static bool IsPublisherName(string candidateName, ProgramNames names) =>
        names.PublisherCompact is { } publisher && Compact(candidateName ?? string.Empty) == publisher;

    private static (string? Publisher, bool Shared) DescribePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return (null, false);
        }

        var words = NonAlphanumeric.Split(publisher)
            .Where(word => word.Length > 0 && !CompanySuffixes.Contains(word));
        var compact = Compact(string.Concat(words));
        return compact.Length < MinCandidateLength ? (null, false) : (compact, SharedPublishers.Contains(compact));
    }

    /// <summary>Words of a name, also splitting CamelCase ("MyEditorSettings" gives my, editor, settings).</summary>
    private static IEnumerable<string> CandidateTokens(string name)
    {
        foreach (var part in NonAlphanumeric.Split(name).Where(p => p.Length > 0))
        {
            yield return part.ToLowerInvariant();
            foreach (var piece in CamelBoundary.Split(part))
            {
                yield return piece.ToLowerInvariant();
            }
        }
    }
}
