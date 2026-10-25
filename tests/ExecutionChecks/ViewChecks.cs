using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using System.Windows.Threading;
using SokuReplayLab;

internal static class ViewChecks
{
    public static void Run(string trace, string output)
    {
        Directory.CreateDirectory(output);
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string[] characters = ["reimu", "marisa", "sakuya", "alice", "patchouli", "youmu",
            "remilia", "yuyuko", "yukari", "suika", "udonge", "aya", "komachi", "iku",
            "tenshi", "sanae", "chirno", "meirin", "utsuho", "suwako"];
        using var cards = ZipFile.OpenRead(Path.Combine(project, "CardArt.zip"));
        var count = 0;
        foreach (var entry in cards.Entries.Where(entry => entry.Name.EndsWith(".png")))
        {
            var folder = entry.FullName.Split('/')[0];
            var character = folder == "common" ? 0 : Array.IndexOf(characters, folder);
            var id = int.Parse(Path.GetFileNameWithoutExtension(entry.Name));
            var image = CardArt.Get(character, id);
            if (image is null || !image.IsFrozen || image.PixelWidth <= 0 || image.PixelHeight <= 0)
                throw new IOException("Card image failed: " + entry.FullName);
            count++;
        }
        if (count != 483) throw new IOException("Card image count differs: " + count);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow();
        var data = SessionLoader.Load(trace, project);
        Exception? failure = null;
        window.Loaded += async (_, _) => {
            try
            {
                var result = window.SmokeNavigationCache(data);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainWindow).GetMethod("NavigateToPage", flags)!.Invoke(window, [0]);
                window.UpdateLayout();
                var transport = (FrameworkElement)typeof(MainWindow).GetField("transportPlayButton", flags)!.GetValue(window)!;
                var root = (FrameworkElement)window.Content;
                var transportPoint = transport.TransformToAncestor(root).Transform(new Point());
                if (!transport.IsVisible || transportPoint.Y + transport.ActualHeight > root.ActualHeight + 1)
                    throw new IOException("Docking hid or clipped the global playback control.");
                var quickContent = (ContentControl)typeof(MainWindow).GetField("replayQuickContent", flags)!.GetValue(window)!;
                if (data.Events.Count <= 10) throw new IOException("Quick navigation check needs more than ten events.");
                var quick = quickContent.Content as ListBox;
                if (quick?.Items.Count != data.Events.Count)
                    throw new IOException("Quick navigation omitted events.");
                quick.ScrollIntoView(quick.Items[^1]);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                quick.UpdateLayout();
                if (quick.ItemContainerGenerator.ContainerFromIndex(quick.Items.Count - 1) is null)
                    throw new IOException("Last quick navigation point cannot be reached.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                await window.SmokeTableWheelAsync(data);
                window.ExportPreview(data, Path.Combine(output, "replay-light.png"), 0, "经典");
                window.ExportPreview(data, Path.Combine(output, "replay-dark.png"), 0, "深色");
                window.ExportPreview(data, Path.Combine(output, "curves.png"), 3);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                File.WriteAllText(Path.Combine(output, "view-check.json"), JsonSerializer.Serialize(new {
                    images = count, result.FirstStatsMs, result.FirstMultiMs, result.FirstCurvesMs,
                    result.CachedStatsMs, result.CachedMultiMs, result.CachedCurvesMs,
                    result.ReusedStats, result.ReusedMulti, result.ReusedCurves, result.InvalidatedAfterFilter,
                    scrolling = true, playbackRefreshPreservesCache = true,
                    quickPoints = quick.Items.Count, lastQuickPointReachable = true
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (!result.ReusedStats || !result.ReusedMulti || !result.ReusedCurves || !result.InvalidatedAfterFilter)
                    throw new IOException("Navigation cache did not preserve or invalidate the expected view.");
                CheckPanelSettings(window, output);
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        };
        app.Run(window);
        if (failure is not null) throw failure;
        Console.WriteLine("PASS: 483 card images, navigation cache, filter invalidation and table scrolling.");
    }
    private static void CheckPanelSettings(MainWindow window, string output)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags | BindingFlags.DeclaredOnly)!.Invoke(window, args);
        void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
        IEnumerable<CheckBox> Checks(DependencyObject node)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is CheckBox check) yield return check;
                foreach (var nested in Checks(child)) yield return nested;
            }
        }
        CheckBox Option(string name)
        {
            window.UpdateLayout();
            return Checks(window).Single(c => Equals(c.Content, name));
        }
        var original = Get("session"); var cursor = Get("cursor");
        Set("root", Path.GetFullPath(output));
        Set("shutdownReady", false);
        try
        {
            Call("NavigateToPage", 9); Set("settingsSection", "回放面板"); Call("Render");
            Option("固定快照").IsChecked = false;
            Call("NavigateToPage", 0);
            var dock = (ReplayWorkspace)Get("replayWorkspace")!; dock.CompleteRegistration();
            Require(!dock.Layout.State.Slots.Any(s => s.Panels.Contains("snapshot")), "Settings did not close the snapshot panel");
            Require(dock.ClosePanel("quick"), "Closing quick panel failed");
            Call("NavigateToPage", 9);
            Require(Option("快速定位").IsChecked == false, "Settings did not reflect a panel closed in replay");
            Option("快速定位").IsChecked = true;
            Set("sessionLoading", true);
            var before = JsonSerializer.Serialize(Get("replayDockState"));
            Option("对局画面与当前状态").IsChecked = false;
            Require(Option("对局画面与当前状态").IsChecked == true && before == JsonSerializer.Serialize(Get("replayDockState")),
                "A busy replay accepted a settings layout change");
            Set("sessionLoading", false);
            Call("NavigateToPage", 0);
            dock = (ReplayWorkspace)Get("replayWorkspace")!; dock.CompleteRegistration();
            Require(dock.Layout.State.Slots.Any(s => s.Panels.Contains("quick")) && !dock.Layout.State.Slots.Any(s => s.Panels.Contains("snapshot")),
                "Returning from settings lost panel visibility");
            Require(ReferenceEquals(original, Get("session")) && Equals(cursor, Get("cursor")), "Panel settings changed replay data or position");
            File.WriteAllText(Path.Combine(output, "panel-settings-check.json"), JsonSerializer.Serialize(new {
                closeFromSettings = true, reopenFromSettings = true, busyChangeRejected = true, replayPreserved = true
            }));
            Console.WriteLine("PASS: settings panel switches, replay-page round trips, busy rejection and replay preservation.");
        }
        finally { Set("sessionLoading", false); Set("shutdownReady", true); }
    }
}
