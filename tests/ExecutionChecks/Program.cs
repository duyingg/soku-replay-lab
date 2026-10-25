using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using SokuReplayLab;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--workflow")
        {
            Directory.CreateDirectory(args[1]); CheckParser(args[1]);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(WorkflowChecks.RunAsync(args[1])); return;
        }
        if (args.Length == 2 && args[0] == "--dock")
        {
            DockLayoutChecks.Run();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(DockLayoutChecks.RunViewAsync(args[1])); return;
        }
        if (args.Length == 4 && args[0] == "--playback")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(LivePlaybackChecks.RunAsync(args[1], args[2], args[3]));
            return;
        }
        if (args.Length == 4 && args[0] == "--scoretracker")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(LiveScoreTrackerChecks.RunAsync(args[1], args[2], args[3]));
            return;
        }
        if (args.Length == 3 && args[0] == "--views")
        {
            ViewChecks.Run(args[1], args[2]);
            return;
        }
        if (args.Length == 4 && args[0] == "--audio")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(LiveAudioChecks.RunAsync(args[1], args[2], args[3]));
            return;
        }
        if (args.Length == 3 && args[0] == "--external-game")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(LivePressureChecks.CheckExternalGameAsync(args[1], args[2]));
            return;
        }
        if (args.Length == 2 && args[0] == "--recorded")
        {
            PressureChecks.CheckRecorded(args[1]);
            return;
        }
        if (args.Length == 4 && args[0] == "--live")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Pump(LivePressureChecks.RunAsync(args[1], args[2], args[3]));
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "SokuReplayLabChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckParser(root);
            DockLayoutChecks.Run();
            var rejected = false;
            try { GameDiscovery.VerifyExternalGameLocation(Path.Combine(AppContext.BaseDirectory, "th123.exe")); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Workspace game copy was accepted.");
            PressureChecks.Run();
            CheckRefresh(root);
            Pump(WorkflowChecks.RunAsync(root));
            SynchronizationContext.SetSynchronizationContext(null);
            ProbeClient.ProtocolSmokeAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS: CSV parsing, prefix/tail reads, stale refresh, cancelled command and probe protocol.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void CheckParser(string root)
    {
        const string header = "frame,match_state,round,weather,p1_char,p1_action,p1_hp,p2_char,p2_action,p2_hp,p1_hand_ids,p1_used_ids,note";
        string[] rows = ["0,2,0,0,0,0,10000,1,0,10000,?,,text",
            "1,2,0,0,0,300,10000,1,50,9800,12|200,12,text",
            "2,2,0,0,0,300,10000,1,50,9800,,?,text"];
        var plain = Path.Combine(root, "plain.csv");
        var quoted = Path.Combine(root, "quoted.csv");
        File.WriteAllLines(plain, new[] { header }.Concat(rows));
        File.WriteAllLines(quoted, new[] { header }.Concat(rows.Select(row =>
            string.Join(',', row.Split(',').Select(cell => '"' + cell + '"')))));
        var frames = TraceReader.Read(plain);
        Require(JsonSerializer.Serialize(frames) == JsonSerializer.Serialize(TraceReader.Read(quoted)),
            "Quoted and unquoted CSV differ.");
        Require(frames[0].P1.HandIds is null && frames[0].P1.UsedIds is { Length: 0 } &&
            frames[1].P1.HandIds!.SequenceEqual([12, 200]) && frames[2].P1.UsedIds is null,
            "Unknown, empty or multiple card IDs changed.");
        var prefix = TraceReader.ReadWithPrefix(plain, frames, 1);
        Require(ReferenceEquals(prefix[0], frames[0]) && JsonSerializer.Serialize(prefix) == JsonSerializer.Serialize(frames),
            "Prefix reconstruction differs.");
        File.WriteAllLines(quoted, new[] { header }.Concat(rows.Skip(1)));
        Require(JsonSerializer.Serialize(TraceReader.ReadTailWithPrefix(quoted, frames, 1)) == JsonSerializer.Serialize(frames),
            "Tail reconstruction differs.");
        foreach (var bad in new[] { rows[0] + ",extra", rows[0][..rows[0].LastIndexOf(',')], rows[0] + "\"" })
        {
            File.WriteAllLines(quoted, [header, bad]);
            var rejected = false;
            try { TraceReader.Read(quoted); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Malformed CSV was accepted.");
        }
        File.WriteAllLines(quoted, [header, rows[0][..^4] + "\"comma, and \"\"quote\"\"\""]);
        Require(TraceReader.Read(quoted).Count == 1, "Escaped quotes or comma in imported CSV failed.");
    }

    private static void CheckRefresh(string root)
    {
        // Keep the continuation queued on the UI dispatcher while changing the
        // selected replay. No timing assumptions or real game are required.
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var window = new MainWindow();
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        object? Get(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
        var header = new ReplayHeader(Path.Combine(root, "original.rep"), "test", "1P", "2P", 3, 0, 1);
        File.Copy(Path.Combine(root, "plain.csv"), Path.Combine(root, "trace_test.csv"));
        ReplaySession Make(string source) => ReplayAnalyzer.Analyze(source, header, TraceReader.Read(Path.Combine(root, "plain.csv")));
        var first = Make(header.Path);
        var second = Make(Path.Combine(root, "second.rep"));
        Set("root", root);
        Set("session", first);
        var refresh = (Task)typeof(MainWindow).GetMethod("RefreshSessionFromProbe", Private)!.Invoke(window, null)!;
        Set("session", second);
        Pump(refresh);
        Require(ReferenceEquals(Get("session"), second), "Old refresh replaced the newly selected replay.");

        Set("session", first);
        var older = (Task)typeof(MainWindow).GetMethod("RefreshSessionFromProbe", Private)!.Invoke(window, null)!;
        var newer = (Task)typeof(MainWindow).GetMethod("RefreshSessionFromProbe", Private)!.Invoke(window, null)!;
        Pump(Task.WhenAll(older, newer));
        Require(!ReferenceEquals(Get("session"), first) && ((ReplaySession)Get("session")!).Source == first.Source,
            "Latest refresh was lost.");
        Set("shutdownReady", true);
        window.Close();
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("UI refresh did not complete.");
        task.GetAwaiter().GetResult();
    }
}
