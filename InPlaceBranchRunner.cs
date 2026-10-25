using System.IO;

namespace SokuReplayLab;

// Reuses one battle scene. A branch trace is produced by the engine, while a
// generated REP is retained only so the user can open that result for review.
internal sealed class InPlaceBranchRunner
{
    private readonly ProbeClient probe;
    private ReplaySession baseline;
    private readonly string root;
    private int? savedAnchor;
    private readonly SortedSet<int> savedAnchors = [0];

    private InPlaceBranchRunner(ProbeClient probe, ReplaySession baseline, string root)
    {
        this.probe = probe;
        this.baseline = baseline;
        this.root = root;
    }

    internal static async Task<InPlaceBranchRunner> StartAsync(ProbeClient probe,
        ReplaySession baseline, string root)
    {
        if (baseline.Header is null) throw new InvalidOperationException("原 REP 不存在。");
        var trace = Path.Combine(root, $"inplace_setup_{probe.Pid}.csv");
        await probe.SendAsync(ProbeCommand.Load, replayPath: baseline.Source,
            tracePath: trace);
        var start = await probe.SendAsync(ProbeCommand.Seek, target: 0);
        if (start.Ordinal != 0 || start.Status != 3)
            throw new IOException("工作进程未到达原回放起点。");
        await probe.SendAsync(ProbeCommand.SaveSnapshot, target: 0);
        return new InPlaceBranchRunner(probe, baseline, root);
    }

    internal async Task<ReplaySession> CaptureBaselineAsync(int requiredFrame)
    {
        await probe.SendAsync(ProbeCommand.Seek, target: requiredFrame);
        var trace = Path.Combine(root, $"inplace_setup_{probe.Pid}.csv");
        baseline = await Task.Run(() => SessionLoader.LoadSimulationTrace(
            baseline.Source, baseline.Header!, trace, detectGaps: true));
        return baseline;
    }

    internal async Task<ProbeSnapshot> RestoreOriginalAsync(int cursor)
    {
        var trace = Path.Combine(root, "trace_" + baseline.Header!.Hash + ".csv");
        await probe.SendAsync(ProbeCommand.RestoreState, target: 0, tracePath: trace);
        savedAnchors.Clear();
        savedAnchors.Add(0);
        savedAnchor = null;
        var restored = await probe.SendAsync(ProbeCommand.Seek, target: cursor);
        if (restored.Status != 3 || restored.Ordinal != cursor)
            throw new IOException("快速推演后未恢复原回放位置。");
        var actual = await Task.Run(() => TraceReader.Read(trace));
        if (actual.Count <= cursor || cursor < baseline.Frames.Count &&
            !ReplayBranchRunner.SameState(baseline.Frames[cursor], actual[cursor]))
            throw new InvalidDataException("快速返回原回放的状态校验失败。");
        return restored;
    }

    internal async Task<ReplayBranchResult> RunAsync(int anchor, int side,
        IReadOnlyList<ushort> masks, bool permitInactiveTail = false)
    {
        if (baseline.Header is null || side is not (0 or 1) ||
            anchor < 32 || anchor + masks.Count >= baseline.Frames.Count ||
            masks.Count is < 1 or > 250)
            throw new ArgumentException("快照分支范围无效。");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (savedAnchor != anchor)
        {
            if (!savedAnchors.Contains(anchor))
            {
                // Keep up to the native engine's 16 slots. Seek from the nearest
                // verified original snapshot rather than replaying from frame 0.
                var reset = savedAnchors.Count >= 16;
                var from = reset ? 0 : savedAnchors.GetViewBetween(0, anchor).Max;
                await probe.SendAsync(ProbeCommand.RestoreState, target: from);
                if (from == 0) { savedAnchors.Clear(); savedAnchors.Add(0); }
                var at = await probe.SendAsync(ProbeCommand.Seek, target: anchor);
                if (at.Status != 3 || at.Ordinal != anchor)
                    throw new IOException("工作进程未到达分支接管帧。");
                await probe.SendAsync(ProbeCommand.SaveSnapshot, target: anchor);
                savedAnchors.Add(anchor);
            }
            savedAnchor = anchor;
        }
        var prefix = $"inplace_{probe.Pid}_{anchor}_{side}_{Guid.NewGuid():N}";
        var branchPath = Path.Combine(root, prefix + ".rep");
        var tracePath = Path.Combine(root, prefix + ".csv");
        var (_, inputIndex) = ReplayInputAligner.WriteBranchReplay(
            baseline.Source, baseline.Frames, anchor, side, masks, branchPath);
        var header = ReplayHeader.Read(branchPath);
        var prepareMs = timer.ElapsedMilliseconds;
        ProbeSnapshot stopped;
        try
        {
            stopped = await probe.SendAsync(ProbeCommand.Branch, target: anchor,
                tracePath: tracePath, branchSide: side, branchInputIndex: inputIndex,
                branchMasks: masks, timeout: TimeSpan.FromSeconds(30));
        }
        catch (IOException) when (permitInactiveTail)
        {
            var terminal = probe.Read();
            if (terminal.Status != -5 || terminal.MatchState is not (3 or 5) ||
                terminal.Sequence != terminal.Acknowledged) throw;
            stopped = await probe.SendAsync(ProbeCommand.Pause);
        }
        var seekMs = timer.ElapsedMilliseconds - prepareMs;
        if (stopped.Status != 3 || stopped.Ordinal > anchor + masks.Count || stopped.Ordinal <= anchor ||
            !permitInactiveTail && stopped.Ordinal != anchor + masks.Count)
            throw new IOException("快照分支没有停在目标帧。");
        var session = SessionLoader.LoadSimulationTrace(branchPath, header, tracePath,
            baselinePrefix: baseline.Frames, firstChangedFrame: anchor,
            tailOnly: true);
        var parseMs = timer.ElapsedMilliseconds - prepareMs - seekMs;
        if (session.Frames.Count != stopped.Ordinal + 1)
            throw new InvalidDataException("快照分支轨迹帧数不完整。");
        if (!ReplayBranchRunner.SameState(baseline.Frames[anchor], session.Frames[anchor]))
            throw new InvalidDataException("快照分支起点与实机基线不一致。");
        var matched = 0;
        var inactiveTail = false;
        for (var i = 0; i < stopped.Ordinal - anchor; i++)
        {
            var frame = session.Frames[anchor + i + 1];
            inactiveTail |= frame.MatchState != 2 || frame.Round != session.Frames[anchor].Round;
            if (frame.Side(side).Input == masks[i]) matched++;
            else if (!permitInactiveTail || !inactiveTail)
                throw new InvalidDataException($"快照输入在对战帧 {frame.Ordinal} 与预期不一致。");
        }
        if (!permitInactiveTail && matched != masks.Count)
            throw new InvalidDataException(
                $"快照输入接管未通过逐帧验证：{matched}/{masks.Count}；不能用于逃压判定。");
        return new ReplayBranchResult(session, branchPath, anchor, stopped.Ordinal,
            side, inputIndex, matched) {
            PrepareMs = prepareMs, SeekMs = seekMs, ParseMs = parseMs, Engine = "帧状态分支"
        };
    }
}
