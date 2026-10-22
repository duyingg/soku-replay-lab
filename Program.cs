using System.Windows;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SokuReplayLab;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashLog.Write(eventArgs.ExceptionObject, "进程未处理异常");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            CrashLog.Write(eventArgs.Exception, "未观察到的任务异常");
            eventArgs.SetObserved();
        };
        try { Run(args); }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "程序入口异常");
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--guarded-game-check")
        {
            using var gameProcess = GameDiscovery.Launch(args[1]);
            GameInstance? game = null;
            for (var attempt = 0; attempt < 30 && !gameProcess.HasExited; attempt++)
            {
                Thread.Sleep(500);
                game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == gameProcess.Id);
                if (GameDiscovery.InspectWindows().Any(x =>
                    x.Pid == gameProcess.Id && x.Title == "DirectX-Error")) break;
                if (game is not null && attempt >= 5) break;
            }
            if (!gameProcess.HasExited) Thread.Sleep(1500);
            game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == gameProcess.Id);
            var windows = GameDiscovery.InspectWindows().Where(x => x.Pid == gameProcess.Id).ToArray();
            try { GameDiscovery.CloseVerifiedGameAsync(gameProcess.Id, args[1])
                .GetAwaiter().GetResult(); }
            finally
            {
                if (!gameProcess.HasExited) gameProcess.Kill();
                gameProcess.WaitForExit(5000);
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                pid = gameProcess.Id, connected = game is not null,
                directXError = windows.Any(x => x.Title == "DirectX-Error"),
                closedWithApp = gameProcess.HasExited, windows
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-shutdown-game-check")
        {
            using var gameProcess = GameDiscovery.Launch(args[1]);
            try
            {
                GameInstance? game = null;
                for (var i = 0; i < 40 && !gameProcess.HasExited; i++)
                {
                    Thread.Sleep(500);
                    game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == gameProcess.Id);
                    if (game is not null) break;
                }
                if (game is null) throw new InvalidOperationException("游戏主窗口未就绪。");
                var shutdownApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new MainWindow();
                window.Loaded += (_, _) =>
                {
                    window.SmokeAttachForShutdown(game);
                    window.Dispatcher.BeginInvoke(new Action(window.Close));
                };
                shutdownApp.Run(window);
                File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                    game.Pid, gameClosedWithWindow = gameProcess.HasExited
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (!gameProcess.HasExited) Environment.ExitCode = 1;
            }
            finally
            {
                if (!gameProcess.HasExited) gameProcess.Kill();
                gameProcess.WaitForExit(5000);
            }
            return;
        }
        if (args.Length >= 4 && args[0] == "--escape-batch-live-check")
        {
            EscapeBatchLiveCheck.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 5 && args[0] == "--inplace-live-check")
        {
            InPlaceLiveCheck.RunAsync(args[1], args[2], args[3], args[4])
                .GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 5 && args[0] == "--ui-escape-repeat-check")
        {
            var trialCount = args.Length >= 6 ? int.Parse(args[5]) : 2;
            using var gameProcess = GameDiscovery.Launch(args[1]);
            try
            {
                GameInstance? game = null;
                for (var i = 0; i < 40 && !gameProcess.HasExited; i++)
                {
                    Thread.Sleep(500);
                    game = GameDiscovery.Find().FirstOrDefault(x => x.Pid == gameProcess.Id);
                    if (game is not null) break;
                }
                if (game is null) throw new InvalidOperationException("游戏主窗口未就绪。");
                using var attachedProbe = ProbeClient.AttachAsync(game,
                    Directory.GetCurrentDirectory()).GetAwaiter().GetResult();
                var replay = Path.GetFullPath(args[2]);
                var source = ReplayAnalyzer.Analyze(replay, ReplayHeader.Read(replay),
                    TraceReader.Read(Path.GetFullPath(args[3])),
                    "已验证的完整实机轨迹");
                var currentTrace = Path.Combine(Directory.GetCurrentDirectory(),
                    "trace_" + source.Header!.Hash + ".csv");
                attachedProbe.SendAsync(ProbeCommand.Load,
                    replayPath: replay, tracePath: currentTrace).GetAwaiter().GetResult();
                attachedProbe.SendAsync(ProbeCommand.Seek, target: 0)
                    .GetAwaiter().GetResult();
                var repeatApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new MainWindow();
                window.SmokeDisableEscapeWorker = args.Length >= 7 && args[6] == "--serial";
                var runs = new List<object>();
                object? playback = null;
                Exception? failure = null;
                window.Loaded += async (_, _) =>
                {
                    try
                    {
                        window.SmokeConnectResident(game, source);
                        window.SmokeNavigate(6);
                        for (var i = 0; i < 2; i++)
                        {
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            var batch = await window.SmokeEscapeBatchAsync(trialCount,
                                skip: args.Length >= 9 ? int.Parse(args[8]) : 0,
                                move: args.Length >= 8 ? args[7] : "4A");
                            runs.Add(new { batch.Completed, batch.Total,
                                batch.Success, batch.Failures, batch.Unresolved,
                                batch.ExecutionErrors, batch.Message, batch.Verdicts,
                                seconds = watch.Elapsed.TotalSeconds });
                        }
                        playback = await window.SmokeEscapeResultPlaybackAsync();
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { window.Close(); }
                };
                repeatApp.Run(window);
                File.WriteAllText(args[4], JsonSerializer.Serialize(new {
                    game.Pid, runs, playback, gameClosedWithWindow = gameProcess.HasExited,
                    error = failure?.ToString()
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (failure is not null || !gameProcess.HasExited)
                    Environment.ExitCode = 1;
            }
            finally
            {
                if (!gameProcess.HasExited) gameProcess.Kill();
                gameProcess.WaitForExit(5000);
            }
            return;
        }
        if (args.Length >= 4 && args[0] == "--full-replay-live-check")
        {
            FullReplayLiveCheck.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 3 && args[0] == "--fast-capture-live-check")
        {
            FullReplayLiveCheck.FastCaptureAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 3 && args[0] == "--snapshot-live-check")
        {
            SnapshotLiveCheck.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 4 && args[0] == "--snapshot-ui-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var replay = Path.GetFullPath(args[1]);
            var inputTrace = Path.GetFullPath(args[2]);
            var source = ReplayAnalyzer.Analyze(replay, ReplayHeader.Read(replay),
                TraceReader.Read(inputTrace));
            var beforeWindow = GameWindowLease.Inspect(game.Window);
            var snapshotApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            object? result = null;
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var attached = window.SmokeConnectResident(game, source);
                    var snapshot = await window.SmokeNativeSnapshotAsync();
                    result = new { attached.Attached, attached.HostHandle,
                        snapshot.Saved, snapshot.Restored, snapshot.SlotVisible,
                        snapshot.Message };
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            snapshotApp.Run(window);
            using var resident = ProbeClient.TryOpen(game.Pid);
            var afterWindow = GameWindowLease.Inspect(game.Window);
            File.WriteAllText(args[3], JsonSerializer.Serialize(new { result,
                afterProbe = resident?.Read(), beforeWindow, afterWindow,
                windowRestored = beforeWindow.EquivalentTo(afterWindow),
                error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 4 && args[0] == "--escape-batch-ui-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var residentProbe = ProbeClient.TryOpen(game.Pid);
            if (residentProbe is null)
            {
                var preflight = ProbePreflight.Inspect(game);
                if (!preflight.Passed)
                    throw new InvalidOperationException("只读预检未通过，未加载探针。");
                residentProbe = ProbeClient.AttachAsync(game,
                    Directory.GetCurrentDirectory()).GetAwaiter().GetResult();
            }
            using var testProbe = residentProbe;
            var replay = Path.GetFullPath(args[1]);
            var inputTrace = Path.GetFullPath(args[2]);
            var source = ReplayAnalyzer.Analyze(replay, ReplayHeader.Read(replay),
                TraceReader.Read(inputTrace));
            var beforeWindow = GameWindowLease.Inspect(game.Window);
            var batchUiApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            object? result = null;
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var attached = window.SmokeConnectResident(game, source);
                    window.SmokeNavigate(6);
                    var batch = await window.SmokeEscapeBatchAsync(
                        args.Length >= 5 ? int.Parse(args[4]) : 2);
                    result = new { attached.Attached, attached.HostHandle,
                        batch.Completed, batch.Total, batch.Success, batch.Failures,
                        batch.Unresolved, batch.ExecutionErrors,
                        batch.Message };
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            batchUiApp.Run(window);
            using var resident = ProbeClient.TryOpen(game.Pid);
            var afterWindow = GameWindowLease.Inspect(game.Window);
            File.WriteAllText(args[3], JsonSerializer.Serialize(new { result,
                afterProbe = resident?.Read(), beforeWindow, afterWindow,
                windowRestored = beforeWindow.EquivalentTo(afterWindow),
                error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 2 && args[0] == "--embed-check")
        {
            GameEmbedCheck.Run(args[1]);
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-live-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到游戏主窗口。");
            GameWindowState? InspectIfAccessible()
            {
                try { return GameWindowLease.Inspect(game.Window); }
                catch (InvalidOperationException) { return null; }
            }
            var session = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            var output = args[2];
            var smokeApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            var observations = new List<object>();
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    window.SmokeConnectResident(game, session);
                    await Task.Delay(500);
                    var replay = window.SmokeNavigate(0);
                    observations.Add(new { Page = 0, replay.Attached, replay.HostHandle,
                        Window = InspectIfAccessible() });
                    if (!replay.Attached)
                        throw new InvalidOperationException("游戏窗口未能嵌入；无法验证切页时宿主是否保持。");
                    var refreshed = window.SmokeNavigate(0);
                    observations.Add(new { Page = 0, refreshed.Attached, refreshed.HostHandle,
                        Window = InspectIfAccessible() });
                    if (replay.Attached && (!refreshed.Attached ||
                        replay.HostHandle != refreshed.HostHandle))
                        throw new InvalidOperationException("回放页刷新释放了游戏窗口宿主。");
                    var stats = window.SmokeNavigate(1);
                    observations.Add(new { Page = 1, stats.Attached, stats.HostHandle,
                        Window = InspectIfAccessible() });
                    var again = window.SmokeNavigate(0);
                    await Task.Delay(500);
                    observations.Add(new { Page = 0, again.Attached, again.HostHandle,
                        Window = InspectIfAccessible() });
                    if (replay.Attached && (!stats.Attached || !again.Attached ||
                        replay.HostHandle != stats.HostHandle ||
                        replay.HostHandle != again.HostHandle))
                        throw new InvalidOperationException("切页释放或重建了游戏窗口宿主。");
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            smokeApp.Run(window);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                game.Pid, observations, after = InspectIfAccessible(),
                error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) throw failure;
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-auto-import-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var before = GameWindowLease.Inspect(game.Window);
            var importApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            object? imported = null;
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try {
                    var result = await window.SmokeAutoImportAsync(game, args[1]);
                    imported = new { result.Source, result.Status, result.Ordinal,
                        result.FrameCount, result.Message };
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            importApp.Run(window);
            var after = GameWindowLease.Inspect(game.Window);
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                imported, before, after, windowRestored = before.EquivalentTo(after),
                error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-full-capture-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var before = GameWindowLease.Inspect(game.Window);
            var appCheck = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            object? result = null;
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try {
                    await window.SmokeAutoImportAsync(game, args[1]);
                    var captured = await window.SmokeFullCaptureAsync();
                    result = new { captured.FrameCount, captured.FinalOrdinal,
                        captured.Message };
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            appCheck.Run(window);
            var after = GameWindowLease.Inspect(game.Window);
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                result, windowRestored = before.EquivalentTo(after),
                error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-full-capture-cancel-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var before = GameWindowLease.Inspect(game.Window);
            var appCheck = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            object? result = null;
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try {
                    await window.SmokeAutoImportAsync(game, args[1]);
                    var canceled = await window.SmokeCancelFullCaptureAsync();
                    result = new { canceled.Ordinal, canceled.Message };
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            appCheck.Run(window);
            var after = GameWindowLease.Inspect(game.Window);
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                result, windowRestored = before.EquivalentTo(after),
                error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 4 && args[0] == "--ui-escape-move-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var appCheck = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            object? result = null;
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try {
                    await window.SmokeAutoImportAsync(game, args[1]);
                    var trial = await window.SmokeEscapeMoveAsync(args[2],
                        args.Length >= 5 ? int.Parse(args[4]) : 0);
                    result = new { trial.Anchor, trial.Defender, trial.Move,
                        trial.Result, trial.Reason, trial.MatchedFrames, trial.End };
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            appCheck.Run(window);
            File.WriteAllText(args[3], JsonSerializer.Serialize(new {
                result, error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 4 && args[0] == "--ui-rep-switch-check")
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var before = GameWindowLease.Inspect(game.Window);
            var switchApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            var loaded = new List<object>();
            Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try {
                    var first = await window.SmokeAutoImportAsync(game, args[1]);
                    loaded.Add(new { first.Source, first.Status, first.Ordinal,
                        first.FrameCount, first.Message });
                    var second = await window.SmokeImportAnotherAsync(args[2]);
                    loaded.Add(new { second.Source, second.Status, second.Ordinal,
                        second.FrameCount, second.Message });
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            switchApp.Run(window);
            var after = GameWindowLease.Inspect(game.Window);
            File.WriteAllText(args[3], JsonSerializer.Serialize(new {
                game.Pid, loaded, windowRestored = before.EquivalentTo(after),
                error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failure is not null) Environment.ExitCode = 1;
            return;
        }
        if (args.Length >= 4 && args[0] == "--probe-live-check")
        {
            ProbeLiveCheck.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 4 && args[0] == "--branch-live-check")
        {
            BranchLiveCheck.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 3 && args[0] == "--escape-live-check")
        {
            EscapeLiveCheck.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 2 && args[0] == "--probe-preflight")
        {
            var game = GameDiscovery.Find().FirstOrDefault();
            File.WriteAllText(args[1], JsonSerializer.Serialize(
                game is null ? null : ProbePreflight.Inspect(game),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--card-check")
        {
            var frames = TraceReader.Read(args[1]);
            File.WriteAllText(args[2], JsonSerializer.Serialize(CardTimeline.Build(frames),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--gap-check")
        {
            var frames = TraceReader.Read(args[1]);
            File.WriteAllText(args[2], JsonSerializer.Serialize(PressureGapDetector.Detect(frames),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--pressure-check")
        {
            var frames = TraceReader.Read(args[1]);
            File.WriteAllText(args[2], JsonSerializer.Serialize(
                ReplayAnalyzer.Analyze(args[1], null, frames).Pressures,
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--escape-attempt-check")
        {
            var frames = TraceReader.Read(args[1]);
            var session = ReplayAnalyzer.Analyze(args[1], null, frames);
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                frames = frames.Count,
                pressures = session.Pressures.Count,
                attempts = session.EscapeAttempts.Count,
                success = session.EscapeAttempts.Count(x => x.Outcome == "成功"),
                failure = session.EscapeAttempts.Count(x => x.Outcome == "失败"),
                unresolved = session.EscapeAttempts.Count(x => x.Outcome == "待判定"),
                successes = session.EscapeAttempts.Where(x => x.Outcome == "成功")
                    .Select(x => new { x.Start, x.End, x.Defender, x.PressureStart,
                        reason = session.Pressures.FirstOrDefault(p =>
                            p.Round == x.Round && p.Defender == x.Defender &&
                            p.Start == x.PressureStart)?.EndReason }),
                examples = session.EscapeAttempts.Take(20)
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--escape-plan-check")
        {
            var frames = TraceReader.Read(args[1]);
            var gap = PressureGapDetector.Detect(frames).First(x =>
                x.Start >= 32 && x.Start + 90 < frames.Count);
            var character = frames[gap.Start].Side(gap.Defender).Character;
            var move = EscapeTrial.AvailableMoves(character)
                .First(x => x.Length > 2 && x.EndsWith('B'));
            var masks = EscapeTrial.Plan(frames, gap, move);
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                gap, character, move, prefix = masks.Take(8), count = masks.Length
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 1 && args[0] == "--escape-judge-regression")
        {
            static Fighter FighterAt(int hp = 10000, int action = 0, int comboDamage = 0) =>
                new(0, 0, 0, action, 1, hp, 1000, 0, 0, comboDamage,
                    0, 0, 0, 0, 0, 0, 0);
            var baseline = Enumerable.Range(0, 92).Select(i =>
                new Frame(i, i, 2, 0, 0, FighterAt(),
                    FighterAt(i >= 10 ? 9800 : 10000, i >= 10 ? 50 : 0)))
                .ToArray();
            var sameHit = baseline.Select((frame, i) => i >= 5
                ? frame with { P1 = FighterAt(action: 300) } : frame).ToArray();
            var additionalHit = sameHit.Select((frame, i) => i >= 10
                ? frame with { P2 = FighterAt(9700, 50) } : frame).ToArray();
            var unchanged = EscapeTrial.Judge(baseline, baseline, 0, 0, "4A", 90);
            var coincident = EscapeTrial.Judge(baseline, sameHit, 0, 0, "4A", 90);
            var gained = EscapeTrial.Judge(baseline, additionalHit, 0, 0, "4A", 90);
            if (unchanged.Result != "未判定" || coincident.Result == "成功" ||
                gained.Result != "成功")
                throw new InvalidDataException("逃压分支与原回放的差分判定回归失败。");
            var checks = new List<object>();
            void Check(string name, bool passed)
            {
                checks.Add(new { name, passed });
                if (!passed) throw new InvalidDataException($"逃压回归失败：{name}");
            }
            Check("same-state-is-inconclusive", unchanged.Result == "未判定");
            Check("baseline-damage-is-not-new-hit", coincident.Result != "成功");
            Check("requested-attack-new-hit", gained.Result == "成功");
            var wrongMove = additionalHit.Select((frame, i) => i >= 5
                ? frame with { P1 = FighterAt(action: 400) } : frame).ToArray();
            Check("unrelated-attack-cannot-credit-4A",
                EscapeTrial.Judge(baseline, wrongMove, 0, 0, "4A", 90).Result != "成功");
            var trade = additionalHit.Select((frame, i) => i >= 20
                ? frame with { P1 = FighterAt(9990, 50) } : frame).ToArray();
            var worseBaseline = baseline.Select((frame, i) => i >= 20
                ? frame with { P1 = FighterAt(9000, 50) } : frame).ToArray();
            Check("taking-less-damage-is-still-hit",
                EscapeTrial.Judge(worseBaseline, trade, 0, 0, "4A", 90).Result == "失败");
            var idle = Enumerable.Range(0, 100).Select(i =>
                new Frame(i, i, 2, 0, 0, FighterAt(), FighterAt())).ToArray();
            var jump = idle.Select((frame, i) => i is >= 5 and <= 10
                ? frame with { P1 = FighterAt(action: 6) } : frame).ToArray();
            Check("jump-with-safe-tail",
                EscapeTrial.Judge(idle, jump, 0, 0, "8", 90).Result == "成功");
            var reblocked = jump.Select((frame, i) => i >= 40
                ? frame with { P1 = FighterAt(action: 150) } : frame).ToArray();
            Check("jump-reblocked-is-not-escape",
                EscapeTrial.Judge(idle, reblocked, 0, 0, "8", 90).Result == "失败");
            var lateJump = idle.Select((frame, i) => i is >= 5 and <= 85
                ? frame with { P1 = FighterAt(action: 6) } : frame).ToArray();
            Check("late-recovery-needs-observation",
                EscapeTrial.Judge(idle, lateJump, 0, 0, "8", 90).Result == "未判定");
            Check("short-trace-is-inconclusive",
                EscapeTrial.Judge(idle, jump.Take(50).ToArray(), 0, 0, "8", 90).Result == "未判定");
            var wrongStart = jump.ToArray();
            wrongStart[0] = wrongStart[0] with { P1 = FighterAt(9000) };
            Check("different-anchor-is-inconclusive",
                EscapeTrial.Judge(idle, wrongStart, 0, 0, "8", 90).Result == "未判定");
            var gap = new PressureGap(0, 0, 40, 48, 8, "regression", false);
            Check("input-at-first-free-frame", EscapeTrial.Anchor(gap, "4A") + 1 == gap.Start);
            var plannedFrames = Enumerable.Range(0, 150).Select(i =>
                new Frame(i, i, 2, 0, 0, FighterAt() with { Direction = 1, Input = 0x200 },
                    FighterAt())).ToArray();
            var plan = EscapeTrial.Plan(plannedFrames, gap, "4A");
            Check("no-original-attack-tail", plan[0] == 0x14 && plan.Skip(1).All(mask => mask == 6));
            var hitDuringDash = plannedFrames.Select((frame, i) => i >= 50
                ? frame with { P1 = frame.P1 with { Hp = 9900, Action = 50 } } : frame).ToArray();
            Check("skip-release-after-earlier-hit",
                EscapeTrial.SafeDashReleaseOffsets(hitDuringDash, 39, 0, 90).All(delay => 39 + delay < 50));
            if (args.Length >= 2)
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { checks },
                    new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 1 && args[0] == "--escape-attempt-regression")
        {
            static Frame FrameAt(int i, bool performed) => new(i, i, 2, 0, 0,
                new Fighter(0, 0, 0, performed && i is >= 10 and < 15 ? 300 :
                    i < 10 ? 150 : 0, 1, 10000, 1000, i == 5 ? 0x10 : 0,
                    0, 0, 0, 0, 0, 0, 0, 0, 0),
                new Fighter(0, 0, 0, 0, 1, 10000, 1000, 0,
                    0, 0, 0, 0, 0, 0, 0, 0, 0));
            var pressure = new Pressure(0, 50, 0, 1, 0, 1, 1, "300", "脱离");
            var ignored = EscapeAttemptDetector.Detect(
                Enumerable.Range(0, 92).Select(i => FrameAt(i, false)).ToArray(), [pressure]);
            var activated = EscapeAttemptDetector.Detect(
                Enumerable.Range(0, 92).Select(i => FrameAt(i, true)).ToArray(), [pressure]);
            var confirmed = EscapeAttemptDetector.Detect(
                Enumerable.Range(0, 92).Select(i => FrameAt(i, true)).ToArray(),
                [pressure with { EndReason = "被反击" }]);
            var singleD = Enumerable.Range(0, 92).Select(i => FrameAt(i, false) with {
                P1 = FrameAt(i, false).P1 with { Input = i == 5 ? 0x82 : 0 }
            }).ToArray();
            var doubleD = singleD.Select((frame, i) => i == 7
                ? frame with { P1 = frame.P1 with { Input = 0x82 } } : frame).ToArray();
            if (ignored.Count != 1 || ignored[0].Outcome != "待判定" ||
                activated.Count != 1 || activated[0].Outcome != "待判定" ||
                confirmed.Count != 1 || confirmed[0].Outcome != "成功" ||
                EscapeAttemptDetector.Detect(singleD, [pressure]).Count != 0 ||
                EscapeAttemptDetector.Detect(doubleD, [pressure]).Count != 1)
                throw new InvalidDataException("逃压统计的输入与压制归因回归失败。");
            return;
        }
        if (args.Length >= 3 && args[0] == "--escape-batch-plan-check")
        {
            var session = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            var move = args.Length >= 4 ? args[3] : "4A";
            var gaps = EscapeTrial.EligibleGaps(session, -1, move, 1);
            var planned = gaps.Select(gap =>
            {
                try
                {
                    var masks = EscapeTrial.Plan(session.Frames, gap, move);
                    return new { gap.Start, gap.Defender, gap.Length, Anchor = EscapeTrial.Anchor(gap, move),
                        Frames = masks.Length, Error = (string?)null };
                }
                catch (Exception ex)
                {
                    return new { gap.Start, gap.Defender, gap.Length, Anchor = EscapeTrial.Anchor(gap, move),
                        Frames = 0, Error = (string?)ex.Message };
                }
            }).ToArray();
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                move, candidates = planned.Length, planned = planned.Count(x => x.Error is null),
                results = planned
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 8 && args[0] == "--escape-judge-check")
        {
            var baseline = TraceReader.Read(args[1]);
            var branch = TraceReader.Read(args[2]);
            var verdict = EscapeTrial.Judge(baseline, branch,
                int.Parse(args[3]), int.Parse(args[4]), args[5], int.Parse(args[6]));
            File.WriteAllText(args[7], JsonSerializer.Serialize(verdict,
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 2 && args[0] == "--desktop-check")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(DesktopDiagnostics.Inspect(),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 2 && args[0] == "--game-diagnostics")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(GameDiscovery.InspectWindows(),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 1 && args[0] == "--probe-protocol-check")
        {
            ProbeClient.ProtocolSmokeAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 2 && args[0] == "--live-read")
        {
            var game = GameDiscovery.FindProcesses().FirstOrDefault();
            LiveGameStatus? status = null;
            bool memoryReadable = false;
            uint battlePointer = 0;
            if (game is not null)
            {
                using var reader = new LiveGameReader(game);
                memoryReadable = reader.TryReadBattlePointer(out battlePointer);
                reader.TryRead(out status);
            }
            File.WriteAllText(args[1], JsonSerializer.Serialize(new {
                found = game is not null, pid = game?.Pid,
                windowFound = game?.Window != 0, memoryReadable,
                battlePointer = memoryReadable ? $"0x{battlePointer:X8}" : null,
                status
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 3 && args[0] == "--export-events")
        {
            var session = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            int? side = args.Length >= 4 && int.TryParse(args[3], out var selectedSide)
                ? selectedSide : null;
            var kind = args.Length >= 5 ? args[4] : null;
            var events = session.Events.Where(item =>
                (side is null || item.Side == side) &&
                (kind is null || item.Kind == kind));
            EventCsvExporter.Export(args[2], events);
            return;
        }
        if (args.Length >= 3 && args[0] == "--interpret-check")
        {
            var session = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            var minimum = args.Length >= 4 && int.TryParse(args[3], out var parsed)
                ? Math.Clamp(parsed, 1, 3) : 1;
            var tips = ReplayInterpretation.Evaluate(session, minimum);
            File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                session.Source, session.TraceOrigin, Frames = session.Frames.Count,
                MinimumPressureMoves = minimum, Tips = tips
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 2 && args[0] == "--interpret-rule-check")
        {
            ReplayInterpretationRuleCheck.Run(args[1]);
            return;
        }
        if (args.Length >= 3 && args[0] == "--export-stats")
        {
            var session = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            var side = args.Length >= 4 && int.TryParse(args[3], out var selectedSide)
                ? selectedSide : -1;
            var round = args.Length >= 5 && int.TryParse(args[4], out var selectedRound)
                ? selectedRound : -1;
            var minMoves = args.Length >= 6 && int.TryParse(args[5], out var selectedMinimum)
                ? selectedMinimum : 1;
            StatsCsvExporter.Export(args[2], session, side, round, minMoves);
            return;
        }
        if (args.Length >= 4 && args[0] == "--export-multi-stats")
        {
            var root = Directory.GetCurrentDirectory();
            var sessions = args.Skip(2).Select(path => SessionLoader.Load(path, root)).ToArray();
            StatsCsvExporter.ExportMany(args[1], sessions);
            return;
        }
        if (args.Length >= 1 && args[0] == "--game-check")
        {
            var output = args.Length >= 2 ? args[1] :
                Path.Combine(Directory.GetCurrentDirectory(), "game_discovery_check.json");
            File.WriteAllText(output,
                JsonSerializer.Serialize(GameDiscovery.Find().Select(x => new {
                    x.Pid, Window = x.Window.ToInt64(), x.Exe, x.Title
                }), new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 2 && args[0] == "--self-check")
        {
            var root = Directory.GetCurrentDirectory();
            var session = SessionLoader.Load(args[1], root);
            if (session.Total(0).Damage != session.Total(1).TakenDamage ||
                session.Total(1).Damage != session.Total(0).TakenDamage)
                throw new InvalidDataException("双方实际损血统计不守恒。");
            for (var side = 0; side < 2; side++)
            {
                var own = session.Total(side);
                var opponent = session.Total(1 - side);
                if (own.ChipDamage != opponent.TakenChipDamage ||
                    own.WeatherDamage != opponent.TakenWeatherDamage ||
                    own.TyphoonDamage != opponent.TakenTyphoonDamage ||
                    own.AttackDamage != opponent.TakenAttackDamage)
                    throw new InvalidDataException("双方损血分类统计不守恒。");
            }
            var result = new {
                source = session.Source, traceOrigin = session.TraceOrigin,
                frames = session.Frames.Count,
                rounds = session.RoundCount, combos = session.Combos.Count,
                pressures = session.Pressures.Count, events = session.Events.Count,
                projectileOpeners = session.Events.Where(x => x.Kind == "被弹幕起手")
                    .Select(x => new { x.Ordinal, x.Round, x.Side, x.Detail }),
                sameDirectionHits = session.Events.Where(x => x.Kind == "同向被攻击")
                    .Select(x => new { x.Ordinal, x.Round, x.Side }),
                p1Damage = session.Total(0).Damage, p2Damage = session.Total(1).Damage,
                p1Taken = session.Total(0).TakenDamage, p2Taken = session.Total(1).TakenDamage,
                byRound = session.Stats.Select(x => new {
                    x.Side, x.Round, x.Damage, x.ChipDamage, x.TakenChipDamage,
                    x.WeatherDamage, x.TakenWeatherDamage, x.TyphoonDamage,
                    x.TakenTyphoonDamage, x.AttackDamage, x.TakenAttackDamage,
                    x.Inputs, x.EffectiveInputs, x.RedundantInputs,
                    x.ForwardDashes, x.BackDashes, x.DdAttempts, x.DdSuccess,
                    x.DDirections, x.HighJumps, x.SmallJumps,
                    x.GuardFrames, x.DefenseEpisodes, x.LowSpiritFrames,
                    x.StandGuards, x.CrouchGuards, x.WrongGuards, x.GuardLevelKnown,
                    x.ChargedMixupsFaced, x.ChargedMixupsBlocked,
                    x.ChargedMixupsWrongGuard, x.ChargedMixupsKnown,
                    x.LongestLowSpiritFrames, x.EmptySpirit, x.BrokenOrbs,
                    x.Techs, x.TechTrapped, x.TechChases, x.TechChaseSuccess,
                    x.ReversalsUsed, x.ReversalsHit, x.ReversalsKnown,
                    x.TakenProjectileOpeners, x.ProjectileOpenerKnown,
                    x.SameDirectionHits,
                    x.StartupPunished, x.RecoveryPunished, x.ReversalPunished, x.PunishPhaseKnown,
                    x.LandingCancels, x.LandingCancelsKnown,
                    x.LimitBreaks, x.LowDamageHits, x.ProjectileLowDamageHits,
                    x.TakenLowDamageHits, x.ProjectileLowDamageKnown,
                    x.LowDamageSpellHits, x.LowDamageSpellOnlyHits,
                    x.ExtremeMeleeHits, x.TakenExtremeMeleeHits,
                    x.ExtremeMeleeKnown, x.TakenExtremeMeleeKnown,
                    x.WakeupPressureFailures, x.WakeupPressures,
                    x.WakeupPressureSuccess, x.WakeupPressureInterrupted,
                    x.WakeupPressureReversed, x.UnfinishedHits,
                    x.PressureInterrupted, x.PressureReversed, x.PressureMashCatches,
                    x.CaughtMashing, x.PressureDdCatches,
                    x.EscapeAttempts, x.EscapeSuccess, x.EscapeFailed,
                    x.EscapeUnresolved,
                    x.CircleOrDownHits, x.CounterHits, x.TakenCounterHits, x.CounterKnown
                })
            };
            var output = args.Length >= 3 ? args[2] : Path.Combine(root, "new_self_check.json");
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length >= 4 && args[0] == "--simulation-trace-check")
        {
            var names = ("frame,match_state,round,weather," +
                "p1_char,p1_x,p1_y,p1_action,p1_action_frame,p1_hp,p1_spirit,p1_input," +
                "p1_combo_damage,p1_card_count,p1_card_gauge,p1_hit_stop,p1_collision_type,p1_hand_ids,p1_used_ids," +
                "p2_char,p2_x,p2_y,p2_action,p2_action_frame,p2_hp,p2_spirit,p2_input," +
                "p2_combo_damage,p2_card_count,p2_card_gauge,p2_hit_stop,p2_collision_type,p2_hand_ids,p2_used_ids")
                .Split(',');
            var full = TraceReader.Read(args[1]);
            var start = Math.Clamp(int.Parse(args[2]), 1, full.Count - 1);
            var temporary = Path.GetTempFileName();
            try
            {
                using (var reader = new StreamReader(args[1]))
                using (var writer = new StreamWriter(temporary))
                {
                    var header = (reader.ReadLine() ?? throw new InvalidDataException()).Split(',');
                    var columns = names.Select(name => Array.IndexOf(header, name)).ToArray();
                    if (columns.Any(column => column < 0))
                        throw new InvalidDataException("完整轨迹缺少精简轨迹字段。");
                    writer.WriteLine(string.Join(',', names));
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        var cells = line.Split(',');
                        writer.WriteLine(string.Join(',', columns.Select(column => cells[column])));
                    }
                }
                var reduced = TraceReader.ReadWithPrefix(temporary, full, start);
                if (full.Count != reduced.Count) throw new InvalidDataException("轨迹长度不同。");
                for (var i = start; i < full.Count; i++)
                {
                    var before = full[i];
                    var after = reduced[i];
                    if (before.EngineFrame != after.EngineFrame ||
                        before.MatchState != after.MatchState || before.Round != after.Round ||
                        before.Weather != after.Weather ||
                        !SameFighter(before.P1, after.P1) || !SameFighter(before.P2, after.P2))
                        throw new InvalidDataException($"精简轨迹第 {i} 帧判定字段不同。");
                }
                static bool SameFighter(Fighter a, Fighter b) =>
                    a.Character == b.Character && a.X == b.X && a.Y == b.Y &&
                    a.Action == b.Action && a.ActionFrame == b.ActionFrame &&
                    a.Hp == b.Hp && a.Spirit == b.Spirit && a.Input == b.Input &&
                    a.ComboDamage == b.ComboDamage && a.CardCount == b.CardCount &&
                    a.CardGauge == b.CardGauge && a.HitStop == b.HitStop &&
                    a.Collision == b.Collision &&
                    (a.HandIds is null && b.HandIds is null ||
                     a.HandIds is not null && b.HandIds is not null &&
                     a.HandIds.SequenceEqual(b.HandIds)) &&
                    (a.UsedIds is null && b.UsedIds is null ||
                     a.UsedIds is not null && b.UsedIds is not null &&
                     a.UsedIds.SequenceEqual(b.UsedIds));
                var originalCards = CardTimeline.Build(full.Skip(start - 1).ToArray());
                var reducedCards = CardTimeline.Build(reduced.Skip(start - 1).ToArray());
                if (!originalCards.SequenceEqual(reducedCards))
                    throw new InvalidDataException("精简轨迹的用卡判定不同。");
                File.WriteAllText(args[3], JsonSerializer.Serialize(new {
                    frames = full.Count, start, verifiedCards = originalCards.Count
                }));
            }
            finally { File.Delete(temporary); }
            return;
        }
        if (args.Length >= 4 && args[0] == "--trace-prefix-check")
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var full = TraceReader.Read(args[1]);
            var fullMs = timer.ElapsedMilliseconds;
            var firstChanged = Math.Clamp(int.Parse(args[2]), 0, full.Count);
            timer.Restart();
            var partial = TraceReader.ReadWithPrefix(args[1], full, firstChanged);
            var partialMs = timer.ElapsedMilliseconds;
            if (partial.Count != full.Count ||
                Enumerable.Range(0, firstChanged).Any(i => !ReferenceEquals(full[i], partial[i])) ||
                Enumerable.Range(firstChanged, full.Count - firstChanged).Any(i =>
                    JsonSerializer.Serialize(full[i]) != JsonSerializer.Serialize(partial[i])))
                throw new InvalidDataException("复用前缀后，轨迹与完整解析不一致。");
            File.WriteAllText(args[3], JsonSerializer.Serialize(new {
                frames = full.Count, firstChanged, fullMs, partialMs
            }));
            return;
        }
        if (args.Length >= 1 && args[0] == "--replay-workspace-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var layouts = new List<ReplayPanelLayout>();
            var workspace = new ReplayWorkspace((_, layout) => layouts.Add(layout));
            workspace.AddPanel("test", "面板", new TextBlock { Text = "内容" },
                250, 180, _ => new ReplayPanelLayout(100, 100, 400, 300), null,
                Brushes.White, Brushes.Gray, Brushes.DarkSlateGray, Brushes.White);
            var window = new Window { Width = 900, Height = 700, Content = workspace };
            Exception? failure = null;
            window.Loaded += (_, _) => {
                try
                {
                    Thumb Find(Func<Thumb, bool> predicate) => Descendants(workspace)
                        .OfType<Thumb>().First(predicate);
                    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
                    {
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                        {
                            var child = VisualTreeHelper.GetChild(parent, i);
                            yield return child;
                            foreach (var descendant in Descendants(child)) yield return descendant;
                        }
                    }
                    static void Drag(Thumb thumb, double dx, double dy)
                    {
                        thumb.RaiseEvent(new DragDeltaEventArgs(dx, dy) {
                            RoutedEvent = Thumb.DragDeltaEvent
                        });
                        thumb.RaiseEvent(new DragCompletedEventArgs(dx, dy, false) {
                            RoutedEvent = Thumb.DragCompletedEvent
                        });
                    }
                    var move = Find(x => x.Cursor == Cursors.SizeAll);
                    var corner = Find(x => x.Width == 22 && x.Height == 22);
                    var left = Find(x => x.Cursor == Cursors.SizeWE &&
                        x.HorizontalAlignment == HorizontalAlignment.Left);
                    var top = Find(x => x.Cursor == Cursors.SizeNS &&
                        x.VerticalAlignment == VerticalAlignment.Top);
                    Drag(move, 50, 30);
                    Drag(corner, 60, 50);
                    Drag(left, 30, 0);
                    Drag(top, 0, 20);
                    var actual = workspace.LayoutOf("test");
                    if (actual != new ReplayPanelLayout(180, 150, 430, 330) ||
                        layouts.Count != 4 || layouts[^1] != actual)
                        throw new InvalidOperationException($"面板操作未按像素更新或保存：{actual}");
                    Drag(corner, -1000, -1000);
                    var minimum = workspace.LayoutOf("test");
                    if (minimum.Width != 250 || minimum.Height != 180)
                        throw new InvalidOperationException($"面板最小尺寸失效：{minimum}");
                }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            checkApp.Run(window);
            if (failure is not null) throw failure;
            return;
        }
        if (args.Length >= 2 && args[0] == "--ui-check")
        {
            _ = new Application();
            var window = new MainWindow();
            window.Smoke(SessionLoader.Load(args[1], Directory.GetCurrentDirectory()),
                args.Length >= 3 ? SessionLoader.Load(args[2], Directory.GetCurrentDirectory()) : null);
            window.Close();
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-navigation-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            var test = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            Exception? failure = null;
            window.Loaded += (_, _) => {
                try
                {
                    var result = window.SmokeNavigationCache(test);
                    File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                        result.FirstStatsMs, result.FirstMultiMs, result.FirstCurvesMs,
                        result.CachedStatsMs, result.CachedMultiMs, result.CachedCurvesMs,
                        result.ReusedStats, result.ReusedMulti, result.ReusedCurves,
                        result.InvalidatedAfterFilter
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    if (!result.ReusedStats || !result.ReusedMulti || !result.ReusedCurves ||
                        !result.InvalidatedAfterFilter)
                        throw new InvalidOperationException("页面缓存复用或筛选失效。");
                }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            checkApp.Run(window);
            if (failure is not null) throw failure;
            return;
        }
        if (args.Length >= 2 && args[0] == "--ui-scroll-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            var test = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            Exception? failure = null;
            window.Loaded += async (_, _) => {
                try { await window.SmokeTableWheelAsync(test); }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            checkApp.Run(window);
            if (failure is not null) throw failure;
            return;
        }
        if (args.Length >= 2 && args[0] == "--ui-timeline-check")
        {
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow();
            var test = SessionLoader.Load(args[1], Directory.GetCurrentDirectory());
            Exception? failure = null;
            window.Loaded += async (_, _) => {
                try { await window.SmokeTimelineClickAsync(test); }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            checkApp.Run(window);
            if (failure is not null) throw failure;
            return;
        }
        if (args.Length >= 3 && args[0] == "--preview")
        {
            _ = new Application();
            var window = new MainWindow();
            var page = args.Length >= 4 && int.TryParse(args[3], out var selected) ? selected : 0;
            window.ExportPreview(SessionLoader.Load(args[1], Directory.GetCurrentDirectory()),
                args[2], page, args.Length >= 5 ? args[4] : null,
                args.Length >= 6 && double.TryParse(args[5], out var offset) ? offset : 0);
            window.Close();
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, eventArgs) =>
        {
            CrashLog.Write(eventArgs.Exception, "界面未处理异常");
            eventArgs.Handled = true;
            app.Shutdown(1);
        };
        app.Run(new MainWindow());
    }
}

internal static class CrashLog
{
    private static readonly object Gate = new();

    internal static void Write(object? failure, string source)
    {
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "replaylab_crash.log");
            var record = $"[{DateTimeOffset.Now:O}] {source} PID={Environment.ProcessId}" +
                         Environment.NewLine + failure + Environment.NewLine + Environment.NewLine;
            lock (Gate) File.AppendAllText(path, record, Encoding.UTF8);
        }
        catch { /* Failure reporting must not mask the original exception. */ }
    }
}

internal static class ConnectionTrace
{
    private static readonly object Gate = new();

    internal static void Write(string phase, int pid = 0, string? detail = null)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "replaylab_connection.log");
            var entry = $"[{DateTimeOffset.Now:O}] app={Environment.ProcessId} game={pid} {phase}" +
                (detail is null ? "" : $" {detail}") + Environment.NewLine;
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) return;
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
        }
        catch { /* Diagnostics must never break connection. */ }
    }
}
