using System.IO;
using System.Text.Json;

namespace SokuReplayLab;

public static class ProbeLiveCheck
{
    public static async Task RunAsync(string replay, string reportPath, string tracePath)
    {
        var steps = new List<object>();
        var capturePath = Path.GetFullPath(tracePath) + ".capture.csv";
        ProbeClient? client = null;
        string? error = null;
        void Record(string name, ProbeSnapshot? snapshot = null)
        {
            steps.Add(new { name, at = DateTimeOffset.Now, snapshot });
            Save();
        }
        void Save()
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new {
                replay, tracePath, capturePath, steps, error,
                traceExists = File.Exists(tracePath),
                traceLength = File.Exists(tracePath) ? new FileInfo(tracePath).Length : 0
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            replay = Path.GetFullPath(replay);
            reportPath = Path.GetFullPath(reportPath);
            tracePath = Path.GetFullPath(tracePath);
            if (!File.Exists(replay)) throw new FileNotFoundException("测试 REP 不存在。", replay);
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证的游戏主窗口。");
            var preflight = ProbePreflight.Inspect(game);
            var safeResident = preflight.LoadedProbeModules.Count == 1 &&
                preflight.LoadedProbeModules[0].Equals("NativeProbe.dll", StringComparison.OrdinalIgnoreCase);
            if (!preflight.ExecutableVerified || !preflight.MainWindowVerified ||
                !preflight.PeHeaderReadable || !preflight.FunctionsReadable ||
                !preflight.ReplayManagerReadable || !preflight.FramePacingReadable ||
                preflight.Slots.Any(x => !x.PointerReadable) ||
                (preflight.LoadedProbeModules.Count > 0 && !safeResident))
                throw new InvalidOperationException("只读预检未通过。" + JsonSerializer.Serialize(preflight));
            Record("preflight");
            client = await ProbeClient.AttachAsync(game, Directory.GetCurrentDirectory());
            Record("attached", client.Read());
            Record("loaded", await client.SendAsync(ProbeCommand.Load,
                replayPath: replay, tracePath: tracePath));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
            while (client.Read().Ordinal < 600 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100);
            Record("advanced", client.Read());
            if (client.Read().Ordinal < 600)
                throw new TimeoutException("回放载入后未推进到第 600 帧。");
            Record("paused", await client.SendAsync(ProbeCommand.Pause));
            var paused = client.Read().Ordinal;
            var captured = TraceReader.Read(tracePath);
            if (captured.Count < 500 || !captured.Any(x => x.MatchState != 0))
                throw new InvalidDataException("采集轨迹缺少实际对局帧。");
            File.Copy(tracePath, capturePath, overwrite: true);
            Record($"captured-{captured.Count}", client.Read());
            await Task.Delay(250);
            Record("pause-held", client.Read());
            if (client.Read().Ordinal != paused)
                throw new InvalidDataException("暂停后回放仍在推进。");
            Record("seek-forward", await client.SendAsync(ProbeCommand.Seek,
                target: paused + 30));
            Record("seek-backward", await client.SendAsync(ProbeCommand.Seek,
                target: 10));
            Record("played", await client.SendAsync(ProbeCommand.Play));
            await Task.Delay(250);
            Record("play-advanced", client.Read());
            Record("speed-2x", await client.SendAsync(ProbeCommand.Speed, target: 200));
            var speedStart = client.Read().Ordinal;
            await Task.Delay(250);
            Record("speed-advanced", client.Read());
            if (client.Read().Ordinal <= speedStart)
                throw new InvalidDataException("调速后回放没有推进。");
            foreach (var speed in new[] { 50, 75, 100, 150, 200 })
            {
                var before = client.Read().Ordinal;
                Record($"speed-{speed}", await client.SendAsync(ProbeCommand.Speed, target: speed));
                await Task.Delay(250);
                if (client.Read().Ordinal <= before)
                    throw new InvalidDataException($"{speed}% 调速后回放没有推进。");
            }
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Record("error", client is null ? null : SafeRead(client));
        }
        finally
        {
            if (client is not null)
            {
                try { Record("released", await client.ReleaseAsync()); }
                catch (Exception exception) {
                    steps.Add(new { name = "release-error", at = DateTimeOffset.Now,
                        snapshot = (ProbeSnapshot?)null, message = exception.ToString() });
                }
                client.Dispose();
            }
            if (!File.Exists(capturePath) && File.Exists(tracePath))
            {
                try { File.Copy(tracePath, capturePath, overwrite: true); }
                catch (IOException) { /* Preserve the original diagnostic failure. */ }
            }
            Save();
        }
    }

    private static ProbeSnapshot? SafeRead(ProbeClient client)
    {
        try { return client.Read(); }
        catch { return null; }
    }
}
