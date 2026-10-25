using System.IO;
using System.Reflection;
using System.Text.Json;
using SokuReplayLab;

internal static class WorkflowChecks
{
    public static async Task RunAsync(string testRoot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var root = Path.Combine(testRoot, "workflow"); Directory.CreateDirectory(root);
        var csv = Path.Combine(root, "second.csv");
        File.Copy(Path.Combine(testRoot, "plain.csv"), csv);
        var window = new MainWindow();
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        Task Call(string name, params object[] args) => (Task)typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args)!;
        void Check(bool value, string reason) { if (!value) throw new IOException(reason); }
        var list = (List<ReplaySession>)Get("samples")!;
        Set("root", root); Set("probe", null); Set("detectedGame", null);
        var first = ReplayAnalyzer.Analyze(Path.Combine(root, "first.csv"), null, TraceReader.Read(csv));
        var second = SessionLoader.Load(csv, root);
        try
        {
            list.Clear(); list.Add(first); list.Add(second);
            Set("session", ReplayAnalyzer.Analyze(Path.Combine(root, "skill.rep"), null, first.Frames));
            Set("branchOriginal", first);
            await Call("RemoveCurrentSampleAsync");
            Check(list.Count == 1 && list[0].Source == second.Source && ((ReplaySession)Get("session")!).Source == second.Source,
                "Removing a skill branch did not remove its original sample");

            var missing = ReplayAnalyzer.Analyze(Path.Combine(root, "missing.csv"), null, first.Frames);
            list.Clear(); list.Add(first); list.Add(missing);
            Set("session", first); Set("branchOriginal", null);
            await Call("RemoveCurrentSampleAsync");
            Check(list.Count == 2 && list.Contains(first) && ReferenceEquals(Get("session"), first),
                "A failed replacement import removed the current sample anyway");

            Set("statsRound", 99);
            await Call("Load", csv);
            Check(((ReplaySession)Get("session")!).Stats.Count > 0 && (int)Get("statsRound")! == -1,
                "A round filter from the previous sample hid all statistics");
            var current = Get("session");
            await Call("Load", missing.Source);
            Check(ReferenceEquals(Get("session"), current), "Invalid import discarded the current replay");
            var rows = StatsCsvExporter.Export(Path.Combine(root, "stats.csv"), (ReplaySession)current!, 0, 0);
            Check(rows == 1, "Filtered statistics export included another side, round or total");
            await Call("ClearAllSamplesAsync");
            Check(list.Count == 0 && Get("session") is null && Get("branchOriginal") is null && Get("nativeReplaySource") is null,
                "Clearing samples left stale replay context");
            File.WriteAllText(Path.Combine(root, "workflow-check.json"), JsonSerializer.Serialize(new {
                branchSampleRemoval = true, failedImportPreservesSample = true, invalidRoundFilterReset = true,
                invalidImportPreservesReplay = true, filteredExport = true, clearResetsContext = true
            }));
            Console.WriteLine("PASS: branch sample removal, failed-import preservation, round-filter reset, filtered export and sample clear.");
        }
        finally { Set("shutdownReady", true); window.Close(); }
    }
}
