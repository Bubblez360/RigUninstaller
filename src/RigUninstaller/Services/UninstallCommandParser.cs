using System.Text.RegularExpressions;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>
/// Turns the UninstallString / QuietUninstallString registry texts into commands that can be started.
/// Handles quoted paths, unquoted paths with spaces, arguments, and MSI entries (msiexec /x {product code}).
/// </summary>
public static class UninstallCommandParser
{
    private static readonly Regex ProductCode = new(
        @"\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}",
        RegexOptions.Compiled);

    private static readonly Regex MsiExec = new(
        @"^\s*""?(?:[^""]*[\\/])?msiexec(?:\.exe)?""?(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // File types that can hold an unquoted uninstall command ("C:\Program Files\X\remove.bat /S").
    private static readonly string[] CommandExtensions = { ".exe", ".bat", ".cmd", ".com" };

    private static string MsiExecPath => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    public static UninstallPlans CreatePlans(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var normalText = Clean(program.UninstallString);
        var quietText = Clean(program.QuietUninstallString);

        string? error = null;
        UninstallPlan? normal = null;
        UninstallPlan? quiet = null;

        if (normalText is not null)
        {
            (normal, error) = Parse(normalText, program, isQuiet: false);
        }

        if (quietText is not null)
        {
            var parsed = Parse(quietText, program, isQuiet: true);
            quiet = parsed.Plan;
            error ??= parsed.Error;
        }

        // Windows installer entries always have a silent form, even without a QuietUninstallString.
        if (quiet is null && normal is { IsMsi: true })
        {
            quiet = new UninstallPlan(MsiExecPath, $"/x {ProductCode.Match(normal.Arguments).Value} /qn /norestart", true, true);
        }

        if (normal is null && quiet is null)
        {
            error ??= "This program has no uninstall command in the registry.";
        }
        else
        {
            error = null;
        }

        return new UninstallPlans(normal, quiet, error);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static (UninstallPlan? Plan, string? Error) Parse(string text, InstalledProgram program, bool isQuiet)
    {
        var expanded = Environment.ExpandEnvironmentVariables(text);

        if (MsiExec.IsMatch(expanded))
        {
            // "/I{code}" (repair) and "/X{code}" both end up as "/x {code}": removing is what we are here for.
            var code = ProductCode.Match(expanded);
            if (!code.Success)
            {
                code = ProductCode.Match(program.KeyName);
            }

            if (!code.Success)
            {
                return (null, "Could not find the product code of this Windows Installer program.");
            }

            var args = isQuiet ? $"/x {code.Value} /qn /norestart" : $"/x {code.Value}";
            return (new UninstallPlan(MsiExecPath, args, true, isQuiet), null);
        }

        var split = SplitCommand(expanded);
        return split is null
            ? (null, $"The uninstall command could not be understood: {text}")
            : (new UninstallPlan(split.Value.File, split.Value.Arguments, false, isQuiet), null);
    }

    private static (string File, string Arguments)? SplitCommand(string text)
    {
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close <= 1)
            {
                return null;
            }

            return (text[1..close], text[(close + 1)..].Trim());
        }

        // Unquoted: the file ends at the first ".exe" (or similar) that is followed by a space or the end.
        var end = -1;
        foreach (var extension in CommandExtensions)
        {
            var index = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var after = index + extension.Length;
                if (after == text.Length || char.IsWhiteSpace(text[after]))
                {
                    if (end < 0 || after < end)
                    {
                        end = after;
                    }

                    break;
                }

                index = text.IndexOf(extension, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return end < 0 ? (text, string.Empty) : (text[..end], text[end..].Trim());
    }
}
