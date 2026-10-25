using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private GameKeyboardFocus? gameKeyboardFocus;
    private bool givingGameFocus;
    private bool returningGameFocus;
    private sealed record TakeoverBookmark(ReplaySession Session, int Ordinal, ProbeClient Probe);
    private TakeoverBookmark? takeoverBookmark;
    private Button? takeoverButton;
    private readonly DispatcherTimer gameFocusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private async Task GiveGameFocusAsync()
    {
        if (gameKeyboardFocus is not null || givingGameFocus || returningGameFocus || branchBusy || escapeBusy ||
            fullCaptureBusy || sessionLoading || probeConnecting || shutdownInProgress || shutdownReady) return;
        if (detectedGame is null)
        {
            message = "请先启动并连接游戏，再点击接管。";
            UpdateTopNotice();
            return;
        }
        givingGameFocus = true;
        await playbackControlGate.WaitAsync();
        try
        {
            if (shutdownInProgress || shutdownReady) return;
            PausePlayback();
            if (probe is not null)
            {
                if (session?.Header is not null) await EnsureNativePlaybackAsync();
                var state = probe.Read();
                if (state.Sequence != state.Acknowledged || state.Status == 1)
                    throw new InvalidOperationException("请等待当前采集或定位完成后再接管。");
                if (state.Status == 2)
                    state = await probe.SendAsync(ProbeCommand.Pause);
                if (NativeSessionMatches && state.Status == 3)
                {
                    AdoptPlaybackPosition(state);
                    takeoverBookmark = new TakeoverBookmark(session!, state.Ordinal, probe);
                }
                if (state.Status is 2 or 3)
                {
                    ClearNativeSnapshots();
                    probeStatus = await probe.SendAsync(ProbeCommand.FreeControl);
                    nativePlaybackReleased = true;
                }
            }
            page = 0;
            message = $"游戏键盘接管中 · {ExitTakeoverKeyLabel} 返回复盘程序；角色接管由游戏 MOD 处理。";
            Render();
            for (var attempt = 0; gameSurface is not null &&
                !gameSurface.IsAttached && gameSurface.IsBusy && attempt < 60; attempt++)
                await Task.Delay(50);
            gameKeyboardFocus = new GameKeyboardFocus(this, detectedGame.Window, ReturnGameFocus,
                KeyInterop.VirtualKeyFromKey(exitTakeoverKey));
            gameKeyboardFocus.Enter();
            gameFocusTimer.Start();
        }
        catch (Exception ex)
        {
            gameKeyboardFocus?.ReturnToOwner();
            gameKeyboardFocus = null;
            message = $"无法交出游戏焦点：{ex.Message}";
            UpdateTopNotice();
        }
        finally
        {
            givingGameFocus = false;
            playbackControlGate.Release();
            UpdateTransport();
        }
        // If keyboard acquisition failed after releasing replay control, put
        // the original replay back rather than leaving the controls detached.
        if (gameKeyboardFocus is null && takeoverBookmark is not null)
            await ReturnGameFocusAsync();
    }

    private void ReturnGameFocus() => _ = ReturnGameFocusAsync();

    private async Task ReturnGameFocusAsync()
    {
        if (returningGameFocus || gameKeyboardFocus is null && takeoverBookmark is null) return;
        returningGameFocus = true;
        var focus = gameKeyboardFocus;
        var gameAlive = focus?.IsGameWindowAlive ?? detectedGame is not null;
        gameKeyboardFocus = null;
        gameFocusTimer.Stop();
        focus?.ReturnToOwner();
        UpdateTransport();
        await playbackControlGate.WaitAsync();
        try
        {
            PausePlayback();
            var saved = takeoverBookmark;
            if (saved is not null && gameAlive && ReferenceEquals(probe, saved.Probe) &&
                detectedGame?.Pid == saved.Probe.Pid && !shutdownInProgress && !shutdownReady)
            {
                message = "已收回键盘，正在恢复接管前的 REP 和播放位置…";
                UpdateTopNotice();
                ClearNativeSnapshots();
                var restored = await ReplayBranchRunner.RestoreAsync(saved.Probe, saved.Session, root, saved.Ordinal);
                session = saved.Session;
                nativeReplaySource = session.Source;
                nativePlaybackReleased = false;
                restored = await saved.Probe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
                AdoptPlaybackPosition(restored);
                takeoverBookmark = null;
                message = $"已退出接管，恢复原 REP 第 {restored.Ordinal:N0} 帧并暂停。";
            }
            else message = "已退出键盘接管。";
        }
        catch (Exception ex)
        {
            message = $"键盘已收回，但恢复 REP 失败：{ex.Message}；播放控制不会退回独立计时。";
        }
        finally
        {
            returningGameFocus = false;
            playbackControlGate.Release();
            UpdateTransport();
            Render();
        }
    }
}
