using System.IO;
using System.Text.Json;

namespace SokuReplayLab;

public static class BranchLiveCheck
{
    public static async Task RunAsync(string replay, string report, string trace)
    {
        replay = Path.GetFullPath(replay);
        report = Path.GetFullPath(report);
        trace = Path.GetFullPath(trace);
        var requestedTrace = trace;
        trace = Path.Combine(Directory.GetCurrentDirectory(),
            "trace_" + ReplayHeader.Read(replay).Hash + ".csv");
        string? capture = null, branchReplay = null;
        var steps = new List<object>();
        string? error = null;
        ProbeClient? client = null;
        int anchor = -1, observed = 0;
        try
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证的游戏窗口。");
            client = await ProbeClient.AttachAsync(game, Directory.GetCurrentDirectory());
            steps.Add(new { name = "attached", state = client.Read() });
            await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(50);
            while (client.Read().Ordinal < 620 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100);
            if (client.Read().Ordinal < 620) throw new TimeoutException("游戏未推进到分支测试帧。");
            var paused = await client.SendAsync(ProbeCommand.Pause);
            anchor = paused.Ordinal;
            steps.Add(new { name = "paused", state = paused });
            var original = SessionLoader.Load(replay, Directory.GetCurrentDirectory());
            var plan = Enumerable.Repeat((ushort)0x10, 24).ToArray();
            var result = await ReplayBranchRunner.RunAsync(client, original,
                Directory.GetCurrentDirectory(), anchor, 0, plan);
            branchReplay = result.ReplayPath;
            capture = Path.Combine(Directory.GetCurrentDirectory(),
                "trace_" + result.Session.Header!.Hash + ".csv");
            steps.Add(new { name = "branch-finish", state = client.Read(), result.InputIndex });
            observed = result.MatchedInputFrames;
            if (observed < 12)
                throw new InvalidDataException($"分支输入仅在 {observed} 帧被观察到。");
            steps.Add(new { name = "observed", frames = observed });
            var restored = await ReplayBranchRunner.RestoreAsync(client, original,
                Directory.GetCurrentDirectory(), anchor);
            steps.Add(new { name = "original-restored", state = restored });
            if (restored.Ordinal != anchor || restored.Status != 3)
                throw new InvalidDataException("返回原回放的帧号不一致。");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (client is not null)
            {
                try { steps.Add(new { name = "released", state = await client.ReleaseAsync() }); }
                catch (Exception ex) { steps.Add(new { name = "release-error", error = ex.ToString() }); }
                client.Dispose();
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new {
                replay, requestedTrace, trace, capture, branchReplay,
                anchor, observed, steps, error
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) throw new InvalidOperationException(error);
    }
}
