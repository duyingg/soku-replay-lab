using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SokuReplayLab;

public sealed record GameInstance(int Pid, nint Window, string Exe, string Title);
public sealed record GameWindow(int Pid, long Window, string Class, string Title,
    bool Visible, bool Owned);
[StructLayout(LayoutKind.Sequential)]
internal struct WindowRect { public int Left, Top, Right, Bottom; }

/// <summary>Read-only discovery of the verified 1.10a game process and renderer window.</summary>
public static class GameDiscovery
{
    private const string ExpectedSha256 =
        "F0DB93660CEB8635A1921E0BA4686FCFBD5C492311DEE6EF7061B7BADA16731E";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GwOwner = 4;
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(nint desktop, EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId,
        EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder title, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags,
        StringBuilder exe, ref uint capacity);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    private static void EnumerateGameDesktops(EnumWindowsCallback callback)
    {
        var seen = new HashSet<nint>();
        bool Once(nint window, nint parameter) => !seen.Add(window) || callback(window, parameter);
        EnumWindows(Once, 0);
        var input = OpenInputDesktop(0, false, 0x41); // ENUMERATE | READOBJECTS
        if (input == 0) return;
        try { EnumDesktopWindows(input, Once, 0); }
        finally { CloseDesktop(input); }
    }

    public static void VerifyGameFile(string exe)
    {
        VerifyExternalGameLocation(exe);
        if (!File.Exists(exe) ||
            !string.Equals(Path.GetFileName(exe), "th123.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择非想天则 1.10a 的 th123.exe。");
        using var stream = File.OpenRead(exe);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("游戏 EXE 哈希与已验证的 1.10a 版本不一致。");
    }

    public static void VerifyExternalGameLocation(string exe)
    {
        var path = Path.GetFullPath(exe);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            Path.GetFullPath(AppContext.BaseDirectory)
        };
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(start); directory?.Parent is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SokuReplayLab.csproj")) ||
                    File.Exists(Path.Combine(directory.FullName, "ReplayLabNext.csproj")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git"))) roots.Add(directory.FullName);
            }
        }
        if (roots.Any(root => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("游戏必须位于程序和工作区之外；请直接选择原有本地游戏，不要复制游戏文件。");
    }

    public static async Task CloseVerifiedGameAsync(int pid, string expectedExe)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            var actualExe = ImagePath(checked((uint)pid));
            if (actualExe is null && process.HasExited) return;
            if (actualExe is null ||
                !string.Equals(Path.GetFullPath(actualExe),
                    Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"PID {pid} 已不是当前连接的游戏进程。");
            VerifyGameFile(actualExe);
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            try { await process.WaitForExitAsync(graceful.Token); }
            catch (OperationCanceledException) { }
            if (process.HasExited) return;
            process.Kill();
            using var forced = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(forced.Token);
        }
    }

