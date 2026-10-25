using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SokuReplayLab;

// Diagnostic sessions launch the configured external installation directly.
internal sealed class EscapeWorkerGame : IAsyncDisposable
{
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
        var workerExe = Path.GetFullPath(sourceExe);
        GameDiscovery.VerifyGameFile(workerExe);
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

    public async ValueTask DisposeAsync()
    {
        Probe.Dispose();
        try { await GameDiscovery.CloseVerifiedGameAsync(process.Id, Exe); }
        finally { process.Dispose(); }
    }
}
