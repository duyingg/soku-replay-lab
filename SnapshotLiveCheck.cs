using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SokuReplayLab;

internal static class SnapshotLiveCheck
{
    internal static async Task RunAsync(string replay, string output)
    {
        replay = Path.GetFullPath(replay);
        output = Path.GetFullPath(output);
        var root = Directory.GetCurrentDirectory();
        var steps = new List<object>();
        Exception? failure = null;
        ProbeClient? probe = null;
        try
        {
            var game = GameDiscovery.Find().FirstOrDefault()
                ?? throw new InvalidOperationException("未找到已验证游戏主窗口。");
            var preflight = ProbePreflight.Inspect(game);
            steps.Add(new { name = "preflight", preflight });
            using var resident = ProbeClient.TryOpen(game.Pid);
            if (!preflight.Passed && resident is null)
                throw new InvalidOperationException("只读预检未通过。");
            probe = await ProbeClient.AttachAsync(game, root);
            var trace = Path.Combine(root, "snapshot_live_trace.csv");
            var loaded = await probe.SendAsync(ProbeCommand.Load,
                replayPath: replay, tracePath: trace);
            steps.Add(new { name = "loaded", loaded });
            var start = await probe.SendAsync(ProbeCommand.Seek, target: 400);
            steps.Add(new { name = "start", start });
            if (start.Ordinal != 400 || start.Status != 3)
                throw new InvalidDataException("无法在第 400 帧暂停。");
            var saved = await probe.SendAsync(ProbeCommand.SaveSnapshot, target: start.Ordinal);
            steps.Add(new { name = "saved", saved });
            var first = await probe.SendAsync(ProbeCommand.Seek, target: 500);
            var firstRows = ReadRows(trace).Skip(401).Take(100).ToArray();
            steps.Add(new { name = "first", first, rows = firstRows.Length,
                digest = Digest(firstRows) });
            if (first.Ordinal != 500 || firstRows.Length != 100)
                throw new InvalidDataException("第一次前向轨迹不完整。");
            var restored = await probe.SendAsync(ProbeCommand.RestoreSnapshot, target: 400);
            steps.Add(new { name = "restored", restored });
            if (restored.Ordinal != 400 || restored.Status != 3)
                throw new InvalidDataException("快照未返回第 400 帧暂停状态。");
            var second = await probe.SendAsync(ProbeCommand.Seek, target: 500);
            var secondRows = ReadRows(trace).Skip(401).Take(100).ToArray();
            var identical = firstRows.SequenceEqual(secondRows);
            steps.Add(new { name = "second", second, rows = secondRows.Length,
                digest = Digest(secondRows), identical });
            if (second.Ordinal != 500 || !identical)
                throw new InvalidDataException("内存快照重放轨迹与首次运行不一致。");
            var savedLater = await probe.SendAsync(ProbeCommand.SaveSnapshot, target: 500);
            steps.Add(new { name = "saved-later", savedLater });
            var firstLater = await probe.SendAsync(ProbeCommand.Seek, target: 600);
            var laterRows = ReadRows(trace).Skip(501).Take(100).ToArray();
            steps.Add(new { name = "first-later", firstLater, rows = laterRows.Length,
                digest = Digest(laterRows) });
            if (firstLater.Ordinal != 600 || laterRows.Length != 100)
                throw new InvalidDataException("第二个快照之后的轨迹不完整。");
            var earlyAgain = await probe.SendAsync(ProbeCommand.RestoreSnapshot, target: 400);
            steps.Add(new { name = "early-again", earlyAgain });
            var forwardAgain = await probe.SendAsync(ProbeCommand.Seek, target: 500);
            steps.Add(new { name = "forward-again", forwardAgain });
            var laterAgain = await probe.SendAsync(ProbeCommand.RestoreSnapshot, target: 500);
            steps.Add(new { name = "later-again", laterAgain });
            var secondLater = await probe.SendAsync(ProbeCommand.Seek, target: 600);
            var repeatedRows = ReadRows(trace).Skip(501).Take(100).ToArray();
            var laterIdentical = laterRows.SequenceEqual(repeatedRows);
            steps.Add(new { name = "second-later", secondLater,
                rows = repeatedRows.Length, digest = Digest(repeatedRows),
                identical = laterIdentical });
            if (earlyAgain.Ordinal != 400 || forwardAgain.Ordinal != 500 ||
                laterAgain.Ordinal != 500 || secondLater.Ordinal != 600 ||
                !laterIdentical)
                throw new InvalidDataException("跨两个快照的回放轨迹不一致。");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (probe is not null)
            {
                try { steps.Add(new { name = "release", state = await probe.ReleaseAsync() }); }
                catch (Exception ex) { steps.Add(new { name = "release-error", error = ex.ToString() }); }
                probe.Dispose();
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { steps,
                error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure is not null) Environment.ExitCode = 1;
    }

    private static string[] ReadRows(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        _ = reader.ReadLine();
        var rows = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') rows.Add(line);
        return rows.ToArray();
    }
    private static string Digest(IEnumerable<string> rows) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
}
