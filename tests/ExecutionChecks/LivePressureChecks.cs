using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using SokuReplayLab;

internal static class LivePressureChecks
{
    public static async Task CheckExternalGameAsync(string exe, string report)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var directory = Path.Combine(root, "escape_worker_game");
        if (Directory.Exists(directory)) throw new IOException("Game-copy directory must be absent before this check.");
        var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exe)));
        var workerType = typeof(MainWindow).Assembly.GetType("SokuReplayLab.EscapeWorkerGame")!;
        var task = (Task)workerType.GetMethod("StartAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [exe, root])!;
        await task;
        var worker = (IAsyncDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        string? actual = null;
        try
        {
            actual = (string)workerType.GetProperty("Exe", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            if (!string.Equals(Path.GetFullPath(exe), actual, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
                throw new IOException("Diagnostic execution copied or redirected the game.");
        }
        finally { await worker.DisposeAsync(); }
        var after = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exe)));
        if (before != after || Directory.Exists(directory)) throw new IOException("External game EXE changed or a workspace copy was created.");
        File.WriteAllText(report, JsonSerializer.Serialize(new { externalExe = actual,
            sameExecutable = true, gameCopyCreated = false, executableUnchanged = true }));
        Console.WriteLine("PASS: diagnostic session uses the external EXE directly; no game copy created.");
    }

    public static async Task RunAsync(string exe, string replay, string output)
    {
        var root = Path.GetFullPath(output);
        Directory.CreateDirectory(root);
        using var gameProcess = GameDiscovery.Launch(exe);
        ProbeClient? client = null;
        MainWindow? window = null;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var log = new List<object>();
        Exception? error = null;
        try
        {
            GameInstance? game = null;
            for (var i = 0; i < 80 && game is null; i++)
            {
                game = GameDiscovery.FindProcesses().FirstOrDefault(g => g.Pid == gameProcess.Id && g.Window != 0);
                if (game is null) await Task.Delay(100);
            }
            if (game is null) throw new IOException("Test game did not start.");
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            client = await ProbeClient.AttachAsync(game, project);
            var trace = Path.Combine(root, "fresh.csv");
            var clock = Stopwatch.StartNew();
            await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            await client.SendAsync(ProbeCommand.Capture, timeout: TimeSpan.FromMinutes(2));
            var deadline = Stopwatch.StartNew();
            while (client.Read().Status != 3)
            {
                if (client.Read().Status < 0 || deadline.Elapsed.TotalSeconds > 120)
                    throw new IOException("Fresh capture failed.");
                await Task.Delay(100);
            }
            var source = ReplayAnalyzer.Analyze(replay, ReplayHeader.Read(replay), TraceReader.Read(trace), "已验证的完整实机轨迹");
            log.Add(new { phase = "capture", frames = source.Frames.Count, seconds = clock.Elapsed.TotalSeconds,
                pressures = source.Pressures.Count, selected = PressureSelection.Select(source, 2).Count });
            Console.WriteLine($"Captured {source.Frames.Count} frames; {PressureSelection.Select(source, 2).Count} pressures meet the two-move threshold.");
            window = new MainWindow();
            typeof(MainWindow).GetField("root", flags)!.SetValue(window, root);
            typeof(MainWindow).GetField("minPressureMoves", flags)!.SetValue(window, 2);
            window.SmokeConnectResident(game, source);
            typeof(MainWindow).GetField("playbackSpeed", flags)!.SetValue(window, 2.0);
            var complete = await window.SmokeFullCaptureAsync();
            if (complete.FrameCount != source.Frames.Count || client.Read().Status != 3 || client.Read().Target != 200)
                throw new IOException("Host capture lost the full model, pause state or selected speed.");
            log.Add(new { phase = "host-capture", complete.FrameCount, complete.FinalOrdinal, complete.Message,
                speedRestored = true });
            window.SmokeNavigate(6);
            clock.Restart();
            var count = PressureSelection.Select(source, 2).Count;
            var batch = await window.SmokeEscapeBatchAsync(count);
            log.Add(new { phase = "batch", batch.Completed, batch.Total, batch.Success, batch.Failures,
                batch.Unresolved, batch.ExecutionErrors, batch.Message, batch.Verdicts, seconds = clock.Elapsed.TotalSeconds });
            if (batch.Completed != count || batch.ExecutionErrors > 0 || batch.Message.Contains("失败") || batch.Message.Contains("已停止"))
                throw new IOException("New batch did not complete every selected pressure: " + batch.Message);
            Console.WriteLine(batch.Message);
            var playback = (Task<object>)typeof(MainWindow).GetMethod("SmokeEscapeResultPlaybackAsync", flags)!.Invoke(window, null)!;
            log.Add(new { phase = "playback", result = await playback });
            var anchor = (int)typeof(MainWindow).GetField("branchAnchor", flags)!.GetValue(window)!;
            typeof(MainWindow).GetMethod("ReturnOriginalBranch", flags)!.Invoke(window, null);
            var returnClock = Stopwatch.StartNew();
            while ((bool)typeof(MainWindow).GetField("branchBusy", flags)!.GetValue(window)!)
            {
                if (returnClock.Elapsed.TotalSeconds > 30) throw new IOException("Return from skill branch timed out.");
                await Task.Delay(50);
            }
            var restored = (ReplaySession)typeof(MainWindow).GetField("session", flags)!.GetValue(window)!;
            if (restored.Source != replay || client.Read().Status != 3 || client.Read().Ordinal != anchor ||
                client.Read().Target != 200 || typeof(MainWindow).GetField("branchOriginal", flags)!.GetValue(window) is not null)
                throw new IOException("Returning from skill branch left the wrong REP, position or speed.");
            log.Add(new { phase = "return-original", source = restored.Source, anchor, speedRestored = true });
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            if (window is not null)
            {
                typeof(MainWindow).GetField("shutdownReady", flags)!.SetValue(window, true);
                window.Close();
            }
            client?.Dispose();
            await GameDiscovery.CloseVerifiedGameAsync(gameProcess.Id, exe);
            File.WriteAllText(Path.Combine(root, "pressure-live-check.json"), JsonSerializer.Serialize(new {
                log, error = error?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) throw error;
    }
}
