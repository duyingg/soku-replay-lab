using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SokuReplayLab;

internal static class FullReplayLiveCheck
{
    internal static async Task FastCaptureAsync(string source, string reportPath)
    {
        var root = Directory.GetCurrentDirectory();
        var game = GameDiscovery.Find().FirstOrDefault()
            ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
        var preflight = ProbePreflight.Inspect(game);
        using var resident = ProbeClient.TryOpen(game.Pid);
        if (!preflight.Passed && resident is null)
            throw new InvalidOperationException("只读预检未通过，未加载探针。");
        var replay = Path.GetFullPath(source);
        var header = ReplayHeader.Read(replay);
        var cached = SessionLoader.Load(replay, root);
        if (cached.Frames.Count < 1000)
            throw new InvalidDataException("需要已有完整轨迹的 REP 进行逐帧对比。");
        var trace = Path.Combine(root, "fastcheck_" + header.Hash + ".csv");
        var timer = Stopwatch.StartNew();
        ProbeClient? probe = null;
        string? error = null;
        object? result = null;
        try
        {
            probe = await ProbeClient.AttachAsync(game, root);
            await probe.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            var started = Stopwatch.StartNew();
            await probe.SendAsync(ProbeCommand.Capture);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            ProbeSnapshot final;
            do
            {
                await Task.Delay(100);
                final = probe.Read();
                if (final.Status < 0)
                    throw new IOException($"快速采集探针状态 {final.Status}。");
            } while (final.Status != 3 && DateTimeOffset.UtcNow < deadline);
            started.Stop();
            if (final.Status != 3)
                throw new TimeoutException("快速采集未在三分钟内到达终局。");
            var captured = TraceReader.Read(trace);
            if (captured.Count < cached.Frames.Count || captured[^1].MatchState != 5)
                throw new InvalidDataException("快速采集缺少已确认的终局轨迹。");
            var mismatches = new List<int>();
            for (var i = 0; i < Math.Min(cached.Frames.Count, captured.Count); i += 120)
                if (cached.Frames[i].P1.Hp != captured[i].P1.Hp ||
                    cached.Frames[i].P2.Hp != captured[i].P2.Hp ||
                    cached.Frames[i].P1.Action != captured[i].P1.Action ||
                    cached.Frames[i].P2.Action != captured[i].P2.Action)
                    mismatches.Add(i);
            result = new { replay, header.InputFrames, cachedFrames = cached.Frames.Count,
                capturedFrames = captured.Count, final.Ordinal, final.MatchState,
                captureSeconds = started.Elapsed.TotalSeconds,
                framesPerSecond = captured.Count / Math.Max(.001, started.Elapsed.TotalSeconds),
                mismatches };
            if (mismatches.Count > 0)
                throw new InvalidDataException("快速采集与既有轨迹的角色状态不一致。");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            object? released = null;
            if (probe is not null)
            {
                try { released = await probe.ReleaseAsync(); }
                catch (Exception ex) { released = ex.ToString(); }
                probe.Dispose();
            }
            timer.Stop();
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new {
                game.Pid, result, error, released,
                totalSeconds = timer.Elapsed.TotalSeconds
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) Environment.ExitCode = 1;
    }

    internal static async Task RunAsync(string first, string second, string reportPath)
    {
        var root = Directory.GetCurrentDirectory();
        var game = GameDiscovery.Find().FirstOrDefault()
            ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
        var preflight = ProbePreflight.Inspect(game);
        using var resident = ProbeClient.TryOpen(game.Pid);
        if (!preflight.Passed && resident is null)
            throw new InvalidOperationException("只读预检未通过，未加载探针。");
        ProbeClient? probe = null;
        var results = new List<object>();
        string? error = null;
        try
        {
            probe = await ProbeClient.AttachAsync(game, root);
            foreach (var source in new[] { first, second })
            {
                var replay = Path.GetFullPath(source);
                var header = ReplayHeader.Read(replay);
                var cached = SessionLoader.Load(replay, root);
                var trace = Path.Combine(root, "fullrun_" + header.Hash + ".csv");
                var watch = Stopwatch.StartNew();
                await probe.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
                var checkpoints = new List<object>();
                var limit = Math.Min(cached.Frames.Count - 100, header.InputFrames - 100);
                if (limit < 1000) throw new InvalidDataException("缺少完整 REP 或原始轨迹。");
                foreach (var target in new[] { limit / 4, limit / 2, limit * 3 / 4, limit })
                {
                    var state = await probe.SendAsync(ProbeCommand.Seek, target: target);
                    if (state.Status != 3 || state.Ordinal != target)
                        throw new IOException($"回放定位未停在第 {target} 帧。");
                    checkpoints.Add(new { target, state.MatchState, state.EngineFrame });
                }
                await probe.SendAsync(ProbeCommand.Capture);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(70);
                ProbeSnapshot final;
                do
                {
                    await Task.Delay(100);
                    final = probe.Read();
                } while (final.Status != 3 && DateTimeOffset.UtcNow < deadline);
                if (final.Status != 3)
                    throw new TimeoutException("整场回放未在 70 秒内到达终局。");
                var captured = TraceReader.Read(trace);
                if (captured.Count < limit || final.Ordinal < limit)
                    throw new InvalidDataException("终局轨迹短于已通过的检查点。");
                var common = Math.Min(captured.Count, cached.Frames.Count);
                var hpDifferences = new List<object>();
                foreach (var ordinal in new[] { limit / 4, limit / 2, limit * 3 / 4, limit })
                {
                    if (ordinal >= common) continue;
                    var actual = captured[ordinal];
                    var expected = cached.Frames[ordinal];
                    if (actual.P1.Hp != expected.P1.Hp || actual.P2.Hp != expected.P2.Hp)
                        hpDifferences.Add(new { ordinal, actualP1 = actual.P1.Hp,
                            actualP2 = actual.P2.Hp, expectedP1 = expected.P1.Hp,
                            expectedP2 = expected.P2.Hp });
                }
                watch.Stop();
                results.Add(new { replay, header.InputFrames,
                    cachedFrames = cached.Frames.Count, capturedFrames = captured.Count,
                    final.Ordinal, final.MatchState, checkpoints, hpDifferences,
                    elapsedSeconds = watch.Elapsed.TotalSeconds });
                if (hpDifferences.Count > 0)
                    throw new InvalidDataException("整场回放与既有轨迹的检查点 HP 不一致。");
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (probe is not null)
            {
                try { results.Add(new { released = await probe.ReleaseAsync() }); }
                catch (Exception ex) { results.Add(new { releaseError = ex.ToString() }); }
                probe.Dispose();
            }
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new {
                game.Pid, results, error
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) Environment.ExitCode = 1;
    }
}
