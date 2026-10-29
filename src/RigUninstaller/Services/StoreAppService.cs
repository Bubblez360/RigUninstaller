using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigUninstaller.Services;

/// <summary>A Microsoft Store (UWP/MSIX) package.</summary>
public sealed record StoreApp(
    string Name,
    string FullName,
    string Publisher,
    string Version,
    bool IsFramework,
    bool NonRemovable,
    string SignatureKind,
    string InstallLocation = "")
{
    // Parts of Windows that look like normal apps. Removing them can break the Store, Windows Security or the shell.
    private static readonly string[] ProtectedPrefixes =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.WindowsAppRuntime", "Microsoft.UI.Xaml", "Microsoft.VCLibs", "Microsoft.NET.Native", "Microsoft.Windows.",
        "MicrosoftWindows.", "Windows.", "windows.", "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.LockApp",
        "Microsoft.CredDialogHost", "Microsoft.AsyncTextService", "Microsoft.ECApp", "Microsoft.BioEnrollment",
    };

    /// <summary>Why this package must not be removed, or null when it may be.</summary>
    public string? BlockReason =>
        IsFramework ? "A framework other apps depend on."
        : NonRemovable ? "Windows marks it as not removable."
        : SignatureKind.Equals("System", StringComparison.OrdinalIgnoreCase) ? "A Windows system package."
        : ProtectedPrefixes.Any(p => Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? "Part of Windows itself."
        : null;

    public bool CanRemove => BlockReason is null;

    // Store package names often start with the publisher's id: "19568ShareX.ShareX", "B9ECED6F.ArmouryCrate".
    private static readonly Regex PublisherIdPrefix = new(@"^(?:(?=[0-9A-F]*\d)[0-9A-F]{8}\.|\d{4,6}(?=[A-Za-z]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GuidStart = new(@"^[0-9A-F]{8}-[0-9A-F]{4}-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The package name without the publisher-id prefix, and "ShareX.ShareX" shortened to "ShareX".</summary>
    public string DisplayName
    {
        get
        {
            var name = PublisherIdPrefix.Replace(Name, string.Empty);
            if (name.Length == 0)
            {
                return Name;
            }

            var parts = name.Split('.');
            return parts.Length == 2 && parts[0].Equals(parts[1], StringComparison.OrdinalIgnoreCase) ? parts[0] : name;
        }
    }

    /// <summary>The publisher's name, or null when the package only carries an id such as "366A5DE5-2EC7-...".</summary>
    public string? ReadablePublisher => PublisherName.Length == 0 || GuidStart.IsMatch(PublisherName) ? null : PublisherName;

    /// <summary>"CN=Microsoft Corporation, O=..." becomes "Microsoft Corporation".</summary>
    public string PublisherName
    {
        get
        {
            var cn = Publisher.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
            return cn is null ? Publisher : cn[3..].Trim('"');
        }
    }
}

public sealed record StoreRemoveResult(bool Success, string Message);

public interface IStoreAppService
{
    Task<IReadOnlyList<StoreApp>> ListAsync(CancellationToken cancellationToken);

    Task<StoreRemoveResult> RemoveAsync(StoreApp app, CancellationToken cancellationToken);
}

/// <summary>Lists and removes Store apps with Get-AppxPackage / Remove-AppxPackage.</summary>
public sealed class StoreAppService : IStoreAppService
{
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9._~\-]+$", RegexOptions.Compiled);

    private const string ListScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $apps = foreach ($p in Get-AppxPackage) {
            [pscustomobject]@{ Name = $p.Name; FullName = $p.PackageFullName; Publisher = $p.Publisher; Version = $p.Version.ToString(); IsFramework = [bool]$p.IsFramework; NonRemovable = [bool]$p.NonRemovable; SignatureKind = $p.SignatureKind.ToString(); InstallLocation = [string]$p.InstallLocation }
        }
        ConvertTo-Json -InputObject @($apps) -Compress
        """;

    private const string RemoveScript = """
        $ErrorActionPreference = 'Stop'
        try { Remove-AppxPackage -Package $env:RIG_PACKAGE; Write-Output 'OK' }
        catch { Write-Output ('FAIL: ' + $_.Exception.Message); exit 2 }
        """;

    private readonly IPowerShellRunner _shell;
    private readonly IActionLog _log;

    public StoreAppService(IPowerShellRunner shell, IActionLog log)
    {
        _shell = shell;
        _log = log;
    }

    public async Task<IReadOnlyList<StoreApp>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _shell.RunAsync(ListScript, new Dictionary<string, string>(), cancellationToken);
        var apps = Parse(result.Output);
        _log.Info($"Store apps found: {apps.Count} ({apps.Count(a => a.CanRemove)} removable).");
        return apps;
    }

    public async Task<StoreRemoveResult> RemoveAsync(StoreApp app, CancellationToken cancellationToken)
    {
        if (app.BlockReason is { } reason)
        {
            _log.Error($"Refused to remove Store package {app.Name}: {reason}");
            return new StoreRemoveResult(false, $"{app.Name} cannot be removed: {reason}");
        }

        if (!SafeName.IsMatch(app.FullName))
        {
            return new StoreRemoveResult(false, "The package name has unexpected characters, so it was not touched.");
        }

        _log.Info($"Removing Store package {app.FullName}");
        var result = await _shell.RunAsync(RemoveScript, new Dictionary<string, string> { ["RIG_PACKAGE"] = app.FullName }, cancellationToken);
        if (result.ExitCode == 0 && result.Output.Contains("OK", StringComparison.Ordinal))
        {
            _log.Info($"Store package {app.Name} removed.");
            return new StoreRemoveResult(true, $"{app.Name} was removed.");
        }

        var message = result.Output.StartsWith("FAIL:", StringComparison.Ordinal) ? result.Output[5..].Trim() : result.Output;
        _log.Error($"Could not remove Store package {app.Name}: {message}");
        return new StoreRemoveResult(false, message.Length > 0 ? message : $"Windows PowerShell stopped (exit code {result.ExitCode}).");
    }

    /// <summary>Reads the JSON the list script prints (one object or an array).</summary>
    public static IReadOnlyList<StoreApp> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<StoreApp>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };

            return elements
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new StoreApp(
                    Text(e, "Name"),
                    Text(e, "FullName"),
                    Text(e, "Publisher"),
                    Text(e, "Version"),
                    Flag(e, "IsFramework"),
                    Flag(e, "NonRemovable"),
                    Text(e, "SignatureKind"),
                    Text(e, "InstallLocation")))
                .Where(a => a.Name.Length > 0 && a.FullName.Length > 0)
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<StoreApp>();
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
