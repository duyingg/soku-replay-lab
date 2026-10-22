using System.IO;
using System.Text.Json;

namespace SokuReplayLab;

public static class EscapeLiveCheck
{
    public static async Task RunAsync(string replay, string output)
    {
        replay = Path.GetFullPath(replay);
        output = Path.GetFullPath(output);
        var root = Directory.GetCurrentDirectory();
        var header = ReplayHeader.Read(replay);
        var trace = Path.Combine(root, "trace_" + header.Hash + ".csv");
        ProbeClient? probe = null;
        PressureGap? chosen = null;
        EscapeVerdict? verdict = null;
        ReplayBranchResult? branch = null;
        string? error = null;
        var steps = new List<object>();
        try
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            probe = await ProbeClient.AttachAsync(game, root);
            await probe.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            await probe.SendAsync(ProbeCommand.Seek, target: 520);
            var scouting = ReplayAnalyzer.Analyze(replay, header, TraceReader.Read(trace),
                "当前实机测试轨迹");
            chosen = scouting.PressureGaps.FirstOrDefault(x => x.Start >= 100 &&
                x.Start < 400 && x.Length >= 8 && x.Start + 90 < scouting.Frames.Count)
                ?? throw new InvalidDataException("测试 REP 前 400 帧没有合适的防御空隙。");
            steps.Add(new { name = "chosen", chosen });
            var masks = EscapeTrial.Plan(scouting.Frames, chosen, "4A");
            var anchor = EscapeTrial.Anchor(chosen, "4A");
            await probe.SendAsync(ProbeCommand.Seek, target: anchor);
            // Keep the already captured future for judging, while the current trace is at the anchor.
            branch = await ReplayBranchRunner.RunAsync(probe, scouting, root,
                anchor, chosen.Defender, masks);
            verdict = EscapeTrial.Judge(scouting.Frames, branch.Session.Frames,
                anchor, chosen.Defender, "4A", masks.Length);
            steps.Add(new { name = "judged", branch.End, branch.MatchedInputFrames, verdict });
            await ReplayBranchRunner.RestoreAsync(probe, scouting, root, anchor);
            steps.Add(new { name = "restored", state = probe.Read() });
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (probe is not null)
            {
                try { steps.Add(new { name = "released", state = await probe.ReleaseAsync() }); }
                catch (Exception ex) { steps.Add(new { name = "release-error", error = ex.ToString() }); }
                probe.Dispose();
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                replay, chosen, verdict, branch = branch is null ? null : new {
                    branch.Anchor, branch.End, branch.Side,
                    branch.InputIndex, branch.MatchedInputFrames, branch.ReplayPath
                }, steps, error
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) throw new InvalidOperationException(error);
    }
}
