using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SokuReplayLab;

/// <summary>Owns the host HWND while cross-process window calls run off the UI thread.</summary>
public sealed class GameSurfaceHost : HwndHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private readonly GameInstance game;
    private readonly Action<string> report;
    private readonly Action? stateChanged;
    private nint host;
    private GameWindowLease? lease;
    private bool attaching, releasing, layingOut, detachRequested, destroyed;
    private int layoutFailures;
    private DateTime nextLayoutAttempt;
    public string? LayoutError { get; private set; }
    public int GamePid => game.Pid;
    public nint GameWindow => game.Window;
    public bool IsAttached => lease is not null && !releasing;
    public bool IsBusy => attaching || releasing || layingOut;
    public long HostHandle => host.ToInt64();
    public (int Width, int Height) HostPixelSize => PixelSize();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    private (int Width, int Height) PixelSize()
    {
        if (host != 0 && GetClientRect(host, out var rect) &&
            rect.Right > rect.Left && rect.Bottom > rect.Top)
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return (Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
    }

    public GameSurfaceHost(GameInstance game, Action<string> report, double scale = 1,
        Action? stateChanged = null)
    {
        this.game = game;
        this.report = report;
        this.stateChanged = stateChanged;
        Width = 440 * scale;
        Height = 320 * scale;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        SizeChanged += (_, _) => Reconcile();
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Reconcile),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ConnectionTrace.Write("surface.build.begin", game.Pid);
        var initial = PixelSize();
        host = CreateWindowExW(0, "STATIC", "ReplayLab game surface",
            0x40000000 | 0x10000000 | 0x04000000, 0, 0,
            initial.Width, initial.Height, hwndParent.Handle, 0, 0, 0);
        if (host == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        ConnectionTrace.Write("surface.host-created", game.Pid, $"host={host.ToInt64()}");
        attaching = true;
        var target = host;
        _ = Task.Run(() => {
            GameWindowLease? acquired = null;
            Exception? failure = null;
            try
            {
                ConnectionTrace.Write("surface.lease.begin", game.Pid);
                acquired = new GameWindowLease(game, target);
                acquired.Attach(initial.Width, initial.Height);
                ConnectionTrace.Write("surface.lease.attached", game.Pid);
            }
            catch (Exception error)
            {
                failure = error;
                ConnectionTrace.Write("surface.lease.failed", game.Pid,
                    error.GetType().Name + ": " + error.Message);
                if (acquired?.Release() != false) acquired = null;
            }
            Dispatcher.BeginInvoke(new Action(() => {
                attaching = false;
                lease = acquired;
                if (failure is not null) report($"游戏画面嵌入失败：{failure.Message}");
                if (detachRequested) Detach();
                else if (lease is not null && failure is null) Reconcile();
                stateChanged?.Invoke();
            }));
        });
        return new HandleRef(this, host);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Render and Closing call Detach first. Never destroy the parent while
        // a cross-process SetParent or restore is still running.
        if (!Detach()) return;
        destroyed = true;
        if (hwnd.Handle != 0) DestroyWindow(hwnd.Handle);
        host = 0;
    }

    public bool Detach()
    {
        detachRequested = true;
        if (attaching || releasing || layingOut) return false;
        if (lease is null) return true;
        releasing = true;
        ConnectionTrace.Write("surface.release.begin", game.Pid);
        var current = lease;
        _ = Task.Run(() => {
            bool restored;
            Exception? failure = null;
            try { restored = current.Release(); }
            catch (Exception error) { restored = false; failure = error; }
            ConnectionTrace.Write(restored ? "surface.release.done" : "surface.release.failed",
                game.Pid, failure?.Message);
            Dispatcher.BeginInvoke(new Action(() => {
                releasing = false;
                if (restored) lease = null;
                else LayoutError = failure is null
                    ? "游戏窗口尚未还原；请等待游戏响应后重试。"
                    : $"游戏窗口还原失败：{failure.Message}";
                if (restored) stateChanged?.Invoke();
            }));
        });
        return false;
    }

    public void Reconcile()
    {
        if (destroyed || detachRequested || attaching || releasing || layingOut || lease is null)
            return;
        if (DateTime.UtcNow < nextLayoutAttempt) return;
        var pixels = PixelSize();
        var current = lease;
        layingOut = true;
        _ = Task.Run(() => {
            Exception? failure = null;
            try { current.Layout(pixels.Width, pixels.Height); }
            catch (Exception error) { failure = error; }
            if (failure is not null)
                ConnectionTrace.Write("surface.layout.failed", game.Pid, failure.Message);
            Dispatcher.BeginInvoke(new Action(() => {
                layingOut = false;
                if (failure is null)
                {
                    layoutFailures = 0;
                    nextLayoutAttempt = default;
                    LayoutError = null;
                }
                else
                {
                    if (++layoutFailures >= 5) LayoutError = failure.Message;
                    nextLayoutAttempt = DateTime.UtcNow.AddSeconds(2);
                }
                if (detachRequested) Detach();
                else if (failure is null && PixelSize() != pixels) Reconcile();
            }));
        });
    }
}
