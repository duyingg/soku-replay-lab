using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SokuReplayLab;

/// <summary>Short end-to-end batch check using the same engine branch steps as the UI.</summary>
internal static class EscapeBatchLiveCheck
{
    internal static async Task RunAsync(string exe, string replay, string output)
    {
        if (exe != "-") exe = Path.GetFullPath(exe);
        replay = Path.GetFullPath(replay);
        output = Path.GetFullPath(output);
        var root = Directory.GetCurrentDirectory();
        var steps = new List<object>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure = null;
        System.Diagnostics.Process? gameProcess = null;
        GameInstance? game = null;
        ProbeClient? probe = null;
        try
        {
            if (exe == "-") game = GameDiscovery.Find().FirstOrDefault();
            else
            {
                gameProcess = GameDiscovery.Launch(exe);
                for (var i = 0; i < 40 && !gameProcess.HasExited; i++)
                {
                    await Task.Delay(500);
                    game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == gameProcess.Id);
                    if (game is not null) break;
                }
                if (game is not null)
                {
                    await Task.Delay(1200);
                    game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == gameProcess.Id &&
                        x.Window == game.Window);
                }
            }
            if (game is null) throw new InvalidOperationException("未找到稳定且已验证的游戏主窗口。");
            steps.Add(new { name = "game", game.Pid, Window = game.Window.ToInt64() });
            var preflight = ProbePreflight.Inspect(game);
            steps.Add(new { name = "preflight", preflight });
            using var resident = ProbeClient.TryOpen(game.Pid);
            if (!preflight.Passed && resident is null)
                throw new InvalidOperationException("只读预检未通过，未加载探针。");
            probe = await ProbeClient.AttachAsync(game, root);
            var header = ReplayHeader.Read(replay);
            var trace = Path.Combine(root, "trace_" + header.Hash + ".csv");
            await probe.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            await probe.SendAsync(ProbeCommand.Seek, target: 1100);
            byte[] TraceDigest()
            {
                using var stream = new FileStream(trace, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite);
                return SHA256.HashData(stream);
            }
            var originalDigest = TraceDigest();
            var rewindStarted = elapsed.Elapsed;
            var rewind = await probe.SendAsync(ProbeCommand.Seek, target: 650);
            steps.Add(new { name = "automatic-snapshot-rewind", rewind.Ordinal,
                elapsedSeconds = (elapsed.Elapsed - rewindStarted).TotalSeconds });
            if (rewind.Ordinal != 650)
                throw new InvalidDataException("自动快照未恢复到预定帧。");
            await probe.SendAsync(ProbeCommand.Seek, target: 1100);
            if (!originalDigest.SequenceEqual(TraceDigest()))
                throw new InvalidDataException("快照回退后重算的轨迹与原轨迹不同。");
            var original = ReplayAnalyzer.Analyze(replay, header, TraceReader.Read(trace),
                "当前实机测试轨迹");
            File.Copy(trace, output + ".baseline.csv", true);
            steps.Add(new { name = "baseline", elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                frames = original.Frames.Count });
            var gaps = EscapeTrial.EligibleGaps(original, -1, "4A", 1).Where(x =>
                x.Start >= 100 && x.Start < 1000 && x.Start + 90 < original.Frames.Count)
                .Take(2).ToArray();
            if (gaps.Length < 2) throw new InvalidDataException("测试 REP 可用空隙不足两处。");
            foreach (var gap in gaps)
            {
                var trialStarted = elapsed.Elapsed;
                var anchor = EscapeTrial.Anchor(gap, "4A");
                var masks = EscapeTrial.Plan(original.Frames, gap, "4A");
                var branch = await ReplayBranchRunner.RunAsync(probe, original, root,
                    anchor, gap.Defender, masks, analyzeForDisplay: false,
                    requirePausedAtAnchor: false, simulationTrace: true);
                var verdict = branch.MatchedInputFrames == masks.Length
                    ? EscapeTrial.Judge(original.Frames, branch.Session.Frames,
                        anchor, gap.Defender, "4A", masks.Length)
                    : new EscapeVerdict("未判定", "分支输入未逐帧匹配");
                steps.Add(new { name = "trial", gap.Start, gap.Defender, anchor,
                    branch.End, branch.MatchedInputFrames, expected = masks.Length,
                    verdict, replay = branch.ReplayPath, branch.PrepareMs,
                    branch.LoadMs, branch.SeekMs, branch.ParseMs,
                    elapsedSeconds = (elapsed.Elapsed - trialStarted).TotalSeconds });
            }
            var final = await ReplayBranchRunner.RestoreAsync(probe, original, root, 0);
            steps.Add(new { name = "final-restore", final.Ordinal, final.MatchState });
        }
        catch (Exception ex)
        {
            failure = ex;
            if (probe is not null)
                try { steps.Add(new { name = "probe-on-failure", state = probe.Read() }); }
                catch (Exception) { }
            if (game is not null)
                steps.Add(new { name = "failed-windows", windows = GameDiscovery.InspectWindows()
                    .Where(x => x.Pid == game.Pid).ToArray() });
        }
        finally
        {
            if (probe is not null)
            {
                try { steps.Add(new { name = "release", state = await probe.ReleaseAsync() }); }
                catch (Exception ex) { steps.Add(new { name = "release-error", error = ex.ToString() }); }
                probe.Dispose();
            }
            if (gameProcess is not null)
            {
                try
                {
                    if (!gameProcess.HasExited) gameProcess.Kill();
                    gameProcess.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                gameProcess.Dispose();
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { steps,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure is not null) Environment.ExitCode = 1;
    }
}
