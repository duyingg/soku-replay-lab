using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using SokuReplayLab;

internal static class LivePlaybackChecks
{
    public static async Task RunAsync(string exe, string replay, string output)
    {
        Directory.CreateDirectory(output);
        using var process = GameDiscovery.Launch(exe);
        ProbeClient? client = null;
        MainWindow? window = null;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var log = new List<object>();
        Exception? error = null;
        try
        {
            GameInstance? game = null;
            for (var i = 0; i < 100 && game is null; i++)
            {
                game = GameDiscovery.FindProcesses().FirstOrDefault(g => g.Pid == process.Id && g.Window != 0);
                if (game is null) await Task.Delay(100);
            }
            if (game is null) throw new IOException("Test game did not start.");
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            client = await ProbeClient.AttachAsync(game, project);
            var trace = Path.Combine(output, "complete.csv");
            await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            await client.SendAsync(ProbeCommand.Capture);
            var captureClock = Stopwatch.StartNew();
            while (client.Read().Status != 3)
            {
                if (captureClock.Elapsed.TotalSeconds > 100 || client.Read().Status < 0)
                    throw new IOException("Capture failed.");
                await Task.Delay(100);
            }
            var model = ReplayAnalyzer.Analyze(replay, ReplayHeader.Read(replay), TraceReader.Read(trace), "已验证的完整实机轨迹");
            window = new MainWindow();
            void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
            object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
            Task Call(string name, params object[] args) => (Task)typeof(MainWindow).GetMethod(name, flags | BindingFlags.Public)!.Invoke(window, args)!;
            void CallVoid(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
            void Require(bool condition, string text) { if (!condition) throw new IOException(text + ": " + Get("message")); }
            Set("root", Path.GetFullPath(output));
            Set("replayDockState", null); Set("replayDockDefaultState", null);
            ((Dictionary<string, ReplayPanelLayout>)Get("replayPanelLayouts")!).Clear();
            window.SmokeConnectResident(game, model);
            ((DispatcherTimer)Get("probeTimer")!).Start();
            window.Show();
            await Task.Delay(1000);
            await Call("SeekPlaybackAsync", 600);
            CheckPaused(600, "initial seek");
            var originalModel = Get("session");
            var originalCount = model.Frames.Count;
            var dock = (ReplayWorkspace)Get("replayWorkspace")!;
            dock.CompleteRegistration();
            var surface = (GameSurfaceHost)Get("gameSurface")!;
            var host = surface.HostHandle;
            Require(dock.MovePanel("bookmarks", "home-game", ReplayDockZone.Center), "Live panel merge failed");
            Require(dock.ClosePanel("game") && dock.OpenPanel("game"), "Live game panel close/reopen failed");
            window.UpdateLayout();
            Require(ReferenceEquals(surface, Get("gameSurface")) && surface.HostHandle == host && surface.IsAttached,
                "Docking destroyed or recreated the game window host");
            CheckPaused(600, "docking preserves game host and playback");
            Require(dock.Undo() && dock.Undo() && dock.Undo(), "Live panel undo failed");
            // An unknown resident probe must not be adopted as this REP.
            Set("nativeReplaySource", null);
            await client.SendAsync(ProbeCommand.Seek, 750);
            CallVoid("RefreshProbeStatus");
            Require((int)Get("cursor")! == 600, "Unknown native REP changed the selected model position");
            await Call("SeekPlaybackAsync", 600);
            CheckPaused(600, "unknown native source recovered");

            // Save and seek share one command queue, including stale-slot recovery.
            await Task.WhenAll(Call("SaveNativeSnapshotAsync", 600), Call("SeekPlaybackAsync", 650));
            CheckPaused(650, "snapshot and seek serialized");
            Require(((HashSet<int>)Get("nativeSnapshotSlots")!).Contains(600), "Snapshot was not saved");
            await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: trace);
            await client.SendAsync(ProbeCommand.Seek, 650); // Native slots no longer exist.
            await Call("SeekPlaybackAsync", 600);
            CheckPaused(600, "stale native snapshot falls back to seek");
            await Call("ToggleNativePlaybackAsync");
            Require(client.Read().Status == 2, "Play did not control the game");
            await Task.Delay(400);
            await Call("ToggleNativePlaybackAsync");
            var stopped = client.Read().Ordinal;
            await Task.Delay(250);
            CheckPaused(stopped, "pause after play");
            await Call("SeekPlaybackAsync", 600);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await Call("GiveGameFocusAsync");
                Require(Get("gameKeyboardFocus") is not null && client.Read().Status == 0,
                    "Real keyboard takeover was not established");
                await Task.Delay(300);
                if (cycle == 0)
                {
                    // Exercise the deactivation path before returning focus;
                    // its messages used to overwrite replay-mode acquisition.
                    var focus = Get("gameKeyboardFocus")!;
                    focus.GetType().GetMethod("UpdateForeground", flags)!.Invoke(focus, [nint.Zero]);
                    await Task.Delay(100);
                }
                if (cycle == 1)
                    await client.SendAsync(ProbeCommand.Release); // Player leaves the REP for title.
                await Call("ReturnGameFocusAsync");
                CheckPaused(600, cycle == 0 ? "return from takeover" : "return after leaving REP");
                Require(ReferenceEquals(Get("session"), originalModel) && ((ReplaySession)Get("session")!).Frames.Count == originalCount,
                    "Takeover return discarded the original analysis/REP model");
                await Call("ToggleNativePlaybackAsync");
                Require(client.Read().Status == 2, "Play did not recover after takeover");
                await Task.Delay(250);
                await Call("ToggleNativePlaybackAsync");
                var paused = client.Read().Ordinal;
                await Task.Delay(250);
                CheckPaused(paused, "pause after takeover " + cycle);
                await Call("SeekPlaybackAsync", 600);
            }
            await Call("ToggleNativePlaybackAsync");
            await Task.Delay(300);
            await Call("GiveGameFocusAsync");
            var bookmark = Get("takeoverBookmark")!;
            var movingAnchor = (int)bookmark.GetType().GetProperty("Ordinal")!.GetValue(bookmark)!;
            Require(movingAnchor > 600, "Takeover did not save the actual playing position");
            await Call("ReturnGameFocusAsync");
            CheckPaused(movingAnchor, "takeover during active playback");
            await Task.WhenAll(Call("SeekPlaybackAsync", 900), Call("SeekPlaybackAsync", 100),
                Call("SeekPlaybackAsync", 1200), Call("SeekPlaybackAsync", 700));
            CheckPaused(700, "rapid seeks keep last request");
            var random = new Random(20261024);
            for (var round = 0; round < 8; round++)
            {
                var targets = Enumerable.Range(0, 5).Select(_ => random.Next(250, 2200)).ToArray();
                await Task.WhenAll(targets.Select(target => Call("SeekPlaybackAsync", target)));
                CheckPaused(targets[^1], "stress seek round " + round);
            }
            await Call("SeekPlaybackAsync", 700);
            var slider = (Slider)Get("replaySlider")!;
            slider.Value = 420;
            Require((int)Get("cursor")! == 700, "Unconfirmed slider preview changed the displayed frame");
            CallVoid("CommitTransportSeek");
            await WaitForTarget(420);
            CheckPaused(420, "slider commit");
            CallVoid("Jump", 250);
            await WaitForTarget(250);
            CheckPaused(250, "node click");
            Require(ReferenceEquals(Get("session"), originalModel) && ((ReplaySession)Get("session")!).Frames.Count == originalCount,
                "Seeking truncated or replaced the analysis model");
            Require(typeof(MainWindow).GetMethod("StartInputRecording", flags) is null,
                "Player-input recording is still exposed");
            // A CSV model must never drive an unrelated live game or start the
            // independent preview clock when a probe is connected.
            Set("session", ReplayAnalyzer.Analyze(trace, null, model.Frames));
            Set("cursor", 123);
            await client.SendAsync(ProbeCommand.Play);
            await Task.Delay(250);
            CallVoid("RefreshProbeStatus");
            Require((int)Get("cursor")! == 123, "Live polling moved the offline CSV position");
            await client.SendAsync(ProbeCommand.Pause);
            await Call("ToggleNativePlaybackAsync");
            Require((bool)Get("playing")! == false && client.Read().Status == 3,
                "CSV/live mismatch started an independent playback clock");
            Set("session", originalModel);
            // Importing a CSV while a branch is selected must discard branch
            // ownership and pause the game without moving the CSV preview.
            Set("branchOriginal", originalModel);
            await client.SendAsync(ProbeCommand.Play);
            await Call("Load", trace);
            Require(Get("branchOriginal") is null && ((ReplaySession)Get("session")!).Header is null &&
                (int)Get("cursor")! == 0 && client.Read().Status == 3,
                "CSV import kept a stale branch or running game");
            Set("session", originalModel);
            Set("playbackSpeed", 0.75);
            await Call("LoadCurrentRepIntoGameAsync");
            Require(client.Read().Target == 75, "REP reload lost the selected playback speed");
            CheckPaused(0, "reload restores speed and initial frame");
            await Call("SmokeCancelFullCaptureAsync");
            Require(client.Read().Status == 3, "Cancelled capture kept the game running");
            Set("branchOriginal", originalModel);
            Set("probeStatus", null); // The UI cache must not decide whether the resident game is released.
            await Call("ClearAllSamplesAsync");
            Require(Get("session") is null && Get("branchOriginal") is null &&
                Get("nativeReplaySource") is null && client.Read().Status == 0,
                "Clearing samples left live playback or stale branch ownership");
            Set("session", originalModel);
            Set("cursor", 600);
            await Call("SeekPlaybackAsync", 600);
            var pendingCloseSeek = Call("SeekPlaybackAsync", 1800);
            window.Close();
            await pendingCloseSeek;
            var closeDeadline = Stopwatch.StartNew();
            while (!(bool)Get("shutdownReady")!)
            {
                if (closeDeadline.Elapsed.TotalSeconds > 30) throw new IOException("Close during seek did not finish");
                await Task.Delay(50);
            }
            log.Add(new { phase = "complete", frames = originalCount, originalModelPreserved = true,
                recordingRemoved = true, csvLiveMismatchBlocked = true,
                stressSeekRequests = 40, snapshotRecovery = true, captureCancellation = true,
                csvImportClearsBranch = true, sampleClearReleasesPlayback = true, closeDuringSeek = true });
            Console.WriteLine("PASS: real takeover/return, REP exit/restore, play/pause, rapid seeks, slider and node synchronization.");

            async Task WaitForTarget(int target)
            {
                var clock = Stopwatch.StartNew();
                while (Get("pendingSeekTarget") is not null || client.Read().Ordinal != target)
                {
                    if (clock.Elapsed.TotalSeconds > 30) throw new IOException("Node/slider target not reached.");
                    await Task.Delay(50);
                }
            }
            void CheckPaused(int target, string phase)
            {
                CallVoid("RefreshProbeStatus");
                var state = client.Read();
                Require(state.Status == 3 && state.Ordinal == target && (int)Get("cursor")! == target,
                    "Game/control position mismatch at " + phase);
                Require(state.EngineFrame == model.Frames[target].EngineFrame,
                    "Engine frame differs from the selected REP at " + phase);
                log.Add(new { phase, state.Ordinal, state.EngineFrame, status = state.Status, cursor = Get("cursor") });
            }
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            await GameDiscovery.CloseVerifiedGameAsync(process.Id, exe);
            if (window is not null)
            {
                typeof(MainWindow).GetField("shutdownReady", flags)!.SetValue(window, true);
                window.Close();
            }
            client?.Dispose();
            File.WriteAllText(Path.Combine(output, "playback-check.json"), JsonSerializer.Serialize(new {
                log, error = error?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) throw error;
    }
}
