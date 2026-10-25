using System.IO;

namespace SokuReplayLab;

public sealed class PressureEscapeRunner(ReplaySession original, IEscapeBranchEngine engine)
{
    private readonly Dictionary<int, EscapeBranch> controls = [];
    private Pressure? controlPressure;
    private bool started;

    public async Task StartAsync(int requiredFrame)
    {
        started = false;
        controls.Clear();
        controlPressure = null;
        await engine.StartAsync(requiredFrame);
        started = true;
    }

    public Task RestoreAsync(int cursor) => engine.RestoreAsync(cursor);

    public async Task<EscapeTrialResult> RunAsync(PressureEscapeJob job, Func<bool> cancelled)
    {
        if (controlPressure != job.Pressure)
        { controls.Clear(); controlPressure = job.Pressure; }
        var plans = PressureEscapeSimulation.Plans(original, job);
        if (plans.Count == 0)
            return Result(null, new("不可执行", "没有可验证的输入时机：可能缺少朝向、手牌、回合前后轨迹或招式不适用"), 0);
        if (!started) throw new InvalidOperationException("先调用 StartAsync 初始化推演引擎。");
        EscapeBranch? best = null;
        EscapeVerdict? evidence = null;
        var tried = 0;
        foreach (var plan in plans)
        {
            if (cancelled()) break;
            var branch = await engine.RunAsync(plan);
            if (branch.Anchor != plan.Anchor || branch.End < plan.Anchor || branch.End >= branch.Frames.Count ||
                branch.Frames.Count <= plan.Anchor)
                throw new InvalidDataException("分支执行器返回了不完整或错位的轨迹。");
            if (!ReplayState.Matches(original.Frames[plan.Anchor], branch.Frames[plan.Anchor]))
                throw new InvalidDataException("实机未复现原压制起点，不能据此给出建议。");
            // Rejected attempts and movement outcomes need no attack attribution.
            // Only a prospective attack gain requires another engine branch.
            var verdict = PressureEscapeSimulation.Evaluate(plan, branch.Frames,
                branch.Frames, branch.MatchedInputFrames);
            if (verdict.Result == "未找到" && HasAttackContact(plan, branch.Frames))
            {
                if (cancelled()) return Result(branch, new("已取消", "试验已结束；停止请求后不再启动新的只防御对照"), tried + 1);
                var key = checked(plan.Anchor * 2 + plan.Side);
                if (!controls.TryGetValue(key, out var control))
                {
                    control = await engine.RunAsync(plan with {
                        Inputs = PressureEscapeSimulation.GuardInputs(original, plan.Anchor, plan.Side, plan.Inputs.Length)
                    });
                    if (ValidControl(plan, control)) controls[key] = control;
                }
                verdict = ValidControl(plan, control)
                    ? PressureEscapeSimulation.Evaluate(plan, branch.Frames, control.Frames, branch.MatchedInputFrames)
                    : new("未判定", "只防御对照的起点、观察范围或输入验证不完整，不能用于确认招式效果");
            }
            tried++;
            if (best is null || Rank(verdict.Result) > Rank(evidence!.Result))
            { best = branch; evidence = verdict; }
            if (verdict.Result == "成功") return Result(branch, verdict, tried);
        }
        return Result(best, new(cancelled() ? "已取消" : evidence?.Result == "未判定" ? "未判定" : "未找到",
            $"已尝试 {tried}/{plans.Count} 个接触／防御结束附近的输入时机，未找到成功方案；" +
            (evidence?.Reason ?? "尚无完成的试验") + "。不代表其他时机不可行"), tried);

        EscapeTrialResult Result(EscapeBranch? branch, EscapeVerdict verdict, int count) =>
            new(branch?.Anchor ?? job.Pressure.Start, job.Pressure.Defender, job.Move, verdict,
                original, branch?.ReplayPath ?? "", branch?.End ?? job.Pressure.Start,
                branch?.MatchedInputFrames ?? 0) {
                PressureStart = job.Pressure.Start, PressureEnd = job.Pressure.End, AttemptCount = count
            };
        static int Rank(string status) => status switch {
            "成功" => 5, "未找到" => 4, "仍受压" => 3, "受击" => 2, "未发动" => 1, _ => 0
        };
    }

    private bool ValidControl(EscapeInputPlan plan, EscapeBranch control)
    {
        var end = plan.Anchor + plan.Inputs.Length;
        return control.Anchor == plan.Anchor && control.End >= end && control.End < control.Frames.Count && control.Frames.Count > end &&
            control.MatchedInputFrames == plan.Inputs.Length &&
            ReplayState.Matches(original.Frames[plan.Anchor], control.Frames[plan.Anchor]);
    }

    private static bool HasAttackContact(EscapeInputPlan plan, IReadOnlyList<Frame> frames)
    {
        for (var i = plan.Anchor + 1; i <= plan.Anchor + plan.Inputs.Length; i++)
        {
            var own = frames[i].Side(plan.Side);
            var enemy = frames[i].Side(1 - plan.Side);
            if (own.Action >= 300 && PressureEscapeSimulation.RequestedAction(plan.Move, own) &&
                (enemy.Hp < frames[i - 1].Side(1 - plan.Side).Hp ||
                    PressureEscapeSimulation.Guard(enemy.Action) && own.Collision == 2)) return true;
        }
        return false;
    }
}