    public static IReadOnlyList<GameInstance> Find()
    {
        var candidates = new Dictionary<uint, List<(nint hwnd, string title, string windowClass, long area)>>();
        var failed = new HashSet<uint>();
        EnumerateGameDesktops((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != 0) return true;
            var title = new StringBuilder(256);
            var name = new StringBuilder(256);
            GetWindowTextW(hwnd, title, title.Capacity);
            GetClassNameW(hwnd, name, name.Capacity);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;
            if (string.Equals(title.ToString(), "DirectX-Error", StringComparison.OrdinalIgnoreCase))
                failed.Add(pid);
            else if (string.Equals(name.ToString(), "th123_110a", StringComparison.OrdinalIgnoreCase) ||
                     title.ToString().Contains("ver1.10a", StringComparison.OrdinalIgnoreCase)) {
                GetWindowRect(hwnd, out var rect);
                var area = (long)Math.Max(0, rect.Right - rect.Left) *
                    Math.Max(0, rect.Bottom - rect.Top);
                if (!candidates.TryGetValue(pid, out var windows))
                    candidates[pid] = windows = [];
                windows.Add((hwnd, title.ToString(), name.ToString(), area));
            }
            return true;
        });
        var found = new List<GameInstance>();
        foreach (var (pid, windows) in candidates)
        {
            if (failed.Contains(pid)) continue;
            var exe = ImagePath(pid);
            if (exe is null) continue;
            try { VerifyGameFile(exe); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }
            var best = windows.Where(x =>
                    string.Equals(x.windowClass, "th123_110a", StringComparison.OrdinalIgnoreCase) ||
                    x.title.Contains("ver1.10a", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => string.Equals(x.windowClass, "th123_110a", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.title.Contains("ver1.10a", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.area).FirstOrDefault();
            if (best.hwnd != 0)
                found.Add(new GameInstance((int)pid, best.hwnd, exe, best.title));
        }
        return found;
    }

    public static IReadOnlyList<GameInstance> FindProcesses()
    {
        var windows = Find().ToDictionary(x => x.Pid);
        var found = new List<GameInstance>();
        foreach (var process in Process.GetProcessesByName("th123"))
        {
            using (process)
            {
                var exe = ImagePath(checked((uint)process.Id));
                if (exe is null) continue;
                try { VerifyGameFile(exe); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                catch (InvalidDataException) { continue; }
                found.Add(windows.TryGetValue(process.Id, out var window)
                    ? window : new GameInstance(process.Id, 0, exe, ""));
            }
        }
        return found.OrderByDescending(x => x.Window != 0).ThenBy(x => x.Pid).ToArray();
    }

    public static bool HasDirectXError(int processId)
    {
        var found = false;
        EnumerateGameDesktops((window, _) =>
        {
            if (found) return false;
            GetWindowThreadProcessId(window, out var pid);
            if (pid != processId) return true;
            var title = new StringBuilder(64);
            GetWindowTextW(window, title, title.Capacity);
            found = string.Equals(title.ToString(), "DirectX-Error",
                StringComparison.OrdinalIgnoreCase);
            return !found;
        });
        return found;
    }

    public static IReadOnlyList<GameWindow> InspectWindows()
    {
        var processes = Process.GetProcessesByName("th123").Select(x => x.Id).ToHashSet();
        var windows = new List<GameWindow>();
        var seen = new HashSet<nint>();
        bool Inspect(nint hwnd, nint _)
        {
            if (!seen.Add(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var name = new StringBuilder(256);
            var title = new StringBuilder(512);
            GetClassNameW(hwnd, name, name.Capacity);
            GetWindowTextW(hwnd, title, title.Capacity);
            if (!processes.Contains((int)pid) &&
                !string.Equals(name.ToString(), "th123_110a", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(title.ToString(), "DirectX-Error", StringComparison.OrdinalIgnoreCase))
                return true;
            windows.Add(new GameWindow((int)pid, hwnd.ToInt64(), name.ToString(),
                title.ToString(), IsWindowVisible(hwnd), GetWindow(hwnd, GwOwner) != 0));
            return true;
        }
        EnumerateGameDesktops(Inspect);
        foreach (var process in Process.GetProcessesByName("th123"))
        {
            using (process)
            {
                try {
                    foreach (ProcessThread thread in process.Threads)
                        EnumThreadWindows(checked((uint)thread.Id), Inspect, 0);
                }
                catch (InvalidOperationException) { /* Process may exit during diagnosis. */ }
                catch (System.ComponentModel.Win32Exception) { /* Thread list may be inaccessible. */ }
            }
        }
        return windows;
    }

    public static Process Launch(string exe)
    {
        VerifyGameFile(exe);
        return DisplayGuard.Launch(exe);
    }

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0) return null;
        try
        {
            var capacity = 32768u;
            var path = new StringBuilder((int)capacity);
            return QueryFullProcessImageNameW(handle, 0, path, ref capacity)
                ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
}
