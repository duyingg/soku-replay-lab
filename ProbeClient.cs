using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace SokuReplayLab;

public enum ProbeCommand {
    Load = 1, Play = 2, Pause = 3, Seek = 4, Release = 5, Speed = 6,
    SaveSnapshot = 7, RestoreSnapshot = 8, Capture = 9,
    Branch = 10, RestoreState = 11, FreeControl = 12
}
public sealed record ProbeSnapshot(int Sequence, int Acknowledged, int Status,
    int Ordinal, int Target, int MatchState, int EngineFrame);

public sealed class ProbeClient : IDisposable
{
    private const int Size = 1080;
    private const int Magic = 0x524c5031;
    private const int Version = 8;
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly object commandGate = new();
    private readonly Process gameProcess;
    private bool disposed;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    public int Pid { get; }

    private ProbeClient(MemoryMappedFile map, int pid)
    {
        Pid = pid;
        this.map = map;
        view = map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
        if (view.ReadInt32(0) != Magic || view.ReadInt32(4) != Version)
        {
            view.Dispose(); map.Dispose();
            throw new InvalidDataException("探针控制协议版本不匹配，需要重新启动游戏并接入新版探针。");
        }
        try { gameProcess = Process.GetProcessById(pid); }
        catch (ArgumentException ex)
        {
            view.Dispose(); map.Dispose();
            throw new IOException("游戏进程已退出，无法连接探针。", ex);
        }
    }

    private static string Name(int pid) => $@"Local\ReplayLabProbeV8_{pid}";

    public static ProbeClient? TryOpen(int pid)
    {
        try { return new ProbeClient(MemoryMappedFile.OpenExisting(Name(pid)), pid); }
        catch (FileNotFoundException)
        {
            foreach (var version in new[] { 7, 6, 5, 4, 3 })
            {
                try
                {
                    using var old = MemoryMappedFile.OpenExisting($@"Local\ReplayLabProbeV{version}_{pid}");
                    throw new InvalidOperationException(
                        "游戏内仍是旧版探针；请关闭并重新启动游戏后连接，避免叠加注入。");
                }
                catch (FileNotFoundException) { }
            }
            return null;
        }
    }

