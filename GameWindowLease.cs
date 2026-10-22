using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SokuReplayLab;

public sealed record GameWindowState(long Parent, int Style, int ExStyle,
    int Left, int Top, int Width, int Height, int ShowCommand, bool Visible)
{
    public bool EquivalentTo(GameWindowState other) =>
        Parent == other.Parent && Style == other.Style && ExStyle == other.ExStyle &&
        ShowCommand == other.ShowCommand && Visible == other.Visible &&
        (ShowCommand == 2 ||
         Left == other.Left && Top == other.Top &&
         Width == other.Width && Height == other.Height);
}

/// <summary>Owns one reversible Win32 parent/style lease for the verified game window.</summary>
public sealed class GameWindowLease
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public WindowPoint MinPosition, MaxPosition;
        public WindowRect NormalPosition;
    }

    private const int GwlStyle = -16, GwlExStyle = -20;
    private const uint WsChild = 0x40000000, WsPopup = 0x80000000,
        WsCaption = 0x00C00000, WsThickFrame = 0x00040000,
        WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000,
        WsMaximizeBox = 0x00010000, WsMinimize = 0x20000000,
        WsExTransparent = 0x00000020;
    private const uint FrameMask = WsPopup | WsCaption | WsThickFrame |
        WsSysMenu | WsMinimizeBox | WsMaximizeBox;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
        SwpFrameChanged = 0x0020, SwpShowWindow = 0x0040,
        SwpNoCopyBits = 0x0100, SwpAsyncWindowPos = 0x4000;
    private const int SwHide = 0, SwShowNoActivate = 8, SwRestoreNoActivate = 4;
    private readonly nint game;
    private readonly nint host;
    private readonly nint originalParent;
    private readonly int originalStyle;
    private readonly int originalExStyle;
    private readonly WindowRect originalRect;
    private readonly WindowPlacement originalPlacement;
    private readonly bool originallyVisible;
    private readonly int pid;
    private readonly List<nint> hiddenAuxiliary = [];
    private int lastWidth, lastHeight;
    private bool active;

    private delegate bool EnumCallback(nint hwnd, nint value);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint hwnd, nint parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint hwnd, in WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam,
        nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint value);

    public GameWindowLease(GameInstance instance, nint hostWindow)
    {
        GameDiscovery.VerifyGameFile(instance.Exe);
        if (instance.Window == 0 || !IsWindow(instance.Window) || !IsWindow(hostWindow))
            throw new InvalidOperationException("游戏窗口或画面宿主已关闭。");
        GetWindowThreadProcessId(instance.Window, out var windowPid);
        if (windowPid != instance.Pid)
            throw new InvalidOperationException("游戏主窗口 PID 与已验证进程不一致。");
        game = instance.Window;
        host = hostWindow;
        pid = instance.Pid;
        originalParent = GetParent(game);
        originalStyle = GetWindowLongW(game, GwlStyle);
        originalExStyle = GetWindowLongW(game, GwlExStyle);
        if (!GetWindowRect(game, out originalRect)) throw new Win32Exception();
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(game, ref placement)) throw new Win32Exception();
        originalPlacement = placement;
        originallyVisible = IsWindowVisible(game);
    }

    private static int EmbeddedStyle(int style) => unchecked((int)(((uint)style | WsChild) & ~FrameMask));
    public static GameWindowState Inspect(nint window)
    {
        if (!IsWindow(window) || !GetWindowRect(window, out var rect))
            throw new InvalidOperationException("无法读取游戏窗口状态。");
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(window, ref placement)) throw new Win32Exception();
        return new GameWindowState(GetAncestor(window, 1).ToInt64(),
            GetWindowLongW(window, GwlStyle), GetWindowLongW(window, GwlExStyle),
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            placement.ShowCommand, IsWindowVisible(window));
    }
    private static void SetStyle(nint window, int index, int value)
    {
        Marshal.SetLastPInvokeError(0);
        var old = SetWindowLongW(window, index, value);
        if (old == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static void ChangeParent(nint window, nint parent)
    {
        Marshal.SetLastPInvokeError(0);
        var old = SetParent(window, parent);
        if (old == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static bool Responsive(nint window) =>
        SendMessageTimeoutW(window, 0, 0, 0, 0x0003, 500, out _) != 0;

    public void Attach(int width, int height)
    {
        if (active) return;
        ConnectionTrace.Write("lease.preflight", pid);
        if (!Responsive(game))
            throw new InvalidOperationException("游戏窗口当前无响应，暂不嵌入画面。请等待游戏恢复后重试连接。");
        ConnectionTrace.Write("lease.hide-bars", pid);
        HideBlackBars();
        try
        {
            ShowWindowAsync(game, SwHide);
            ConnectionTrace.Write("lease.style", pid);
            SetStyle(game, GwlStyle, EmbeddedStyle(originalStyle));
            SetStyle(game, GwlExStyle, originalExStyle & unchecked((int)~WsExTransparent));
            ConnectionTrace.Write("lease.parent", pid);
            ChangeParent(game, host);
            active = true;
            ConnectionTrace.Write("lease.layout", pid);
            Layout(width, height);
            if (GetAncestor(game, 1) != host)
                throw new InvalidOperationException("游戏窗口未能保持在播放区域内。");
        }
        catch
        {
            if (!Release())
                throw new InvalidOperationException("游戏画面嵌入失败，且窗口未能自动恢复；请保持复盘程序窗口开启。");
            throw;
        }
    }

    public void Layout(int width, int height)
    {
        if (!active || !IsWindow(game)) return;
        width = Math.Max(1, width); height = Math.Max(1, height);
        var style = GetWindowLongW(game, GwlStyle);
        if (((uint)style & WsMinimize) != 0)
        {
            ShowWindowAsync(game, SwRestoreNoActivate);
            lastWidth = lastHeight = 0;
            return;
        }
        var changed = false;
        if (style != EmbeddedStyle(style))
        {
            SetStyle(game, GwlStyle, EmbeddedStyle(style));
            changed = true;
        }
        if (GetAncestor(game, 1) != host)
        {
            if (!Responsive(game))
                throw new InvalidOperationException("游戏窗口当前无响应，暂不重试嵌入画面。");
            ShowWindowAsync(game, SwHide);
            ChangeParent(game, host);
            if (GetAncestor(game, 1) != host)
                throw new InvalidOperationException("切屏后游戏窗口脱离播放区域。");
            changed = true;
        }
        var exStyle = GetWindowLongW(game, GwlExStyle);
        if (((uint)exStyle & WsExTransparent) != 0)
        {
            SetStyle(game, GwlExStyle, exStyle & unchecked((int)~WsExTransparent));
            changed = true;
        }
        var matched = GetClientRect(game, out var client) &&
            client.Right - client.Left == width && client.Bottom - client.Top == height;
        if (!changed && matched && width == lastWidth && height == lastHeight) return;
        if (!SetWindowPos(game, 0, 0, 0, width, height,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpAsyncWindowPos |
                SwpNoCopyBits | (changed ? SwpFrameChanged : 0)))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        lastWidth = width; lastHeight = height;
    }

    public bool Release()
    {
        if (!IsWindow(game)) { active = false; return true; }
        ConnectionTrace.Write("lease.restore.preflight", pid);
        if (!Responsive(game)) return false;
        ShowWindowAsync(game, SwHide);
        var target = ((uint)originalStyle & WsChild) != 0 ? originalParent : 0;
        var expected = target != 0 ? target : GetDesktopWindow();
        try
        {
            ConnectionTrace.Write("lease.restore.parent", pid);
            if (GetAncestor(game, 1) != expected) ChangeParent(game, target);
            if (GetAncestor(game, 1) != expected) return RestoreVisible(false);
            SetStyle(game, GwlStyle, originalStyle);
            SetStyle(game, GwlExStyle, originalExStyle);
            if (GetParent(game) != originalParent) return RestoreVisible(false);
            var width = originalRect.Right - originalRect.Left;
            var height = originalRect.Bottom - originalRect.Top;
            if (!SetWindowPos(game, 0, originalRect.Left, originalRect.Top,
                    width, height, SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
                return RestoreVisible(false);
            if (!SetWindowPlacement(game, in originalPlacement)) return RestoreVisible(false);
            if (!originallyVisible) ShowWindowAsync(game, SwHide);
            foreach (var window in hiddenAuxiliary.Where(IsWindow)) ShowWindowAsync(window, SwShowNoActivate);
            hiddenAuxiliary.Clear();
            active = false;
            lastWidth = lastHeight = 0;
            ConnectionTrace.Write("lease.restore.done", pid);
            return true;
        }
        catch (Win32Exception) { return RestoreVisible(false); }
    }

    private bool RestoreVisible(bool result)
    {
        ShowWindowAsync(game, originallyVisible ? SwShowNoActivate : SwHide);
        return result;
    }

    private void HideBlackBars()
    {
        EnumWindows((window, _) =>
        {
            if (window == game || !IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var owner);
            if (owner != pid) return true;
            var name = new StringBuilder(64);
            GetClassNameW(window, name, name.Capacity);
            if (name.ToString() == "BlackBars")
            {
                ShowWindowAsync(window, SwHide);
                hiddenAuxiliary.Add(window);
            }
            return true;
        }, 0);
    }
}
