using System.IO;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim playbackControlGate = new(1, 1);
    private bool nativeTransportBusy;
    private bool nativePlaybackReleased;
    private string? nativeReplaySource;
    private int? pendingSeekTarget;
    private long seekRequestVersion;
    private Task? sessionLoadTask;
    private bool TakeoverTransition => givingGameFocus || returningGameFocus || gameKeyboardFocus is not null;
    private bool PlaybackOperationBlocked => TakeoverTransition || probeConnecting || branchBusy || escapeBusy || fullCaptureBusy || sessionLoading || shutdownInProgress || shutdownReady;
    private bool ReplayControlInFlight => TakeoverTransition || nativeTransportBusy || pendingSeekTarget is not null || playbackControlGate.CurrentCount == 0;
    private bool NativeSessionMatches => probe is not null && session?.Header is not null &&
        !nativePlaybackReleased && string.Equals(nativeReplaySource, session.Source, StringComparison.OrdinalIgnoreCase);

    private void ResetReplayContext()
    {
        ++sessionRefreshVersion;
        ++seekRequestVersion;
        pendingSeekTarget = null;
        nativeReplaySource = null;
        nativePlaybackReleased = true;
        branchOriginal = null;
        takeoverBookmark = null;
        selectedEscapeResult = null;
        escapeResults.Clear();
        ClearNativeSnapshots();
    }

    private void AdoptPlaybackPosition(ProbeSnapshot state)
    {
        probeStatus = state;
        if (!NativeSessionMatches || state.Status is not (2 or 3 or -5) || session?.Frames.Count is not > 0) return;
        cursor = Math.Clamp(state.Ordinal, 0, session.Frames.Count - 1);
        UpdateTransport();
        refreshReplayFrame?.Invoke();
    }

    private async Task EnsureNativePlaybackAsync()
    {
        if (probe is null) throw new InvalidOperationException("请先连接游戏探针。");
        if (session?.Header is null || !string.Equals(Path.GetExtension(session.Source), ".rep", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前是离线轨迹，不能控制另一个游戏回放；请载入对应 REP。");
        var state = probe.Read();
        if (state.Sequence != state.Acknowledged)
            throw new InvalidOperationException("游戏仍在执行采集或定位，请等待完成。");
        if (state.Status is 2 or 3 && NativeSessionMatches)
        {
            nativeReplaySource = session.Source;
            probeStatus = state;
            return;
        }
        ClearNativeSnapshots();
        probeStatus = await ReplayBranchRunner.RestoreAsync(probe, session, root, cursor);
        probeStatus = await probe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
        nativeReplaySource = session.Source;
        nativePlaybackReleased = false;
    }

    private async Task SeekPlaybackAsync(int ordinal)
    {
        if (PlaybackOperationBlocked || session?.Frames.Count is not > 0) return;
        var target = Math.Clamp(ordinal, 0, session.Frames.Count - 1);
        var request = ++seekRequestVersion;
        pendingSeekTarget = target;
        PausePlayback();
        await playbackControlGate.WaitAsync();
        try
        {
            if (request != seekRequestVersion || PlaybackOperationBlocked) return;
            nativeTransportBusy = true;
            UpdateTransport();
            if (probe is not null)
            {
                await EnsureNativePlaybackAsync();
                ProbeSnapshot state;
                if (probeStatus?.Status == 3 && nativeSnapshotSlots.Contains(target) &&
                    nativeSnapshotPid == probe.Pid && string.Equals(nativeSnapshotSource, session.Source, StringComparison.OrdinalIgnoreCase))
                {
                    try { state = await probe.SendAsync(ProbeCommand.RestoreSnapshot, target); }
                    catch (IOException) when (probe.Read() is { Status: -7 } rejected && rejected.Sequence == rejected.Acknowledged)
                    {
                        // A stale native slot must not make the node unusable.
                        ClearNativeSnapshots();
                        state = await probe.SendAsync(ProbeCommand.Seek, target);
                    }
                }
                else
                    state = await probe.SendAsync(ProbeCommand.Seek, target);
                if (state.Status != 3 || state.Ordinal != target)
                    throw new IOException("游戏未确认目标位置，未将界面跳转视作定位成功。");
                if (request == seekRequestVersion)
                {
                    AdoptPlaybackPosition(state);
                    message = $"游戏回放已定位到轨迹帧 {state.Ordinal:N0}。";
                }
            }
            else if (detectedGame is not null)
                throw new InvalidOperationException("请先连接探针，再控制游戏播放位置。");
            else
            {
                cursor = target;
                refreshReplayFrame?.Invoke();
            }
        }
        catch (Exception ex) { if (request == seekRequestVersion) message = $"游戏定位失败：{ex.Message}"; }
        finally
        {
            nativeTransportBusy = false;
            if (request == seekRequestVersion) pendingSeekTarget = null;
            playbackControlGate.Release();
            UpdateTransport();
            UpdateTopNotice();
        }
    }

    private async Task ToggleNativePlaybackAsync()
    {
        if (PlaybackOperationBlocked) return;
        PausePlayback();
        await playbackControlGate.WaitAsync();
        try
        {
            if (PlaybackOperationBlocked) return;
            nativeTransportBusy = true;
            UpdateTransport();
            await EnsureNativePlaybackAsync();
            var command = probe!.Read().Status == 2 ? ProbeCommand.Pause : ProbeCommand.Play;
            if (command == ProbeCommand.Play && session!.Frames.Count > 0 &&
                session.Frames[^1].MatchState == 5 && cursor >= session.Frames.Count - 1)
                await probe.SendAsync(ProbeCommand.Seek, 0);
            AdoptPlaybackPosition(await probe.SendAsync(command));
            message = command == ProbeCommand.Pause ? "游戏回放已暂停。" : "游戏回放继续播放。";
        }
        catch (Exception ex) { message = $"游戏回放控制失败：{ex.Message}"; }
        finally
        {
            nativeTransportBusy = false;
            playbackControlGate.Release();
            UpdateTransport();
            UpdateTopNotice();
        }
    }

    private async Task ChangePlaybackSpeedAsync(double rate)
    {
        if (probe is null || session?.Header is null || PlaybackOperationBlocked) return;
        await playbackControlGate.WaitAsync();
        try
        {
            if (PlaybackOperationBlocked) return;
            nativeTransportBusy = true;
            await EnsureNativePlaybackAsync();
            AdoptPlaybackPosition(await probe!.SendAsync(ProbeCommand.Speed, (int)Math.Round(rate * 100)));
        }
        catch (Exception ex) { message = $"游戏调速失败：{ex.Message}"; }
        finally
        {
            nativeTransportBusy = false;
            playbackControlGate.Release();
            UpdateTransport();
            UpdateTopNotice();
        }
    }
}