    public static async Task<ProbeClient> AttachAsync(GameInstance game, string root,
        CancellationToken cancellationToken = default)
    {
        var verified = await Task.Run(() =>
        {
            GameDiscovery.VerifyGameFile(game.Exe);
            return game.Window != 0 && GameDiscovery.Find().Any(x =>
                x.Pid == game.Pid && x.Window == game.Window);
        }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        if (!verified)
            throw new InvalidOperationException("当前会话未找到该游戏的已验证主窗口，无法连接探针。");
        var resident = TryOpen(game.Pid);
        if (resident is not null) return resident;
        var helper = Path.Combine(root, "NativeAttach.exe");
        var module = Path.Combine(root, "NativeProbe.dll");
        if (!File.Exists(helper) || !File.Exists(module))
            throw new FileNotFoundException("新版 x86 探针未编译。请运行 build_native.bat。");
        var start = new ProcessStartInfo(helper) {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(game.Pid.ToString());
        start.ArgumentList.Add(module);
        start.ArgumentList.Add(game.Window.ToInt64().ToString());
        using var process = Process.Start(start) ?? throw new IOException("注入辅助程序未能启动。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("探针连接辅助程序超时；请确认游戏仍在标题界面。", cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken) : null);
        }
        if (process.ExitCode != 0)
            throw new IOException($"探针连接失败（{process.ExitCode}）：{await error}");
        _ = await output;
        return TryOpen(game.Pid) ?? throw new IOException("探针已启动，但没有建立控制通道。");
    }

    public ProbeSnapshot Read()
    {
        lock (commandGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (gameProcess.HasExited) throw new IOException("游戏进程已退出，探针控制已断开。");
            if (view.ReadInt32(0) != Magic || view.ReadInt32(4) != Version)
                throw new InvalidDataException("探针控制通道已失效。");
            return new(view.ReadInt32(8), view.ReadInt32(12), view.ReadInt32(20),
                view.ReadInt32(24), view.ReadInt32(28), view.ReadInt32(32), view.ReadInt32(36));
        }
    }

    public async Task<ProbeSnapshot> SendAsync(ProbeCommand command, int target = -1,
        string? replayPath = null, string? tracePath = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        bool supersedePending = false, int branchSide = -1,
        int branchInputIndex = -1,
        IReadOnlyList<ushort>? branchMasks = null)
    {
        int sequence;
        // Serialize publication, but let Release supersede a command whose
        // acknowledgement is still pending on the game thread.
        lock (commandGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var before = Read();
            sequence = checked(before.Sequence + 1);
            if (!supersedePending && before.Sequence != before.Acknowledged)
                throw new InvalidOperationException("上一条探针命令尚未完成。");
            if (command == ProbeCommand.Load)
            {
                if (replayPath is null || tracePath is null || !File.Exists(replayPath))
                    throw new FileNotFoundException("载入命令需要存在的 REP 和轨迹输出路径。");
                WriteAnsi(40, replayPath);
                WriteAnsi(560, tracePath);
            }
            if (command == ProbeCommand.Branch)
            {
                if (branchSide is not (0 or 1) || branchInputIndex < 0 ||
                    branchMasks is null || branchMasks.Count is < 1 or > 250 ||
                    tracePath is null)
                    throw new ArgumentException("快照分支需要玩家侧、输入和轨迹路径。");
                var payload = new byte[520];
                BitConverter.TryWriteBytes(payload.AsSpan(0, 4), branchSide);
                BitConverter.TryWriteBytes(payload.AsSpan(4, 4), branchMasks.Count);
                BitConverter.TryWriteBytes(payload.AsSpan(8, 4), branchInputIndex);
                for (var i = 0; i < branchMasks.Count; i++)
                    BitConverter.TryWriteBytes(payload.AsSpan(12 + i * 2, 2), branchMasks[i]);
                view.WriteArray(40, payload, 0, payload.Length);
                WriteAnsi(560, tracePath);
            }
            if (command == ProbeCommand.RestoreState)
            {
                view.Write(40, tracePath is null ? 0 : 1);
                if (tracePath is not null) WriteAnsi(560, tracePath);
            }
            view.Write(28, target);
            view.Write(16, (int)command);
            Thread.MemoryBarrier();
            view.Write(8, sequence);
        }
        var limit = Stopwatch.StartNew();
        var maximum = timeout ?? TimeSpan.FromSeconds(command == ProbeCommand.Seek ? 90 : 15);
        while (limit.Elapsed < maximum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Read();
            if (state.Sequence != sequence)
                throw new OperationCanceledException($"探针命令 {command} 已被后续命令替代。");
            if (state.Acknowledged == sequence)
            {
                if (state.Status < 0)
                    throw new IOException($"探针拒绝命令 {command}，状态 {state.Status}。");
                return state;
            }
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException($"探针没有确认命令 {command}；游戏可能无响应。");
    }

    public Task<ProbeSnapshot> ReleaseAsync(CancellationToken cancellationToken = default)
        => SendAsync(ProbeCommand.Release, timeout: TimeSpan.FromSeconds(3),
            cancellationToken: cancellationToken, supersedePending: true);

    private void WriteAnsi(int offset, string path)
    {
        WriteAnsiText(offset, Path.GetFullPath(path));
    }

    private void WriteAnsiText(int offset, string value)
    {
        // The game reads REP paths through its narrow Windows API.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(checked((int)GetACP()),
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var bytes = encoding.GetBytes(value);
        if (bytes.Length >= 520) throw new PathTooLongException("探针路径超过 519 字节。");
        var slot = new byte[520];
        bytes.CopyTo(slot, 0);
        view.WriteArray(offset, slot, 0, slot.Length);
    }

    public void Dispose()
    {
        lock (commandGate)
        {
            if (disposed) return;
            disposed = true;
            view.Dispose();
            map.Dispose();
            gameProcess.Dispose();
        }
    }

    public static async Task ProtocolSmokeAsync()
    {
        var pid = Environment.ProcessId;
        using var server = MemoryMappedFile.CreateOrOpen(Name(pid), Size, MemoryMappedFileAccess.ReadWrite);
        using var peer = server.CreateViewAccessor(0, Size);
        peer.Write(0, Magic);
        peer.Write(4, Version);
        using var client = TryOpen(pid) ?? throw new Exception("模拟通道未建立。");
        var command = client.SendAsync(ProbeCommand.Pause);
        await Task.Delay(50);
        if (peer.ReadInt32(16) != (int)ProbeCommand.Pause || peer.ReadInt32(8) != 1)
            throw new Exception("探针命令字段不一致。");
        peer.Write(20, 3);
        peer.Write(12, 1);
        var result = await command;
        if (result.Status != 3 || result.Acknowledged != 1)
            throw new Exception("探针确认字段不一致。");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await client.SendAsync(ProbeCommand.Seek, target: 999,
                    cancellationToken: cancelled.Token);
                throw new Exception("已取消的探针命令仍然执行。");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            if (peer.ReadInt32(8) != 1 || peer.ReadInt32(16) != (int)ProbeCommand.Pause ||
                peer.ReadInt32(28) != -1)
                throw new Exception("已取消的探针命令修改了共享通道。");
        }
        var release = client.ReleaseAsync();
        await Task.Delay(50);
        if (peer.ReadInt32(16) != (int)ProbeCommand.Release || peer.ReadInt32(8) != 2)
            throw new Exception("探针释放命令字段不一致。");
        peer.Write(20, 0);
        peer.Write(12, 2);
        if ((await release).Status != 0)
            throw new Exception("探针释放确认字段不一致。");
        var pending = client.SendAsync(ProbeCommand.Seek, target: 999);
        try
        {
            await client.SendAsync(ProbeCommand.Pause);
            throw new Exception("未确认命令被普通命令覆盖。");
        }
        catch (InvalidOperationException) { }
        if (peer.ReadInt32(8) != 3 || peer.ReadInt32(16) != (int)ProbeCommand.Seek || peer.ReadInt32(28) != 999)
            throw new Exception("拒绝重入后控制通道被修改。");
        var interruptedRelease = client.ReleaseAsync();
        var replaced = false;
        try { await pending; }
        catch (OperationCanceledException) { replaced = true; }
        if (!replaced) throw new Exception("被释放命令替代的定位仍在等待。");
        peer.Write(20, 0);
        peer.Write(12, 4);
        if ((await interruptedRelease).Acknowledged != 4)
            throw new Exception("抢占后的释放命令未确认。");
    }
}
