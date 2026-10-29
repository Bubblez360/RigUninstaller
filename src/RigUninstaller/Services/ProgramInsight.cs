using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

public enum SignatureState
{
    Signed,

    /// <summary>A signature exists but its certificate is not trusted.</summary>
    Invalid,

    Unsigned,

    /// <summary>Not looked at: no uninstaller file, a Windows file, or a Windows Installer package.</summary>
    NotChecked,
}

public sealed record SignatureInfo(SignatureState State, string? Signer, string Note);

public interface ISignatureChecker
{
    SignatureInfo Check(string path);
}

/// <summary>Reads the embedded Authenticode signature of a file and checks the certificate chain.</summary>
public sealed class SignatureChecker : ISignatureChecker
{
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public SignatureInfo Check(string path)
    {
        if (!File.Exists(path))
        {
            return new SignatureInfo(SignatureState.NotChecked, null, "File not found.");
        }

        // Windows files are signed through catalog files, which this check does not read.
        if (path.StartsWith(_windowsDir + @"\", StringComparison.OrdinalIgnoreCase))
        {
            return new SignatureInfo(SignatureState.NotChecked, null, "Windows component.");
        }

        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only built-in way to read an embedded signature.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            // The organisation ("Microsoft Corporation") says more than the certificate's own name (".NET").
            var organisation = certificate.SubjectName.Name.Split(',')
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.StartsWith("O=", StringComparison.Ordinal))?[2..].Trim('"');
            var signer = string.IsNullOrWhiteSpace(organisation) ? certificate.GetNameInfo(X509NameType.SimpleName, false) : organisation;
            // Signed files stay valid after the signing certificate expires (a timestamp proves when it was signed),
            // so expiry alone does not make the signature "not trusted".
            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid
                                        | X509VerificationFlags.IgnoreCtlNotTimeValid
                                        | X509VerificationFlags.IgnoreNotTimeNested,
                },
            };
            return chain.Build(certificate)
                ? new SignatureInfo(SignatureState.Signed, signer, "Valid signature.")
                : new SignatureInfo(SignatureState.Invalid, signer, "The signing certificate is not trusted.");
        }
        catch (CryptographicException)
        {
            return new SignatureInfo(SignatureState.Unsigned, null, "No digital signature.");
        }
    }
}

/// <summary>When a program was last started, and how we know.</summary>
public sealed record LastUsedInfo(DateTime? When, string Source);

public interface IFileTimes
{
    DateTime? GetLastAccess(string path);

    /// <summary>Windows' Prefetch files for an exe name, e.g. NOTEPAD.EXE-1234ABCD.pf, with their write times.</summary>
    IReadOnlyList<DateTime> GetPrefetchTimes(string exeName);
}

public sealed class FileTimes : IFileTimes
{
    private static readonly string PrefetchFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

    public DateTime? GetLastAccess(string path)
    {
        try
        {
            return File.GetLastAccessTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<DateTime> GetPrefetchTimes(string exeName)
    {
        try
        {
            return Directory.EnumerateFiles(PrefetchFolder, exeName + "-*.pf").Select(File.GetLastWriteTime).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DateTime>();
        }
    }
}

public interface ILastUsedFinder
{
    LastUsedInfo Find(InstalledProgram program);
}

/// <summary>Prefetch data is best (Windows writes it when a program runs); the file's last access time is the fallback.</summary>
public sealed class LastUsedFinder : ILastUsedFinder
{
    private readonly IFileSystemProbe _fs;
    private readonly IFileTimes _times;

    public LastUsedFinder(IFileSystemProbe fs, IFileTimes times)
    {
        _fs = fs;
        _times = times;
    }

    public LastUsedInfo Find(InstalledProgram program)
    {
        var exe = FindMainExe(program);
        if (exe is null)
        {
            return new LastUsedInfo(null, string.Empty);
        }

        var prefetch = _times.GetPrefetchTimes(Path.GetFileName(exe));
        if (prefetch.Count > 0)
        {
            return new LastUsedInfo(prefetch.Max(), "Windows Prefetch data");
        }

        return _times.GetLastAccess(exe) is { } access
            ? new LastUsedInfo(access, "File last-access time (may be off if Windows does not update it)")
            : new LastUsedInfo(null, string.Empty);
    }

    private string? FindMainExe(InstalledProgram program)
    {
        if (DisplayIconParser.Parse(program.DisplayIcon) is { } icon
            && icon.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && _fs.Exists(icon.Path))
        {
            return icon.Path;
        }

        var folder = ProgramFolders.Tidy(program.InstallLocation);
        if (folder.Length == 0)
        {
            return null;
        }

        var exes = _fs.GetFiles(folder)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var core = ProgramNameMatcher.Describe(program).CoreCompact;
        return exes.FirstOrDefault(f =>
        {
            var stem = ProgramNameMatcher.Compact(Path.GetFileNameWithoutExtension(f));
            return stem.Length >= 3 && (core.Contains(stem, StringComparison.Ordinal) || stem.Contains(core, StringComparison.Ordinal));
        }) ?? exes.FirstOrDefault();
    }
}

/// <summary>Everything the list shows about a program that needs a look at its files.</summary>
public sealed record ProgramInsight(SignatureInfo Signature, LastUsedInfo LastUsed, string? OrphanReason);

public interface IProgramInsightService
{
    ProgramInsight Analyze(InstalledProgram program);
}

public sealed class ProgramInsightService : IProgramInsightService
{
    /// <summary>The orphan reason for an entry whose uninstaller file is gone (nothing is left to run).</summary>
    public const string MissingUninstallerReason = "The uninstaller file is missing.";

    private readonly ISignatureChecker _signatures;
    private readonly ILastUsedFinder _lastUsed;
    private readonly IFileSystemProbe _fs;
    private readonly ConcurrentDictionary<string, SignatureInfo> _signatureCache = new(StringComparer.OrdinalIgnoreCase);

    public ProgramInsightService(ISignatureChecker signatures, ILastUsedFinder lastUsed, IFileSystemProbe fs)
    {
        _signatures = signatures;
        _lastUsed = lastUsed;
        _fs = fs;
    }

    public ProgramInsight Analyze(InstalledProgram program)
    {
        var plans = UninstallCommandParser.CreatePlans(program);
        var uninstaller = (plans.Normal ?? plans.Quiet) is { } plan ? plan : null;
        return new ProgramInsight(CheckSignature(uninstaller), _lastUsed.Find(program), FindOrphanReason(program, uninstaller));
    }

    private SignatureInfo CheckSignature(UninstallPlan? plan)
    {
        if (plan is null)
        {
            return new SignatureInfo(SignatureState.NotChecked, null, "No uninstaller.");
        }

        if (plan.IsMsi)
        {
            return new SignatureInfo(SignatureState.NotChecked, "Windows Installer", "Windows Installer package.");
        }

        return Path.IsPathRooted(plan.FileName)
            ? _signatureCache.GetOrAdd(plan.FileName, _signatures.Check)
            : new SignatureInfo(SignatureState.NotChecked, null, "Not a file path.");
    }

    /// <summary>A program is "orphaned" when its entry is left but the uninstaller file or install folder is gone.</summary>
    private string? FindOrphanReason(InstalledProgram program, UninstallPlan? plan)
    {
        if (plan is { IsMsi: false } && Path.IsPathRooted(plan.FileName) && !_fs.Exists(plan.FileName))
        {
            return MissingUninstallerReason;
        }

        var location = ProgramFolders.Tidy(program.InstallLocation);
        return location.Length > 0 && Path.IsPathRooted(location) && !_fs.Exists(location)
            ? "The install folder is missing."
            : null;
    }
}
