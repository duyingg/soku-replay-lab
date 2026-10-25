using System.IO;

namespace SokuReplayLab;

/// <summary>Adapts the desktop application's native probe to the standalone module.</summary>
internal sealed class EscapeSimulationGameEngine(ProbeClient probe, ReplaySession original,
    string root) : IEscapeBranchEngine
{
    private InPlaceBranchRunner? runner;

    public bool CanContinue
    {
        get
        {
            try
            {
                var state = probe.Read();
                return state.Status == 3 && state.Sequence == state.Acknowledged;
            }
            catch { return false; }
        }
    }

    public async Task StartAsync(int requiredFrame)
    {
        runner = await InPlaceBranchRunner.StartAsync(probe, original, root);
        var actual = await runner.CaptureBaselineAsync(requiredFrame);
        if (actual.Frames.Count <= requiredFrame)
            throw new InvalidDataException("未采集到推演所需的实机基线。");
    }

    public async Task<EscapeBranch> RunAsync(EscapeInputPlan plan)
    {
        if (runner is null) throw new InvalidOperationException("推演引擎尚未初始化。");
        var result = await runner.RunAsync(plan.Anchor, plan.Side, plan.Inputs, permitInactiveTail: true);
        return new(result.Session.Frames, result.ReplayPath, result.Anchor, result.End, result.MatchedInputFrames);
    }

    public async Task RestoreAsync(int cursor)
    {
        if (runner is null) await ReplayBranchRunner.RestoreAsync(probe, original, root, cursor);
        else await runner.RestoreOriginalAsync(cursor);
    }
}
