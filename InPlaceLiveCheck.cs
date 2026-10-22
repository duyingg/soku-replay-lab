using System.IO;
using System.Text.Json;

namespace SokuReplayLab;

internal static class InPlaceLiveCheck
{
    internal static async Task RunAsync(string exe, string replay, string baselineTrace,
        string output)
    {
        var log = new List<object>();
        Exception? failure = null;
        EscapeWorkerGame? worker = null;
        try
        {
            var root = Directory.GetCurrentDirectory();
            var header = ReplayHeader.Read(replay);
            var baseline = SessionLoader.Load(replay, root, baselineTrace, header);
            var gaps = EscapeTrial.EligibleGaps(baseline, -1, "4A", 1)
                .Where(x => x.Start >= 100 &&
                    x.Start + 90 < baseline.Frames.Count).Take(4).ToArray();
            if (gaps.Length == 0) throw new InvalidDataException("测试回放没有可核验的防御空隙。");
            worker = await EscapeWorkerGame.StartAsync(exe, root);
            log.Add(new { phase = "worker", worker.Pid });
            var runner = await InPlaceBranchRunner.StartAsync(worker.Probe, baseline, root);
            var branches = new List<(PressureGap Gap, string Move, ReplayBranchResult Result)>();
            foreach (var gap in gaps)
            foreach (var move in new[] { "4A", "2A", "8", "1DD" })
            {
                var anchor = EscapeTrial.Anchor(gap, move);
                var masks = EscapeTrial.Plan(baseline.Frames, gap, move);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await runner.RunAsync(anchor, gap.Defender, masks);
                var mismatches = Enumerable.Range(0, masks.Length).Where(i =>
                    result.Session.Frames[anchor + i + 1].Side(gap.Defender).Input != masks[i])
                    .Select(i => new { i, expected = masks[i], actual =
                        result.Session.Frames[anchor + i + 1].Side(gap.Defender).Input })
                    .ToArray();
                branches.Add((gap, move, result));
                log.Add(new { phase = "inplace", anchor, move, result.MatchedInputFrames,
                    expected = masks.Length, seconds = stopwatch.Elapsed.TotalSeconds,
                    mismatches, replay = result.ReplayPath,
                    verdict = EscapeTrial.Judge(baseline.Frames,
                        result.Session.Frames, anchor, gap.Defender, move, masks.Length) });
            }
            foreach (var (gap, move, result) in branches)
            {
                var masks = EscapeTrial.Plan(baseline.Frames, gap, move);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var legacy = await ReplayBranchRunner.RunAsync(worker.Probe, baseline, root,
                    result.Anchor, gap.Defender, masks, analyzeForDisplay: false,
                    requirePausedAtAnchor: false, simulationTrace: true);
                var different = Enumerable.Range(result.Anchor, masks.Length + 1).Where(index =>
                    !ReplayBranchRunner.SameState(result.Session.Frames[index], legacy.Session.Frames[index])).ToArray();
                log.Add(new { phase = "comparison", result.Anchor, move, legacy.MatchedInputFrames,
                    seconds = timer.Elapsed.TotalSeconds, different, replay = legacy.ReplayPath });
                if (different.Length != 0 || legacy.MatchedInputFrames != masks.Length)
                    throw new InvalidDataException($"{move} 第 {result.Anchor} 帧的两种实机路径不一致。");
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            File.WriteAllText(output, JsonSerializer.Serialize(new { log,
                error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure is not null) Environment.ExitCode = 1;
    }
}
