using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SokuReplayLab;

/// <summary>Start the game suspended and apply the same Windows display job rule as the old launcher.</summary>
internal static class DisplayGuard
{
    private const uint CreateSuspended = 0x4;
    private const int JobObjectBasicUiRestrictions = 4;
    private const uint UiLimitDisplaySettings = 0x10;
    private static readonly Dictionary<int, nint> Jobs = [];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Cb;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public nint Reserved2Pointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, bool inheritHandles,
        uint creationFlags, nint environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass,
        ref uint information, uint informationLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    internal static Process Launch(string exe)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建显示保护作业对象。");
        var processInfo = new ProcessInformation();
        var created = false;
        try
        {
            var restriction = UiLimitDisplaySettings;
            if (!SetInformationJobObject(job, JobObjectBasicUiRestrictions,
                    ref restriction, sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法限制游戏切换显示模式。");
            var startup = new StartupInfo { Cb = (uint)Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder('"' + exe + '"');
            if (!CreateProcessW(exe, command, 0, 0, false, CreateSuspended, 0,
                    Path.GetDirectoryName(exe)!, ref startup, out processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动游戏。");
            created = true;
            if (!AssignProcessToJobObject(job, processInfo.Process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在游戏运行前应用显示保护。");
            var status = NtResumeProcess(processInfo.Process);
            if (status != 0) throw new InvalidOperationException($"无法恢复游戏进程：NTSTATUS 0x{status:X8}。");
            var process = Process.GetProcessById(checked((int)processInfo.ProcessId));
            process.EnableRaisingEvents = true;
            lock (Jobs) Jobs[process.Id] = job;
            process.Exited += (_, _) => {
                lock (Jobs)
                {
                    if (Jobs.Remove(process.Id, out var handle)) CloseHandle(handle);
                }
            };
            if (process.HasExited)
            {
                lock (Jobs)
                {
                    if (Jobs.Remove(process.Id, out var handle)) CloseHandle(handle);
                }
            }
            return process;
        }
        catch
        {
            if (created) TerminateProcess(processInfo.Process, 1);
            CloseHandle(job);
            throw;
        }
        finally
        {
            if (processInfo.Thread != 0) CloseHandle(processInfo.Thread);
            if (processInfo.Process != 0) CloseHandle(processInfo.Process);
        }
    }
}
