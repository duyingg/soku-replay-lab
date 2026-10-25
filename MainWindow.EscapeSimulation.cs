using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private Task? pressureEscapeTask;
    private CancellationTokenSource? pressureEscapeCancellation;
    private void StoreEscapeResult(EscapeTrialResult result)
    {
        escapeResults.RemoveAll(previous => previous.Original.Header?.Hash == result.Original.Header?.Hash &&
            previous.PressureStart == result.PressureStart && previous.Defender == result.Defender && previous.Move == result.Move);
        escapeResults.Add(result);
        selectedEscapeResult = result;
    }

    private void EscapePage()
    {
        var source = branchOriginal ?? session;
        if (source?.Header is null || source.Frames.Count == 0)
        { Empty("先导入 REP 并完成实机轨迹采集，再推演压制中的逃压方案。"); return; }
        var pressures = PressureSelection.Select(source, minPressureMoves);
        var moves = new[] { source.Header.Character1, source.Header.Character2 }
            .SelectMany(PressureEscapeSimulation.Moves).Distinct().ToArray();
        var choices = new UniformGrid { Columns = 6 };
        foreach (var move in moves)
        {
            var selected = move;
            var toggle = Button(move, () => {
                if (escapeBusy) return;
                if (!enabledEscapeMoves.Add(selected)) enabledEscapeMoves.Remove(selected);
                SaveSettings(); Render();
            }, enabledEscapeMoves.Contains(move));
            toggle.IsEnabled = !escapeBusy;
            choices.Children.Add(toggle);
        }
        var selectedMoves = moves.Where(enabledEscapeMoves.Contains).ToArray();
        var jobs = PressureEscapeSimulation.Jobs(source, minPressureMoves, selectedMoves);
        var pending = jobs.Where(job => !escapeResults.Any(result =>
            result.Original.Header?.Hash == source.Header.Hash &&
            result.PressureStart == job.Pressure.Start && result.Defender == job.Pressure.Defender &&
            result.Move == job.Move && result.Verdict.Result is not ("已取消" or "执行错误") &&
            (result.ReplayPath.Length == 0 || File.Exists(result.ReplayPath)))).ToArray();
        var actions = new WrapPanel();
        var run = Button($"对全部 {pressures.Count} 段压制尝试 · 待计算 {pending.Length} 项",
            async () => await RunPressureEscapeAsync(pending), true);
        run.IsEnabled = !escapeBusy && pending.Length > 0 && branchOriginal is null;
        actions.Children.Add(run);
        if (branchOriginal is not null) actions.Children.Add(Button("返回原回放", ReturnOriginalBranch));
        if (escapeBusy) actions.Children.Add(Button("停止推演", () => {
            pressureEscapeCancellation?.Cancel();
            if (escapeProgressLabel is not null) escapeProgressLabel.Text = "正在结束当前分支并恢复原回放…";
        }));
        escapeProgressLabel = Text(escapeBusy ? $"已完成 {escapeBatchDone}/{escapeBatchTotal} 项" :
            $"使用压制总表的“至少 {minPressureMoves} 招”设置，共 {pressures.Count} 段；已选 {selectedMoves.Length} 种招式。", 12, Muted);
        content.Children.Add(Card("整场逃压推演", Vertical(escapeProgressLabel, choices, actions,
            Text("每段压制逐一验证接触及防御结束附近的输入时机。成功建议需确认招式发动、实际效果和后续安全；未找到不代表其他时机不可行。", 12, Muted))));

        var scope = pressures.Select(p => (p.Start, p.Defender)).ToHashSet();
        var results = escapeResults.Where(r => r.Original.Header?.Hash == source.Header.Hash &&
            r.PressureStart >= 0 && scope.Contains((r.PressureStart, r.Defender)))
            .OrderBy(r => r.PressureStart).ThenBy(r => r.Defender).ThenBy(r => r.Move).ToArray();
        if (!results.Contains(selectedEscapeResult)) selectedEscapeResult = results.FirstOrDefault(r => File.Exists(r.ReplayPath));
        var table = Table(["压制开始", "防守方", "尝试招式", "结果", "测试时机数", "依据"],
            results.Select(r => new[] { Time(r.PressureStart), $"{r.Defender + 1}P", r.Move,
                r.Verdict.Result, r.AttemptCount.ToString(), r.Verdict.Reason }));
        table.SelectionChanged += (_, _) => {
            var index = table.SelectedIndex;
            if (index >= 0 && index < results.Length) selectedEscapeResult = results[index];
        };
        BindTimeline(table, new Dictionary<string, IReadOnlyList<int>> {
            ["压制开始"] = results.Select(r => r.Anchor).ToArray()
        }, Jump, (index, ordinal) => _ = OpenEscapeResultAsync(results[index]));
        var playback = Button("播放所选技能分支", async () => {
            if (selectedEscapeResult is { } result) await PlayEscapeResultAsync(result);
        });
        playback.IsEnabled = !escapeBusy && selectedEscapeResult is { } chosen && File.Exists(chosen.ReplayPath);
        content.Children.Add(Card($"推演结果 · {results.Length} 项", Vertical(playback, table)));

        var overview = Table(["压制开始", "压制结束", "防守方", "连续招式", "已完成／待尝试"],
            pressures.Select(p => new[] { Time(p.Start), Time(p.End), $"{p.Defender + 1}P",
                p.ConfirmedMoves.ToString(), $"{results.Count(r => r.PressureStart == p.Start && r.Defender == p.Defender && selectedMoves.Contains(r.Move))}/{selectedMoves.Length}" }));
        // This table only selects scope. Playback is always an actual skill branch.
        content.Children.Add(Card("本次压制范围 · 与压制总表一致", overview));
    }

    private Task RunPressureEscapeAsync(PressureEscapeJob[] jobs)
    {
        if (pressureEscapeTask is { IsCompleted: false }) return Task.CompletedTask;
        return pressureEscapeTask = RunPressureEscapeCoreAsync(jobs);
    }

    private async Task RunPressureEscapeCoreAsync(PressureEscapeJob[] jobs)
    {
        if (shutdownInProgress || shutdownReady || probeConnecting || ReplayControlInFlight || escapeBusy || branchBusy || fullCaptureBusy || sessionLoading || jobs.Length == 0) return;
        if (probe is null || session?.Header is null || branchOriginal is not null ||
            !string.Equals(Path.GetExtension(session.Source), ".rep", StringComparison.OrdinalIgnoreCase))
        { message = "先连接游戏并返回原 REP，再开始整场逃压推演。"; Render(); return; }
        var original = session;
        var activeProbe = probe;
        var returnCursor = cursor;
        using var cancellation = new CancellationTokenSource();
        pressureEscapeCancellation = cancellation;
        escapeBusy = true;
        escapeBatchDone = 0;
        escapeBatchTotal = jobs.Length;
        PausePlayback();
        ClearNativeSnapshots();
        message = "正在建立整场压制的实机对照…";
        Render();
        await playbackControlGate.WaitAsync();
        try
        {
            if (shutdownInProgress || shutdownReady) return;
            var progress = new Progress<EscapeBatchProgress>(update => {
                if (!escapeBusy || !ReferenceEquals(pressureEscapeCancellation, cancellation)) return;
                escapeBatchDone = update.Completed;
                if (escapeProgressLabel is not null) escapeProgressLabel.Text =
                    $"已完成 {update.Completed}/{update.Total} 项 · {Time(update.Result.PressureStart)} · {update.Result.Move}：{update.Result.Verdict.Result}";
            });
            var batch = await new EscapeSimulationService().RunAsync(original, jobs,
                new EscapeSimulationGameEngine(activeProbe, original, root), returnCursor,
                progress, cancellation.Token);
            foreach (var result in batch.Results) StoreEscapeResult(result);
            escapeBatchDone = batch.Completed;
            probeStatus = activeProbe.Read();
            if (batch.RestoreError is null)
            {
                nativeReplaySource = original.Source;
                nativePlaybackReleased = false;
                probeStatus = await activeProbe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
            }
            else { nativeReplaySource = null; nativePlaybackReleased = true; }
            message = batch.Error is not null ? $"推演已停止：{batch.Error}；已完成结果保留。" :
                $"整场逃压推演{(batch.Cancelled ? "已停止" : "完成")}，已完成 {batch.Completed}/{batch.Total} 项，用时 {batch.Elapsed.TotalSeconds:F1} 秒。";
            if (batch.RestoreError is not null) message += $" 恢复原回放失败：{batch.RestoreError}";
        }
        catch (Exception ex) { message = $"推演已停止：{ex.Message}；已完成结果保留。"; }
        finally
        {
            session = original;
            if (NativeSessionMatches && probeStatus?.Status == 3) AdoptPlaybackPosition(probeStatus);
            else { nativeReplaySource = null; nativePlaybackReleased = true; }
            playbackControlGate.Release();
            escapeBusy = false;
            pressureEscapeCancellation = null;
            Render();
        }
    }
}
