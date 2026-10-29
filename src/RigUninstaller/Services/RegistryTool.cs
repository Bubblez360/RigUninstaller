using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Registry writes, behind an interface so no test ever changes the real registry through it.</summary>
public interface IRegistryTool
{
    /// <summary>Saves a key and everything below it to a .reg file that Windows can re-import.</summary>
    Task<RegistryExportResult> ExportAsync(string keyPath, string file, CancellationToken cancellationToken);

    /// <summary>Deletes a key and its sub keys. A key that is already gone is not an error.</summary>
    void DeleteKeyTree(string keyPath);
}

/// <summary>Backs keys up with Windows' own reg.exe and deletes them through the registry API.</summary>
public sealed class RegistryTool : IRegistryTool
{
    public async Task<RegistryExportResult> ExportAsync(string keyPath, string file, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RegistryExportResult(false, $"Could not create the backup folder: {ex.Message}");
        }

        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default,
        };
        foreach (var argument in new[] { "export", keyPath, file, "/y" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new RegistryExportResult(false, "reg.exe could not be started.");
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var message = (await error + await output).Trim();

            // A backup only counts when the file really exists and has content.
            if (process.ExitCode == 0 && File.Exists(file) && new FileInfo(file).Length > 0)
            {
                return new RegistryExportResult(true, "Backup saved.");
            }

            TryDelete(file);
            return new RegistryExportResult(false, message.Length > 0 ? message : $"reg.exe exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex)
        {
            return new RegistryExportResult(false, $"reg.exe could not be started: {ex.Message}");
        }
    }

    public void DeleteKeyTree(string keyPath)
    {
        if (!RegistryPath.TryParse(keyPath, out var hive, out var subPath))
        {
            throw new ArgumentException($"Not a registry key path I can change: {keyPath}", nameof(keyPath));
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
        }
        catch (SecurityException ex)
        {
            // Same meaning as "access denied" for the caller.
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover empty backup file is harmless; the export failure is what gets reported.
        }
    }
}
