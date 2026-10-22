using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SokuReplayLab;

/// <summary>Gives the game's HWND keyboard focus. Only the configured exit key is reserved.</summary>
public sealed class GameKeyboardFocus : IDisposable
{
    private delegate nint KeyboardHook(int code, nint message, nint data);
    private delegate void ForegroundHook(nint hook, uint ev, nint window, int obj, int child, uint thread, uint time);
    private readonly KeyboardHook callback;
    private readonly ForegroundHook foregroundCallback;
    private readonly Window owner;
    private readonly nint game;
    private readonly Action escaped;
    private readonly int exitVirtualKey;
    private nint hook;
    private nint foregroundHook;
    private bool active;
    private bool suspended;
    public bool IsActive => active;
    public bool IsGameWindowAlive => IsWindow(game);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, KeyboardHook proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max,
        nint module, ForegroundHook callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint window, uint message,
        nint w, nint l, uint flags, uint timeout, out nint result);

    public GameKeyboardFocus(Window owner, nint game, Action escaped, int exitVirtualKey = 0x1b)
    {
        this.owner = owner;
        this.game = game;
        this.escaped = escaped;
        if (exitVirtualKey is <= 0 or >= 256) throw new ArgumentOutOfRangeException(nameof(exitVirtualKey));
        this.exitVirtualKey = exitVirtualKey;
        callback = OnKeyboard;
        foregroundCallback = (_, _, window, _, _, _, _) => UpdateForeground(window);
    }
    public void Enter()
    {
        owner.Dispatcher.VerifyAccess();
        if (active) return;
        hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法保留接管退出键。");
        active = true;
        foregroundHook = SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
        if (foregroundHook == 0) { Dispose(); throw new InvalidOperationException("无法跟踪游戏窗口焦点。"); }
        ActivateGame();
    }
    private void ActivateGame()
    {
        var caller = GetCurrentThreadId();
        var target = GetWindowThreadProcessId(game, out _);
        var attached = caller != target && AttachThreadInput(caller, target, true);
        try
        {
            if (target == 0 || caller != target && !attached)
                throw new InvalidOperationException("无法将键盘焦点交给游戏。");
            SetForegroundWindow(GetAncestor(game, 2));
            SetFocus(game);
            // Embedded children do not receive the top-level activation messages
            // which the game's own DirectInput acquisition expects.
            SendMessageTimeoutW(game, 0x1c, 1, 0, 2, 500, out _); // WM_ACTIVATEAPP
            SendMessageTimeoutW(game, 0x06, 1, 0, 2, 500, out _); // WM_ACTIVATE
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(target, ref info) || info.Focus != game)
                throw new InvalidOperationException("游戏窗口未取得键盘焦点。");
            if (SendMessageTimeoutW(game, 0x8000 + 0x524, 1, 0, 2, 1000, out var acquired) == 0 || acquired != 1)
                throw new InvalidOperationException("游戏未能获取嵌入窗口的键盘设备，请确认已连接新版探针。");
            suspended = false;
        }
        catch { Dispose(); throw; }
        finally { if (attached) AttachThreadInput(caller, target, false); }
    }
    private void UpdateForeground(nint foreground)
    {
        if (!active) return;
        if (foreground != GetAncestor(game, 2) && foreground != new WindowInteropHelper(owner).Handle)
        {
            if (suspended) return;
            SendMessageTimeoutW(game, 0x06, 0, 0, 2, 500, out _);
            SendMessageTimeoutW(game, 0x1c, 0, 0, 2, 500, out _);
            SendMessageTimeoutW(game, 0x8000 + 0x524, 0, 0, 2, 1000, out _);
            suspended = true;
        }
        else if (suspended)
        {
            try { ActivateGame(); }
            catch { escaped(); }
        }
    }
    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && active && Marshal.ReadInt32(data) == exitVirtualKey)
        {
            var foreground = GetForegroundWindow();
            if (foreground == GetAncestor(game, 2) ||
                foreground == new WindowInteropHelper(owner).Handle)
            {
                if (message == 0x100 || message == 0x104)
                {
                    active = false;
                    owner.Dispatcher.BeginInvoke(escaped, DispatcherPriority.Send);
                }
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void ReturnToOwner()
    {
        Dispose();
        SendMessageTimeoutW(game, 0x06, 0, 0, 2, 500, out _);
        SendMessageTimeoutW(game, 0x1c, 0, 0, 2, 500, out _);
        owner.Activate();
        SetFocus(new WindowInteropHelper(owner).Handle);
        owner.Focus();
    }
    public void Dispose()
    {
        if (hook != 0 && IsWindow(game))
            SendMessageTimeoutW(game, 0x8000 + 0x524, 0, 0, 2, 1000, out _);
        active = false;
        if (foregroundHook != 0) { UnhookWinEvent(foregroundHook); foregroundHook = 0; }
        if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
        GC.KeepAlive(callback);
        GC.KeepAlive(foregroundCallback);
    }
}
