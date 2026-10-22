using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private GameKeyboardFocus? gameKeyboardFocus;
    private bool givingGameFocus;
    private Button? takeoverButton;
    private readonly DispatcherTimer gameFocusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private async Task GiveGameFocusAsync()
    {
        if (gameKeyboardFocus is not null || givingGameFocus || branchBusy || escapeBusy ||
            fullCaptureBusy || recordingInput || sessionLoading) return;
        if (detectedGame is null)
        {
            message = "请先启动并连接游戏，再点击接管。";
            UpdateTopNotice();
            return;
        }
        givingGameFocus = true;
        try
        {
            PausePlayback();
            if (probe is not null)
            {
                var state = probe.Read();
                if (state.Sequence != state.Acknowledged || state.Status == 1)
                    throw new InvalidOperationException("请等待当前采集或定位完成后再接管。");
                if (state.Status is 2 or 3)
                    probeStatus = await probe.SendAsync(ProbeCommand.FreeControl);
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
        finally { givingGameFocus = false; UpdateTransport(); }
    }

    private void ReturnGameFocus()
    {
        var focus = gameKeyboardFocus;
        gameKeyboardFocus = null;
        gameFocusTimer.Stop();
        focus?.ReturnToOwner();
        message = "已退出键盘接管；游戏保持当前状态。";
        UpdateTopNotice();
        UpdateTransport();
    }
}
