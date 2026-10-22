using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SokuReplayLab;

/// <summary>Read-only window-station diagnostics for a visible game not seen by EnumWindows.</summary>
public static class DesktopDiagnostics
{
    private delegate bool DesktopNameCallback([MarshalAs(UnmanagedType.LPWStr)] string name, nint parameter);
    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(nint handle, int index,
        StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopsW(nint windowStation,
        DesktopNameCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(nint desktop, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder title, int capacity);

    private static string Name(nint handle)
    {
        if (handle == 0) return $"<无句柄:{Marshal.GetLastWin32Error()}>";
        var buffer = new StringBuilder(256);
        return GetUserObjectInformationW(handle, 2, buffer, buffer.Capacity * 2, out _)
            ? buffer.ToString() : $"<读取失败:{Marshal.GetLastWin32Error()}>";
    }

    public static object Inspect()
    {
        var gamePids = Process.GetProcessesByName("th123").Select(x => x.Id).ToHashSet();
        var desktopNames = new List<string>();
        var currentStation = GetProcessWindowStation();
        var stationName = Name(currentStation);
        var currentDesktop = Name(GetThreadDesktop(GetCurrentThreadId()));
        var input = OpenInputDesktop(0, false, 0x41);
        var inputDesktop = Name(input);
        if (input != 0) CloseDesktop(input);
        var enumResult = EnumDesktopsW(currentStation, (name, _) => {
            desktopNames.Add(name); return true;
        }, 0);
        var enumError = enumResult ? 0 : Marshal.GetLastWin32Error();
        var desktops = new List<object>();
        foreach (var name in desktopNames)
        {
            var handle = OpenDesktopW(name, 0, false, 0x41);
            if (handle == 0) {
                desktops.Add(new { name, openError = Marshal.GetLastWin32Error() });
                continue;
            }
            var count = 0;
            var matches = new List<object>();
            var okay = EnumDesktopWindows(handle, (window, _) => {
                count++;
                GetWindowThreadProcessId(window, out var pid);
                if (!gamePids.Contains((int)pid)) return true;
                var windowClass = new StringBuilder(256);
                var title = new StringBuilder(256);
                GetClassNameW(window, windowClass, windowClass.Capacity);
                GetWindowTextW(window, title, title.Capacity);
                matches.Add(new { pid, hwnd = window.ToInt64(),
                    windowClass = windowClass.ToString(), title = title.ToString() });
                return true;
            }, 0);
            var error = okay ? 0 : Marshal.GetLastWin32Error();
            desktops.Add(new { name, count, matches, enumerateError = error });
            CloseDesktop(handle);
        }
        var gameThreads = new List<object>();
        foreach (var process in Process.GetProcessesByName("th123"))
        {
            using (process)
            {
                try {
                    foreach (ProcessThread thread in process.Threads)
                        gameThreads.Add(new { pid = process.Id, tid = thread.Id,
                            desktop = Name(GetThreadDesktop(checked((uint)thread.Id))) });
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return new { stationName, currentDesktop, inputDesktop,
            enumError, desktops, gameThreads };
    }
}
