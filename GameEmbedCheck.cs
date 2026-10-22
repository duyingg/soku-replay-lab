using System.IO;
using System.Text.Json;
using System.Windows;

namespace SokuReplayLab;

public static class GameEmbedCheck
{
    public static void Run(string output)
    {
        var game = GameDiscovery.Find().FirstOrDefault()
            ?? throw new InvalidOperationException("未找到游戏主窗口。");
        var before = GameWindowLease.Inspect(game.Window);
        GameWindowState? during = null;
        GameWindowState? after = null;
        (int Width, int Height) hostPixels = default;
        var errors = new List<string>();
        var surface = new GameSurfaceHost(game, errors.Add);
        var window = new Window {
            Title = "ReplayLab 游戏画面嵌入验证", Width = 700, Height = 530,
            Content = surface, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Closing += (_, e) => { if (!surface.Detach()) e.Cancel = true; };
        window.Loaded += async (_, _) => {
            try
            {
                var attachDeadline = DateTime.UtcNow.AddSeconds(8);
                while (!surface.IsAttached && errors.Count == 0 &&
                    DateTime.UtcNow < attachDeadline) await Task.Delay(50);
                hostPixels = surface.HostPixelSize;
                during = GameWindowLease.Inspect(game.Window);
                if (!surface.IsAttached || during.Parent != surface.HostHandle)
                    errors.Add("游戏窗口未保持在 WPF 画面宿主中。");
                if (during.Width != hostPixels.Width || during.Height != hostPixels.Height)
                    errors.Add($"游戏窗口 {during.Width}×{during.Height} 未填满宿主 {hostPixels.Width}×{hostPixels.Height}。");
                var detachDeadline = DateTime.UtcNow.AddSeconds(8);
                while (!surface.Detach() && DateTime.UtcNow < detachDeadline)
                    await Task.Delay(50);
                if (surface.IsBusy || surface.IsAttached) errors.Add("游戏窗口释放失败。");
                else after = GameWindowLease.Inspect(game.Window);
            }
            catch (Exception exception) { errors.Add(exception.ToString()); }
            finally
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new {
                    game.Pid, Window = game.Window.ToInt64(), before, during,
                    hostPixels = new { hostPixels.Width, hostPixels.Height }, after, errors,
                    restored = after is not null && after.Parent == before.Parent &&
                        after.Style == before.Style && after.ExStyle == before.ExStyle &&
                        after.ShowCommand == before.ShowCommand && after.Visible == before.Visible &&
                        (before.ShowCommand == 2 || after.Left == before.Left &&
                         after.Top == before.Top && after.Width == before.Width &&
                         after.Height == before.Height)
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }
        };
        new Application().Run(window);
    }
}
