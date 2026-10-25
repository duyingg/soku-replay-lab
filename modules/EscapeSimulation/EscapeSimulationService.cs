using System.Diagnostics;
using System.IO;

namespace SokuReplayLab;

public sealed record EscapeBatchProgress(int Completed, int Total, EscapeTrialResult Result);
public sealed record EscapeBatchResult(IReadOnlyList<EscapeTrialResult> Results,
    int Completed, int Total, bool Cancelled, string? Error, string? RestoreError, TimeSpan Elapsed);

/// <summary>Runs a batch sequentially and restores the host engine before returning.</summary>
public sealed class EscapeSimulationService
{
    public Task<EscapeBatchResult> RunAllAsync(ReplaySession source, int minimumMoves,
        IEnumerable<string> moves, IEscapeBranchEngine engine, int returnCursor,
        IProgress<EscapeBatchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(source, PressureEscapeSimulation.Jobs(source, minimumMoves, moves),
            engine, returnCursor, progress, cancellationToken);

    public async Task<EscapeBatchResult> RunAsync(ReplaySession source,
        IReadOnlyList<PressureEscapeJob> jobs, IEscapeBranchEngine engine, int returnCursor,
        IProgress<EscapeBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(engine);
        if (returnCursor < 0 || returnCursor >= source.Frames.Count)
            throw new ArgumentOutOfRangeException(nameof(returnCursor));
        var ordered = jobs.DistinctBy(j => (j.Pressure.Start, j.Pressure.Defender, j.Move))
            .OrderBy(j => j.Pressure.Start).ThenBy(j => j.Pressure.Defender).ToArray();
        var results = new List<EscapeTrialResult>();
        var runner = new PressureEscapeRunner(source, engine);
        var timer = Stopwatch.StartNew();
        var completed = 0;
        var touched = false;
        string? error = null;
        string? restoreError = null;
        try
        {
            if (!cancellationToken.IsCancellationRequested && ordered.Length > 0)
            {
                var required = ordered.SelectMany(job => PressureEscapeSimulation.Plans(source, job))
                    .Select(p => p.Anchor + p.Inputs.Length).DefaultIfEmpty(-1).Max();
                if (required >= 0)
                {
                    touched = true;
                    await runner.StartAsync(required);
                }
                foreach (var job in ordered)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    EscapeTrialResult result;
                    try { result = await runner.RunAsync(job, () => cancellationToken.IsCancellationRequested); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
                    {
                        result = new(job.Pressure.Start, job.Pressure.Defender, job.Move,
                            new("执行错误", ex.Message), source, "", job.Pressure.Start, 0) {
                            PressureStart = job.Pressure.Start, PressureEnd = job.Pressure.End
                        };
                    }
                    results.Add(result);
                    if (result.Verdict.Result == "已取消") break;
                    completed++;
                    progress?.Report(new(completed, ordered.Length, result));
                    if (result.Verdict.Result == "执行错误" && !engine.CanContinue)
                        throw new IOException("推演引擎未恢复可用状态，停止后续试验。");
                }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            // A stop request does not cancel restoration of the original replay.
            if (touched)
            {
                try { await runner.RestoreAsync(returnCursor); }
                catch (Exception ex) { restoreError = ex.Message; }
            }
        }
        return new(results.ToArray(), completed, ordered.Length,
            cancellationToken.IsCancellationRequested, error, restoreError, timer.Elapsed);
    }
}
