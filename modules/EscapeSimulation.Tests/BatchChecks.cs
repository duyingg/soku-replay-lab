using System.IO;
using SokuReplayLab;

internal static class BatchChecks
{
    public static async Task RunAsync()
    {
        Fighter Fighter(int action = 0, int frame = 1) =>
            new(0, 0, 0, action, frame, 10000, 1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Direction: 1);
        var frames = Enumerable.Range(0, 400).Select(i =>
            new Frame(i, i, 2, 0, 0, Fighter(), Fighter())).ToArray();
        var source = new ReplaySession {
            Source = "test.rep", Frames = frames, Pressures = [new(100, 120, 0, 1, 0, 2, 2, "", ""),
                new(180, 200, 0, 1, 0, 3, 3, "", "")], Events = [], Combos = [], PressureGaps = [],
            EscapeAttempts = [], CardEvents = [], Curves = [], Stats = []
        };
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); }

        var engine = new FakeEngine(source);
        var job = new PressureEscapeJob(source.Pressures[0], "8");
        var batch = await new EscapeSimulationService().RunAsync(source, [job, job], engine, 42);
        Check(batch.Completed == 1 && batch.Total == 1 && batch.Results.Single().Verdict.Result == "成功" &&
            batch.Error is null && engine.RestoredCursor == 42, "Batch deduplication or restoration failed.");

        using var cancel = new CancellationTokenSource();
        engine = new FakeEngine(source) { AfterRun = cancel.Cancel };
        batch = await new EscapeSimulationService().RunAllAsync(source, 2, ["8"], engine, 7,
            cancellationToken: cancel.Token);
        Check(batch.Cancelled && batch.Completed <= 1 && engine.Runs == 1 && engine.RestoredCursor == 7,
            "Cancellation continued to another branch or skipped restoration.");

        using var cancelledBefore = new CancellationTokenSource();
        cancelledBefore.Cancel();
        engine = new FakeEngine(source);
        batch = await new EscapeSimulationService().RunAllAsync(source, 2, ["8"], engine, 0,
            cancellationToken: cancelledBefore.Token);
        Check(batch.Cancelled && !engine.Started && engine.Runs == 0 && engine.RestoredCursor is null,
            "Pre-cancelled batch touched the engine.");

        engine = new FakeEngine(source) { FailRunOnce = true };
        batch = await new EscapeSimulationService().RunAllAsync(source, 2, ["8"], engine, 8);
        Check(batch.Completed == 2 && batch.Results[0].Verdict.Result == "执行错误" &&
            batch.Results[1].Verdict.Result == "成功" && batch.Error is null && engine.RestoredCursor == 8,
            "Recoverable failure lost the rest of the selected pressures.");

        engine = new FakeEngine(source) { FailRunOnce = true, CanContinue = false };
        batch = await new EscapeSimulationService().RunAllAsync(source, 2, ["8"], engine, 9);
        Check(batch.Error is not null && batch.Completed == 1 && engine.RestoredCursor == 9,
            "Unusable engine did not stop safely.");

        engine = new FakeEngine(source) { FailStart = true };
        batch = await new EscapeSimulationService().RunAllAsync(source, 2, ["8"], engine, 10);
        Check(batch.Error is not null && engine.RestoredCursor == 10, "Partial startup did not restore.");

        engine = new FakeEngine(source) { FailRestore = true };
        batch = await new EscapeSimulationService().RunAllAsync(source, 3, ["8"], engine, 11);
        Check(batch.Results.Count == 1 && batch.RestoreError is not null, "Restore failure lost completed results.");
        var attackJob = job with { Move = "4A" };
        engine = new FakeEngine(source) { Attack = true };
        batch = await new EscapeSimulationService().RunAsync(source, [attackJob], engine, 12);
        Check(batch.Results.Single().Verdict.Result == "成功", "Verified attack and control were not accepted.");
        foreach (var mode in new[] { "inputs", "anchor", "short" })
        {
            engine = new FakeEngine(source) { Attack = true, InvalidControl = mode };
            batch = await new EscapeSimulationService().RunAsync(source, [attackJob], engine, 12);
            Check(batch.Results.Single().Verdict.Result == "未判定" && engine.RestoredCursor == 12,
                "Invalid guard-only control produced an attack recommendation: " + mode);
        }
        using var cancelAttack = new CancellationTokenSource();
        engine = new FakeEngine(source) { Attack = true, AfterRun = cancelAttack.Cancel };
        batch = await new EscapeSimulationService().RunAsync(source, [attackJob], engine, 13,
            cancellationToken: cancelAttack.Token);
        Check(batch.Cancelled && engine.Runs == 1 && engine.RestoredCursor == 13 &&
            batch.Results.Single().Verdict.Result == "已取消", "Stop request started an additional control branch.");
        Console.WriteLine("PASS: standalone batch API, stop requests, recoverable/fatal errors, startup and restore failures.");
        Console.WriteLine("PASS: attack-control input verification, control anchor/range rejection and cancellation before a new control.");
    }

    private sealed class FakeEngine(ReplaySession original) : IEscapeBranchEngine
    {
        public bool CanContinue { get; init; } = true;
        public bool FailStart { get; init; }
        public bool FailRunOnce { get; set; }
        public bool FailRestore { get; init; }
        public Action? AfterRun { get; init; }
        public bool Attack { get; init; }
        public string? InvalidControl { get; init; }
        public bool Started { get; private set; }
        public int Runs { get; private set; }
        public int? RestoredCursor { get; private set; }

        public Task StartAsync(int requiredFrame)
        {
            Started = true;
            if (FailStart) throw new IOException("test startup failure");
            return Task.CompletedTask;
        }

        public Task<EscapeBranch> RunAsync(EscapeInputPlan plan)
        {
            Runs++;
            if (FailRunOnce) { FailRunOnce = false; throw new IOException("test trial failure"); }
            var frames = original.Frames.ToArray();
            var control = Attack && plan.Inputs.All(mask => (mask & 0x10) == 0);
            if (Attack && !control)
            {
                for (var i = plan.Anchor + 2; i <= plan.Anchor + 8; i++)
                    frames[i] = frames[i] with { P1 = frames[i].P1 with { Action = 300, ActionFrame = i - plan.Anchor - 1 } };
                var hit = plan.Anchor + 3;
                frames[hit] = frames[hit] with { P1 = frames[hit].P1 with { Collision = 1, ComboDamage = 200 },
                    P2 = frames[hit].P2 with { Action = 50, Hp = 9800 } };
                for (var i = hit + 1; i < frames.Length; i++) frames[i] = frames[i] with { P2 = frames[i].P2 with { Hp = 9800 } };
            }
            else if (!Attack)
                frames[plan.Anchor + 2] = frames[plan.Anchor + 2] with { P1 = frames[plan.Anchor + 2].P1 with { Action = 6 } };
            if (control && InvalidControl == "short") frames = frames.Take(plan.Anchor + 4).ToArray();
            AfterRun?.Invoke();
            return Task.FromResult(new EscapeBranch(frames, "test-branch.rep",
                plan.Anchor + (control && InvalidControl == "anchor" ? 1 : 0),
                Math.Min(plan.Anchor + plan.Inputs.Length, frames.Length - 1),
                control && InvalidControl == "inputs" ? 0 : plan.Inputs.Length));
        }

        public Task RestoreAsync(int cursor)
        {
            RestoredCursor = cursor;
            if (FailRestore) throw new IOException("test restoration failure");
            return Task.CompletedTask;
        }
    }
}
