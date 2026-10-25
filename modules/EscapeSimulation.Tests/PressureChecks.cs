using System.IO;
using System.Buffers.Binary;
using System.Text.Json;
using SokuReplayLab;

internal static class PressureChecks
{
    public static void CheckRecorded(string root)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "pressure-live-check.json")));
        if (report.RootElement.GetProperty("error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Recorded engine check did not complete successfully.");
        var log = report.RootElement.GetProperty("log").EnumerateArray().ToArray();
        var playback = log.Single(row => row.GetProperty("phase").GetString() == "playback").GetProperty("result");
        var replay = playback.GetProperty("OriginalSource").GetString()!;
        var branchPath = playback.GetProperty("PlaybackSource").GetString()!;
        var anchor = playback.GetProperty("Anchor").GetInt32();
        var move = playback.GetProperty("Move").GetString()!;
        var side = int.Parse(Path.GetFileNameWithoutExtension(branchPath).Split('_')[3]);
        var source = ReplayAnalyzer.Analyze(replay, ReplayHeader.Read(replay), TraceReader.Read(Path.Combine(root, "fresh.csv")));
        var plan = source.Pressures.Where(p => p.Defender == side)
            .SelectMany(p => PressureEscapeSimulation.Plans(source, new(p, move)))
            .First(p => p.Anchor == anchor);
        var branch = TraceReader.ReadTailWithPrefix(Path.ChangeExtension(branchPath, ".csv"), source.Frames, anchor);
        var guard = PressureEscapeSimulation.GuardInputs(source, anchor, side, plan.Inputs.Length);
        var prefix = string.Join('_', Path.GetFileNameWithoutExtension(branchPath).Split('_').Take(4));
        var control = Directory.GetFiles(root, prefix + "_*.csv")
            .Select(path => TraceReader.ReadTailWithPrefix(path, source.Frames, anchor))
            .FirstOrDefault(frames => frames.Count > anchor + guard.Length && Enumerable.Range(0, guard.Length)
                .All(i => frames[anchor + i + 1].Side(side).Input == guard[i]));
        if (branch.Count <= anchor + plan.Inputs.Length || Enumerable.Range(0, plan.Inputs.Length)
            .Any(i => branch[anchor + i + 1].Side(side).Input != plan.Inputs[i]))
            throw new InvalidDataException("Recorded branch input differs from the selected plan.");
        if (playback.GetProperty("StateMismatches").GetInt32() != 0)
            throw new InvalidDataException("REP playback differs from simulated state.");
        var batch = log.Single(row => row.GetProperty("phase").GetString() == "batch");
        var chosen = batch.GetProperty("Verdicts").EnumerateArray().Select(row => row.GetString()!.Split(':', 5))
            .Single(parts => int.Parse(parts[0]) == anchor && int.Parse(parts[1]) == side && parts[2] == move);
        // The UI also permits playing failed attempts. Such a recording need
        // not contain a successful attack or an unnecessary guard-only branch.
        if (chosen[3] == "成功" && PressureEscapeSimulation.Evaluate(plan, branch, control ?? branch, plan.Inputs.Length).Result != "成功")
            throw new InvalidDataException("Recorded success no longer meets evidence requirements.");
        var selectedCount = log.Single(row => row.GetProperty("phase").GetString() == "capture").GetProperty("selected").GetInt32();
        if (batch.GetProperty("Completed").GetInt32() != selectedCount)
            throw new InvalidDataException("Not every selected pressure was processed.");
        foreach (var line in batch.GetProperty("Verdicts").EnumerateArray())
        {
            var parts = line.GetString()!.Split(':', 5);
            if (parts[3] != "成功") continue;
            var trialAnchor = int.Parse(parts[0]);
            var trialSide = int.Parse(parts[1]);
            var trialPlan = source.Pressures.Where(p => p.Defender == trialSide)
                .SelectMany(p => PressureEscapeSimulation.Plans(source, new(p, parts[2])))
                .First(p => p.Anchor == trialAnchor);
            var candidates = Directory.GetFiles(root, $"inplace_*_{trialAnchor}_{trialSide}_*.csv")
                .Select(path => TraceReader.ReadTailWithPrefix(path, source.Frames, trialAnchor)).ToArray();
            var actual = candidates.First(rows => rows.Count > trialAnchor + trialPlan.Inputs.Length && Enumerable.Range(0, trialPlan.Inputs.Length)
                .All(i => rows[trialAnchor + i + 1].Side(trialSide).Input == trialPlan.Inputs[i]));
            var pureGuard = PressureEscapeSimulation.GuardInputs(source, trialAnchor, trialSide, trialPlan.Inputs.Length);
            var reference = candidates.FirstOrDefault(rows => rows.Count > trialAnchor + pureGuard.Length && Enumerable.Range(0, pureGuard.Length)
                .All(i => rows[trialAnchor + i + 1].Side(trialSide).Input == pureGuard[i])) ?? actual;
            if (PressureEscapeSimulation.Evaluate(trialPlan, actual, reference, trialPlan.Inputs.Length).Result != "成功")
                throw new InvalidDataException("Recorded success lacks collision or combo evidence: " + trialAnchor);
        }
        Console.WriteLine("PASS: recorded completed scope, branch inputs, successful-result evidence and exact REP playback state.");
    }

    public static void Run()
    {
        Fighter Fighter(int action = 0, int hp = 10000, int actionFrame = 1) =>
            new(0, 0, 0, action, actionFrame, hp, 1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                Direction: 1);
        Frame[] Frames() => Enumerable.Range(0, 400).Select(i =>
            new Frame(i, i, 2, 0, 0, Fighter(), Fighter())).ToArray();
        void Check(bool value, string message)
        { if (!value) throw new InvalidDataException(message); }
        var frames = Frames();
        for (var i = 100; i < 110; i++) frames[i] = frames[i] with { P2 = Fighter(150, actionFrame: i - 99) };
        var source = new ReplaySession {
            Source = "test.rep", Frames = frames, Events = [], Combos = [],
            Pressures = [new(100, 120, 0, 0, 1, 2, 1, "", ""),
                new(140, 160, 0, 1, 0, 3, 2, "", ""), new(180, 210, 0, 0, 1, 4, 3, "", "")],
            PressureGaps = [], EscapeAttempts = [], CardEvents = [], Curves = [], Stats = []
        };
        Check(PressureSelection.Select(source, 2).Count == 2, "Pressure threshold changed.");
        var jobs = PressureEscapeSimulation.Jobs(source, 2, ["4A", "8", "4A"]);
        Check(jobs.Count == 4 && jobs.All(j => j.Pressure.ConfirmedMoves >= 2), "Jobs differ from pressure table or repeat.");
        Check(PressureEscapeSimulation.Jobs(source, 3, ["4A"]).Count == 1, "Three-move threshold ignored.");
        var aligned = Frames();
        var rep = new byte[43 + aligned.Length * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(rep, 0xd2);
        for (var i = 0; i < aligned.Length; i++)
        {
            var input = i is >= 160 and <= 220 ? 0 : i + 1;
            aligned[i] = aligned[i] with { P1 = aligned[i].P1 with { Input = input } };
            BinaryPrimitives.WriteUInt16LittleEndian(rep.AsSpan(43 + i * 4), (ushort)input);
        }
        var repPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(repPath, rep);
            Check(ReplayInputAligner.NextInputIndex(repPath, aligned, 200) == 201,
                "Repeated held-input history was not uniquely aligned using earlier edges.");
        }
        finally { File.Delete(repPath); }
        var plans = PressureEscapeSimulation.Plans(source, new(source.Pressures[0], "4A"));
        Check(plans.Count > 0, "Pressure without detected gaps was omitted.");
        Check(plans.All(p => p.Inputs.Length == 124 && p.Inputs[0] == 0 && p.Inputs[1] == (4 | 0x10)), "Input schedule changed.");

        var anchor = 60;
        var plan = new EscapeInputPlan(anchor, 0, "4A", new ushort[124]);
        var control = Frames();
        var branch = Frames();
        for (var i = 62; i <= 70; i++) branch[i] = branch[i] with { P1 = Fighter(300, actionFrame: i - 61) };
        branch[64] = branch[64] with { P2 = Fighter(50, 9800) };
        branch[64] = branch[64] with { P1 = branch[64].P1 with { Collision = 1, ComboDamage = 200 } };
        for (var i = 65; i < branch.Length; i++) branch[i] = branch[i] with { P2 = Fighter(hp: 9800) };
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 124).Result == "成功", "Observed counterhit was lost.");
        Check(PressureEscapeSimulation.Evaluate(plan, branch, branch, 124).Result != "成功", "Existing projectile damage credited to new move.");
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 123).Result == "未判定", "Partial inputs accepted.");
        branch[100] = branch[100] with { P1 = Fighter(50, 9800) };
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 124).Result == "受击", "Trade or later hit recommended.");
        branch = Frames();
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 124).Result == "未发动", "Missing activation accepted.");
        plan = plan with { Move = "8" };
        for (var i = 62; i <= 70; i++) branch[i] = branch[i] with { P1 = Fighter(6, actionFrame: i - 61) };
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 124).Result == "成功", "Verified movement not recognized.");
        branch[100] = branch[100] with { P1 = Fighter(150) };
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 124).Result == "仍受压", "Reblocking recommended.");
        branch = Frames();
        for (var i = anchor; i < branch.Length; i++) branch[i] = branch[i] with { P1 = Fighter(6, actionFrame: i + 1) };
        control[anchor] = branch[anchor];
        Check(PressureEscapeSimulation.Evaluate(plan, branch, control, 124).Result == "未发动", "Existing jump counted as newly started.");
        Console.WriteLine("PASS: pressure thresholds, all-pressure coverage, deduplication, input plans, activation, controlled effects, hits and reblocking.");
    }
}
