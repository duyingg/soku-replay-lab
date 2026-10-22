using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SokuReplayLab;

// A second verified engine can evaluate independent escape branches concurrently.
// The worker has its own writable configuration; large, immutable game archives
// are linked so preparing it does not copy several gigabytes of data.
internal sealed class EscapeWorkerGame : IAsyncDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string path, string source, nint reserved);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    private readonly Process process;
    internal ProbeClient Probe { get; }
    internal string Exe { get; }
    internal int Pid => process.Id;
    internal bool IsAlive => !process.HasExited;

    private EscapeWorkerGame(Process process, ProbeClient probe, string exe)
    {
        this.process = process;
        Probe = probe;
        Exe = exe;
    }

    internal static async Task<EscapeWorkerGame> StartAsync(string sourceExe, string root)
    {
        var workerExe = await Task.Run(() => Prepare(sourceExe, root));
        Process? process = null;
        ProbeClient? probe = null;
        try
        {
            process = GameDiscovery.Launch(workerExe);
            GameInstance? game = null;
            for (var i = 0; i < 40 && !process.HasExited; i++)
            {
                await Task.Delay(500);
                game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == process.Id);
                if (game is not null) break;
                if (GameDiscovery.HasDirectXError(process.Id)) break;
            }
            if (game is null)
                throw new InvalidOperationException("并行计算游戏没有创建可连接的窗口。");
            await Task.Delay(500);
            probe = await ProbeClient.AttachAsync(game, root);
            ShowWindow(game.Window, 0);
            return new EscapeWorkerGame(process, probe, workerExe);
        }
        catch
        {
            probe?.Dispose();
            if (process is not null)
            {
                try { await GameDiscovery.CloseVerifiedGameAsync(process.Id, workerExe); }
                finally { process.Dispose(); }
            }
            throw;
        }
    }

    private static string Prepare(string sourceExe, string root)
    {
        sourceExe = Path.GetFullPath(sourceExe);
        GameDiscovery.VerifyGameFile(sourceExe);
        var source = Path.GetDirectoryName(sourceExe)!;
        var worker = Path.GetFullPath(Path.Combine(root, "escape_worker_game"));
        if (!worker.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("并行计算目录必须在程序工作目录内。");
        Directory.CreateDirectory(worker);
        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
        static void LinkArchive(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                if (new FileInfo(to).Length == new FileInfo(from).Length) return;
                File.Delete(to);
            }
            if (!CreateHardLinkW(to, from, 0))
                throw new IOException($"无法为并行计算准备游戏素材：{Path.GetFileName(from)}。" +
                    $" Win32 错误 {Marshal.GetLastWin32Error()}。");
        }
        foreach (var name in new[] { "th123a.dat", "th123b.dat", "th123c.dat" })
            LinkArchive(Path.Combine(source, name), Path.Combine(worker, name));
        foreach (var name in new[] { "th105a.dat", "th105b.dat", "th105c.dat" })
            LinkArchive(Path.Combine(source, "th105", name),
                Path.Combine(worker, "th105", name));
        foreach (var name in new[] { "th123.exe", "d3d9.dll", "d3dx9_33.dll",
            "d3dx9_43.dll", "config123.dat", "configex123.ini", "mod.json" })
            Copy(Path.Combine(source, name), Path.Combine(worker, name));
        Copy(Path.Combine(source, "th105", "config.dat"),
            Path.Combine(worker, "th105", "config.dat"));
        var names = new[] { "ChainCFix", "FixMeiling236", "MemoryPatch",
            "PracticeEX", "SokuDirectXOptimizations", "SokuReplays", "giuroll" };
        foreach (var name in names)
        {
            var directory = Path.Combine(source, "modules", name);
            foreach (var file in Directory.EnumerateFiles(directory, "*",
                SearchOption.AllDirectories))
                Copy(file, Path.Combine(worker, "modules", name,
                    Path.GetRelativePath(directory, file)));
        }
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(source,
            "ModLoaderSettings.json")))?.AsObject() ??
            throw new InvalidDataException("游戏模组配置不可读。");
        var modules = config["modules"]?.AsObject() ??
            throw new InvalidDataException("游戏模组列表不可读。");
        foreach (var (name, value) in modules)
            if (value is JsonObject entry)
                entry["enabled"] = names.Any(module => name.StartsWith(
                    $"modules\\{module}\\", StringComparison.OrdinalIgnoreCase)) &&
                    (bool?)entry["enabled"] == true;
        File.WriteAllText(Path.Combine(worker, "ModLoaderSettings.json"),
            config.ToJsonString());
        var exe = Path.Combine(worker, "th123.exe");
        GameDiscovery.VerifyGameFile(exe);
        return exe;
    }

    public async ValueTask DisposeAsync()
    {
        Probe.Dispose();
        try { await GameDiscovery.CloseVerifiedGameAsync(process.Id, Exe); }
        finally { process.Dispose(); }
    }
}
