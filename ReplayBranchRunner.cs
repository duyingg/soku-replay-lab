using System.IO;

namespace SokuReplayLab;

public sealed record ReplayBranchResult(ReplaySession Session, string ReplayPath,
    int Anchor, int End, int Side, int InputIndex, int MatchedInputFrames)
{
    public long PrepareMs { get; init; }
    public long LoadMs { get; init; }
    public long SeekMs { get; init; }
    public long ParseMs { get; init; }
    public string Engine { get; init; } = "REP重载";
}

public static class ReplayBranchRunner
{
    public static async Task<ReplayBranchResult> RunAsync(ProbeClient probe,
        ReplaySession original, string root, int anchor, int side,
        IReadOnlyList<ushort> masks, bool analyzeForDisplay = true,
        bool requirePausedAtAnchor = true, bool simulationTrace = false)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (original.Header is null || original.Frames.Count <= anchor ||
            side is not (0 or 1) || masks.Count is < 1 or > 512 ||
            masks.All(mask => mask == 0))
            throw new InvalidOperationException("需要含完整轨迹的原 REP、有效接管帧和非空输入。");
        var status = probe.Read();
        if (status.Status < 0 || requirePausedAtAnchor &&
            (status.Status != 3 || status.Ordinal != anchor))
            throw new InvalidOperationException(requirePausedAtAnchor
                ? "游戏必须暂停在当前接管帧。" : "游戏探针处于错误状态，无法计算分支。");
        var originalTrace = Path.Combine(root, "trace_" + original.Header.Hash + ".csv");
        if ((analyzeForDisplay || requirePausedAtAnchor) && !File.Exists(originalTrace))
            throw new FileNotFoundException("接管前必须由游戏采集当前 REP 的逐帧轨迹。", originalTrace);
        // The optimized caller has already loaded and validated this game run.
        // Reparse the trace for the default interactive path, which may have
        // been handed a session backed by an older cached capture.
        var branchPath = Path.Combine(root,
            $"branch_{original.Header.Hash[..12]}_{anchor}_{side}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.rep");
        var (inputIndex, branchHeader) = await Task.Run(() =>
        {
            var observed = !analyzeForDisplay && !requirePausedAtAnchor
                ? original.Frames : TraceReader.Read(originalTrace);
            if (observed.Count <= anchor)
                throw new InvalidDataException("接管帧尚未写入游戏轨迹。");
            var (_, index) = ReplayInputAligner.WriteBranchReplay(original.Source,
                observed, anchor, side, masks, branchPath);
            return (index, ReplayHeader.Read(branchPath));
        });
        var prepareMs = timer.ElapsedMilliseconds;
        var branchTrace = Path.Combine(root, "trace_" + branchHeader.Hash + ".csv");
        try
        {
            await probe.SendAsync(ProbeCommand.Load,
                target: simulationTrace ? anchor + 2 : -1,
                replayPath: branchPath,
                tracePath: branchTrace);
            var loadMs = timer.ElapsedMilliseconds - prepareMs;
            var stopped = await probe.SendAsync(ProbeCommand.Seek,
                target: anchor + masks.Count);
            var seekMs = timer.ElapsedMilliseconds - prepareMs - loadMs;
            if (stopped.Status != 3 || stopped.Ordinal != anchor + masks.Count)
                throw new IOException("游戏分支未停在预定位置。");
            var branch = await Task.Run(() => analyzeForDisplay
                ? SessionLoader.Load(branchPath, root, branchTrace,
                    preloadedHeader: branchHeader)
                : SessionLoader.LoadSimulationTrace(branchPath, branchHeader, branchTrace,
                    baselinePrefix: original.Frames, firstChangedFrame: anchor,
                    tailOnly: simulationTrace));
            var parseMs = timer.ElapsedMilliseconds - prepareMs - loadMs - seekMs;
            if (branch.Frames.Count <= anchor + masks.Count)
                throw new InvalidDataException("游戏分支的轨迹不完整。");
            if (!SameState(original.Frames[anchor], branch.Frames[anchor]))
                throw new InvalidDataException(
                    $"分支起点第 {anchor} 帧与本次实机基线不同，不能判定本次输入的效果。");
            var matched = 0;
            for (var i = 0; i < masks.Count; i++)
                if (branch.Frames[anchor + 1 + i].Side(side).Input == masks[i]) matched++;
            return new(branch, branchPath, anchor, stopped.Ordinal, side,
                inputIndex, matched) {
                PrepareMs = prepareMs, LoadMs = loadMs,
                SeekMs = seekMs, ParseMs = parseMs
            };
        }
        catch (Exception primary)
        {
            try
            {
                await RestoreAsync(probe, original, root, anchor);
            }
            catch (Exception recovery)
            {
                throw new AggregateException("分支计算失败，返回原回放也失败。",
                    primary, recovery);
            }
            throw;
        }
    }

    internal static bool SameState(Frame baseline, Frame branch) => ReplayState.Matches(baseline, branch);

    public static async Task<ProbeSnapshot> RestoreAsync(ProbeClient probe,
        ReplaySession original, string root, int anchor)
    {
        if (original.Header is null) throw new InvalidOperationException("原 REP 不存在。");
        var trace = Path.Combine(root, "trace_" + original.Header.Hash + ".csv");
        await probe.SendAsync(ProbeCommand.Load, replayPath: original.Source,
            tracePath: trace);
        var result = await probe.SendAsync(ProbeCommand.Seek, target: anchor);
        if (result.Status != 3 || result.Ordinal != anchor)
            throw new IOException("原回放没有恢复到接管起点。");
        return result;
    }
}
