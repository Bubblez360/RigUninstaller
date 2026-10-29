using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

/// <summary>Starts a program and waits for it to end. Behind an interface so uninstall flows are testable.</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a program and waits for it and everything it starts. Many uninstallers copy themselves to a
/// temp folder and quit at once, so the first process ending does not mean the uninstall is over.
/// A job object tracks all child processes so we can wait until none are left.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorBadExeFormat = 193;
    private const int ErrorCancelledByUser = 1223;

    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(300);

    public async Task<ProcessRunResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        // Null (not an empty string) when the file has no known folder: CreateProcess rejects an empty one.
        var workingDirectory = WorkingDirectoryFor(fileName);
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        using var job = JobObject.TryCreate();
        Process? process;
        try
        {
            // Started suspended and put into the job before it runs, so nothing it starts can slip out.
            process = job is not null && StartInJob(fileName, arguments, workingDirectory, job) is { } started
                ? started
                : Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return new ProcessRunResult(false, -1, false, DescribeStartError(ex, fileName));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new ProcessRunResult(false, -1, false, $"The uninstaller could not be started: {ex.Message}");
        }

        if (process is null)
        {
            return new ProcessRunResult(false, -1, false, "The uninstaller could not be started.");
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                var exitCode = process.ExitCode;

                if (job is not null)
                {
                    while (job.ActiveProcessCount > 0)
                    {
                        await Task.Delay(JobPollInterval, cancellationToken);
                    }
                }

                return new ProcessRunResult(true, exitCode, false, null);
            }
            catch (OperationCanceledException)
            {
                // Stop waiting only. The uninstaller itself is left running, never killed.
                return new ProcessRunResult(true, -1, true, null);
            }
        }
    }

    /// <summary>
    /// Creates the process suspended, adds it to the job, then lets it run. Returns null (so the caller falls back to a
    /// normal start) only if the job could not be used; a real start failure throws <see cref="Win32Exception"/>.
    /// </summary>
    private static Process? StartInJob(string fileName, string arguments, string? workingDirectory, JobObject job)
    {
        var commandLine = new StringBuilder(fileName.Contains(' ') && !fileName.StartsWith('"') ? $"\"{fileName}\"" : fileName);
        if (arguments.Length > 0)
        {
            commandLine.Append(' ').Append(arguments);
        }

        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!NativeMethods.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended, IntPtr.Zero, workingDirectory, ref startup, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var process = Process.GetProcessById((int)info.ProcessId);
            if (!job.TryAssign(process))
            {
                // Tracking children is lost, but the uninstaller itself still runs and is awaited.
                Debug.WriteLine("Could not add the uninstaller to the job object.");
            }

            NativeMethods.ResumeThread(info.Thread);
            return process;
        }
        finally
        {
            NativeMethods.CloseHandle(info.Thread);
            NativeMethods.CloseHandle(info.Process);
        }
    }

    private static string? WorkingDirectoryFor(string fileName)
    {
        if (!Path.IsPathRooted(fileName))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fileName);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    /// <summary>Start of the error text when the uninstaller file does not exist (the entry is only a leftover).</summary>
    public const string MissingFilePrefix = "The uninstaller file was not found";

    private static string DescribeStartError(Win32Exception ex, string fileName) => ex.NativeErrorCode switch
    {
        ErrorFileNotFound or ErrorPathNotFound => $"{MissingFilePrefix}: {fileName}",
        ErrorCancelledByUser => "Windows permission for the uninstaller was refused.",
        ErrorBadExeFormat => $"The uninstaller is not a valid program: {fileName}",
        _ => $"The uninstaller could not be started: {ex.Message}",
    };

    /// <summary>Thin wrapper over a Windows job object, used only to count live processes.</summary>
    private sealed class JobObject : IDisposable
    {
        private const int JobObjectBasicAccountingInformation = 1;

        private readonly IntPtr _handle;

        private JobObject(IntPtr handle)
        {
            _handle = handle;
        }

        public static JobObject? TryCreate()
        {
            var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            return handle == IntPtr.Zero ? null : new JobObject(handle);
        }

        public bool TryAssign(Process process) => NativeMethods.AssignProcessToJobObject(_handle, process.Handle);

        public int ActiveProcessCount =>
            NativeMethods.QueryInformationJobObject(
                _handle,
                JobObjectBasicAccountingInformation,
                out var info,
                Marshal.SizeOf<AccountingInformation>(),
                IntPtr.Zero)
                ? (int)info.ActiveProcesses
                : 0;

        // Closing the handle does not kill the processes: no kill-on-close limit is set.
        public void Dispose() => NativeMethods.CloseHandle(_handle);
    }

    private const uint CreateSuspended = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(
            IntPtr job,
            int infoClass,
            out AccountingInformation info,
            int infoLength,
            IntPtr returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
