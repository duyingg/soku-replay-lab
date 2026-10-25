using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Runtime.InteropServices;
using SokuReplayLab;

internal static class DockLayoutChecks
{
    private static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    private static readonly Dictionary<string, Size> Minimums = new() {
        ["game"] = new(420, 360), ["playback"] = new(300, 220), ["quick"] = new(300, 180),
        ["snapshot"] = new(300, 180), ["bookmarks"] = new(300, 160)
    };
    private static ReplayDockState Initial() => new(1, 2000, 1400, [
        new("main", new(0,0,.6,.7), ["game"], "game"),
        new("tools", new(.606,0,.394,.27), ["playback","quick"], "playback"),
        new("snapshot", new(.606,.279,.394,.27), ["snapshot"], "snapshot"),
        new("marks", new(.606,.558,.394,.27), ["bookmarks"], "bookmarks")], []);
    private static ReplayDockLayout Make() => new(Initial(), Minimums);
    public static void Run()
    {
        var model = Make();
        var baseline = JsonSerializer.Serialize(model.State);
        Require(model.MovePanel("quick", "main", ReplayDockZone.Center), "Panel merge failed");
        Require(model.MovePanel("quick", "main", ReplayDockZone.Bottom) && model.Undo(), "A tab cannot split out of its own group");
        Require(model.State.Slots.Length == 4 && model.State.Slots[0].Panels.SequenceEqual(["game","quick"]), "Merge lost panel/slot identity");
        Require(model.Close("game") && model.Close("quick"), "Closing panels failed");
        Require(model.State.Slots.Length == 3 && model.State.Slots.All(s => s.Panels.Length > 0), "Closing last panel left an empty region");
        Require(model.Open("quick"), "Reopen failed");
        var reopened = model.State.Slots.Single(s => s.Panels.Contains("quick")).Id;
        Require(model.MovePanel("game", reopened, ReplayDockZone.Center, 0), "Reopen at index failed");
        Require(model.MovePanel("quick", reopened, ReplayDockZone.Center, 0) && model.State.Slots.Single(s => s.Id == reopened).Panels[0] == "quick", "Reorder failed");
        foreach (var zone in new[] { ReplayDockZone.Left, ReplayDockZone.Right, ReplayDockZone.Top, ReplayDockZone.Bottom })
        {
            model = Make();
            Require(model.MovePanel("quick", "main", zone), "Edge docking failed: " + zone);
            Require(model.State.Slots.Length == 5 && model.State.Slots.Single(s => s.Id == "tools").Panels.SequenceEqual(["playback"]), "Edge docking removed source region");
            Require(model.Undo() && JsonSerializer.Serialize(model.State) == baseline, "Undo did not restore exact layout");
            Require(model.Redo() && model.State.Slots.Length == 5, "Redo failed");
        }
        model = Make();
        Require(model.Close("snapshot"), "Close standalone panel failed");
        var reopenedState = JsonSerializer.Deserialize<ReplayDockState>(JsonSerializer.Serialize(model.State))!;
        model = new ReplayDockLayout(reopenedState, Minimums);
        Require(model.Open("snapshot") && model.State.Slots.Length == 4, "Reopen after restart merged a standalone panel into another view");
        Require(model.State.Slots.Single(s => s.Panels.Contains("snapshot")).Panels.Length == 1, "Reopen hid an unrelated active view");
        model = Make();
        Require(model.MoveSlot("tools", "main", ReplayDockZone.Left), "Whole region move failed");
        Require(model.State.Slots.Count(s => s.Id == "tools") == 1 && model.State.Slots.Single(s => s.Id == "tools").Panels.Length == 2,
            "Moving region duplicated its ID or lost grouped panels");
        model = Make();
        Require(model.MoveSlot("tools", "main", ReplayDockZone.Center) && model.State.Slots.Length == 3, "Whole region merge failed");
        Require(model.State.Slots.Single(s => s.Id == "main").Panels.Length == 3, "Region merge lost tabs");
        Require(!model.MoveSlot("main", "main", ReplayDockZone.Left), "Self-drop was accepted");
        model = Make();
        Require(model.ResizeBoundary("main", "tools", -.02, true), "Shared boundary resize failed");
        Require(model.ResizeBoundary("main", "tools", -1, true) && !model.ResizeBoundary("main", "tools", -1, true), "Minimum size did not stop resizing");
        Require(model.Undo(), "Minimum-boundary undo failed");
        Require(model.Undo() && JsonSerializer.Serialize(model.State) == baseline, "Resize undo failed");
        Require(model.Delete("snapshot", "tools") && model.State.Slots.Length == 3, "Delete/move choice failed");
        Require(model.State.Slots.Single(s => s.Id == "tools").Panels.Contains("snapshot"), "Deleting region discarded content");
        var bad = Initial() with { Format = 999 };
        Require(!model.Restore(bad), "Unknown layout version accepted");
        Require(!model.Restore(Initial() with { Slots = [Initial().Slots[0], Initial().Slots[0]] }), "Duplicate region ID accepted");
        var unknown = Initial(); unknown.Slots[0] = unknown.Slots[0] with { Panels = ["unknown","game","game"] };
        Require(model.Restore(unknown) && model.State.Slots[0].Panels.SequenceEqual(["game"]), "Unknown/duplicate panels were not sanitized");
        var serialized = JsonSerializer.Serialize(model.State);
        var restored = new ReplayDockLayout(JsonSerializer.Deserialize<ReplayDockState>(serialized)!, Minimums);
        Require(JsonSerializer.Serialize(restored.State) == serialized, "Persistence changed layout");
        foreach (var slot in model.State.Slots.Skip(1).ToArray()) model.Delete(slot.Id);
        Require(model.Delete(model.State.Slots[0].Id) && model.State.Slots.Length == 1, "Deleting last region removed the root");
        CheckPartitions();
        CheckCurrentBehavior();
        Console.WriteLine("PASS: docking, four edge zones, tabs, automatic gap filling, whole-region moves, size limits, undo/redo and corrupt layouts.");
    }
    private static void CheckCurrentBehavior()
    {
        var model = Make();
        Require(model.Restore(model.State with { Returns = new() { ["quick"] = new(null!, ReplayDockZone.Left) } }),
            "An invalid reopen anchor crashed or discarded an otherwise valid layout");
        Require(model.State.Returns?.Count == 0, "Invalid reopen anchor survived sanitization");
        Require(model.MovePanel("snapshot", "main", ReplayDockZone.Center), "Single-panel merge failed");
        Require(model.State.Slots.Length == 3 && model.State.Slots.All(s => s.Panels.Length > 0), "Moving the last tab left an empty region");
        foreach (var zone in new[] { ReplayDockZone.Left, ReplayDockZone.Right, ReplayDockZone.Top, ReplayDockZone.Bottom })
        {
            model = Make();
            Require(model.MovePanel("snapshot", "main", zone, workspace: true), "Workspace edge docking failed: " + zone);
            var bounds = model.State.Slots.Single(s => s.Panels.Contains("snapshot")).Bounds;
            Require(zone is ReplayDockZone.Left or ReplayDockZone.Right
                ? Math.Abs(bounds.Height - 1) < .000001 : Math.Abs(bounds.Width - 1) < .000001,
                "Workspace edge did not span the workspace: " + zone);
            Require(model.State.Slots.Length == 4, "Workspace edge docking left an empty source");
        }
        model = Make();
        foreach (var panel in Minimums.Keys) Require(model.Close(panel), "Close all failed: " + panel);
        Require(model.State.Slots.Length == 1 && model.State.Slots[0].Panels.Length == 0, "Closing all panels lost the empty root");
        foreach (var panel in Minimums.Keys) Require(model.Open(panel), "Open all failed: " + panel);
        Require(model.State.Slots.SelectMany(s => s.Panels).Distinct().Count() == Minimums.Count, "Reopening all lost or duplicated panels");
        var tinyRatio = new ReplayDockLayout(new(2, 1000, 600, [
            new("a", new(0, 0, .5, 1), ["game"], "game"), new("b", new(.5, 0, .5, 1), ["quick"], "quick")], [],
            Partition: new(null, true, .01, new("a"), new("b"))), Minimums);
        Require(tinyRatio.State.Width == 1000 && tinyRatio.State.Height == 600, "Saved ratio enlarged a workspace that fits its panels");
        Require(tinyRatio.ResizeBoundary("a", "b", .05, true), "Clamped ratio cannot be resized from its visible position");
        Require(tinyRatio.State.Slots[0].Bounds.Width * 1000 > 460, "Divider jumped from its stale saved ratio");
        for (var i = 0; i < 100; i++)
        {
            var panel = Minimums.Keys.ElementAt(i % Minimums.Count);
            Require(model.Close(panel) && model.Open(panel), "Repeated close/open failed");
            model = new ReplayDockLayout(JsonSerializer.Deserialize<ReplayDockState>(JsonSerializer.Serialize(model.State))!, Minimums);
            Require(model.State.Slots.SelectMany(s => s.Panels).Distinct().Count() == Minimums.Count, "Persistence lost a reopened panel");
        }
    }
    private static void CheckPartitions()
    {
        void Covered(ReplayDockLayout layout)
        {
            var state = layout.State;
            var area = state.Slots.Sum(s => s.Bounds.Width * s.Bounds.Height);
            var boundaries = layout.Boundaries().ToArray();
            Require(boundaries.Length == state.Slots.Length - 1, "A partition has redundant or missing separators");
            foreach (var edge in boundaries) {
                var b = layout.BoundaryBounds(edge.First, edge.Second, edge.Across); area += b.Width * b.Height;
            }
            Require(Math.Abs(area - 1) < .000001, "A docking operation left an unoccupied hole");
            foreach (var a in state.Slots) {
                var min = layout.Minimum(a);
                Require(a.Bounds.Width * state.Width + .01 >= min.Width && a.Bounds.Height * state.Height + .01 >= min.Height, "Nested partition violated minimum size");
                foreach (var b in state.Slots.Where(s => string.CompareOrdinal(a.Id, s.Id) < 0))
                    Require(Math.Min(a.Bounds.X + a.Bounds.Width, b.Bounds.X + b.Bounds.Width) <= Math.Max(a.Bounds.X, b.Bounds.X) + .000001 ||
                        Math.Min(a.Bounds.Y + a.Bounds.Height, b.Bounds.Y + b.Bounds.Height) <= Math.Max(a.Bounds.Y, b.Bounds.Y) + .000001, "Partitions overlap");
            }
        }
        // Two independent vertical separators happen to have the same Y coordinate.
        var grid = new ReplayDockLayout(new(1, 2000, 1400, [
            new("a", new(0,0,.49,.49), ["game"], "game"), new("b", new(0,.51,.49,.49), ["quick"], "quick"),
            new("c", new(.51,0,.49,.49), ["snapshot"], "snapshot"), new("d", new(.51,.51,.49,.49), ["bookmarks"], "bookmarks")], []), Minimums);
        var right = grid.State.Slots.Where(s => s.Id is "c" or "d").Select(s => s.Bounds).ToArray();
        Require(grid.ResizeBoundary("a", "b", .04, false), "Independent vertical separator failed");
        Require(grid.State.Slots.Where(s => s.Id is "c" or "d").Select(s => s.Bounds).SequenceEqual(right), "Resizing one column changed a disconnected column");
        Covered(grid);
        foreach (var id in new[] { "main", "tools", "snapshot", "marks" }) {
            var layout = Make(); Require(layout.Delete(id), "Slot deletion failed: " + id); Covered(layout);
            Require(layout.Undo(), "Deletion undo failed"); Covered(layout);
        }
        var random = new Random(20261025); var stress = Make();
        for (var i = 0; i < 180; i++) {
            var slots = stress.State.Slots; var target = slots[random.Next(slots.Length)];
            switch (i % 5) {
                case 0: if (slots.Length < 12) stress.MovePanel(Minimums.Keys.ElementAt(random.Next(5)), target.Id, (ReplayDockZone)random.Next(5)); break;
                case 1: stress.MoveSlot(slots[random.Next(slots.Length)].Id, target.Id, (ReplayDockZone)random.Next(5)); break;
                case 2: if (slots.Length > 1) stress.Delete(target.Id, slots.First(s => s.Id != target.Id).Id); break;
                case 3: var edges = stress.Boundaries().ToArray(); if (edges.Length > 0) { var e = edges[random.Next(edges.Length)]; stress.ResizeBoundary(e.First, e.Second, .01, e.Across); } break;
                case 4: stress.Arrange(900 + random.Next(900), 600 + random.Next(600)); break;
            }
            Covered(stress);
        }
        Require(!stress.Restore(stress.State with { Partition = new("missing") }), "Corrupt partition accepted");
        Console.WriteLine("PASS: complete area coverage, isolated separators, nested minimums, deletion of every region and 180 mixed operations.");
    }
    public static async Task RunViewAsync(string output)
    {
        Directory.CreateDirectory(output);
        var workspace = new ReplayWorkspace((_, _) => { });
        var frames = new Dictionary<string, Border>();
        var labels = new Dictionary<string, TextBox>();
        var defaults = new Dictionary<string, ReplayPanelLayout> {
            ["game"] = new(0,0,700,650), ["playback"] = new(712,0,360,300),
            ["snapshot"] = new(712,312,360,230), ["quick"] = new(712,554,360,240), ["bookmarks"] = new(712,806,360,180)
        };
        foreach (var (id, bounds) in defaults)
        {
            var content = new TextBox { Text = "保留的状态：" + id, AcceptsReturn = true };
            labels[id] = content;
            frames[id] = workspace.AddPanel(id, id, content, Minimums[id].Width, Minimums[id].Height,
                _ => bounds, null, Brushes.WhiteSmoke, Brushes.SlateGray, Brushes.DarkSlateBlue, Brushes.White);
        }
        var window = new Window { Width = 1440, Height = 900, Content = workspace, Title = "面板回归验证", Topmost = true };
        window.Show(); window.Activate(); window.UpdateLayout(); workspace.CompleteRegistration();
        try
        {
        await Task.Delay(250);
        var parents = frames.ToDictionary(x => x.Key, x => VisualTreeHelper.GetParent(x.Value));
        var migrated = new ReplayWorkspace((_, _) => { });
        migrated.AddPanel("game", "game", new TextBox(), 420, 360, _ => defaults["game"],
            new(0, 0, 600, 650), Brushes.White, Brushes.Gray, Brushes.Navy, Brushes.White);
        migrated.AddPanel("quick", "quick", new TextBox(), 300, 180, _ => defaults["quick"],
            new(612, 0, 400, 650), Brushes.White, Brushes.Gray, Brushes.Navy, Brushes.White);
        migrated.CompleteRegistration();
        Require(migrated.DefaultLayout.Partition != migrated.Layout.State.Partition,
            "Legacy saved positions overwrote the independent default layout");
        var state = ReplayDockLayout.Copy(workspace.Layout.State);
        var beforeDrag = JsonSerializer.Serialize(workspace.Layout.State);
        var tab = Descendants<Thumb>(workspace).First(t => AutomationProperties.GetName(t) == "quick");
        var from = tab.PointToScreen(new Point(Math.Max(4, tab.ActualWidth / 2), 18));
        var targetBounds = workspace.LayoutOf("snapshot");
        var canvas = (Canvas)parents["snapshot"];
        var to = canvas.PointToScreen(new Point(targetBounds.X + targetBounds.Width / 2, targetBounds.Y + targetBounds.Height / 2));
        GetCursorPos(out var previousPointer);
        try
        {
            SetCursorPos((int)from.X, (int)from.Y); mouse_event(2, 0, 0, 0, 0);
            await Task.Delay(80);
            SetCursorPos((int)to.X, (int)to.Y); await Task.Delay(120);
            Require(workspace.IsDragging, "Mouse drag did not start");
            // Escape is routed through the same control which owns the gesture.
            var source = PresentationSource.FromVisual(window)!;
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            workspace.RaiseEvent(escape); mouse_event(4, 0, 0, 0, 0); await Task.Delay(80);
            Require(!workspace.IsDragging && JsonSerializer.Serialize(workspace.Layout.State) == beforeDrag, "Escape changed the layout");
            tab = Descendants<Thumb>(workspace).First(t => AutomationProperties.GetName(t) == "quick");
            from = tab.PointToScreen(new Point(Math.Max(4, tab.ActualWidth / 2), 18));
            SetCursorPos((int)from.X, (int)from.Y); mouse_event(2, 0, 0, 0, 0); await Task.Delay(60);
            SetCursorPos((int)to.X, (int)to.Y); await Task.Delay(100); mouse_event(4, 0, 0, 0, 0); await Task.Delay(100);
            Require(workspace.Layout.State.Slots.Single(s => s.Id == "home-snapshot").Panels.Contains("quick"), "Mouse drop did not merge tabs");
            Require(workspace.Undo(), "Mouse docking undo failed");
            window.UpdateLayout(); await Task.Delay(60);
            var divider = Descendants<Thumb>(workspace).First(t => t.Cursor == Cursors.SizeWE && t.ActualHeight > 0);
            var dividerPoint = divider.PointToScreen(new Point(5, Math.Min(80, divider.ActualHeight / 2)));
            var originalWidth = workspace.LayoutOf("game").Width;
            SetCursorPos((int)dividerPoint.X, (int)dividerPoint.Y); mouse_event(2,0,0,0,0); await Task.Delay(60);
            SetCursorPos((int)dividerPoint.X + 20, (int)dividerPoint.Y); await Task.Delay(80); mouse_event(4,0,0,0,0); await Task.Delay(80);
            Require(workspace.LayoutOf("game").Width > originalWidth + 5, "Mouse divider drag did not resize neighbours");
            divider = Descendants<Thumb>(workspace).First(t => t.Cursor == Cursors.SizeWE && t.ActualHeight > 0);
            dividerPoint = divider.PointToScreen(new Point(5, Math.Min(80, divider.ActualHeight / 2)));
            SetCursorPos((int)dividerPoint.X, (int)dividerPoint.Y);
            for (var click = 0; click < 2; click++)
            { mouse_event(2,0,0,0,0); mouse_event(4,0,0,0,0); await Task.Delay(80); }
            Require(Math.Abs(workspace.LayoutOf("game").Width - originalWidth) < 3, "Double-click did not restore original divider ratio");
            foreach (var (zone, dx, dy) in new[] { (ReplayDockZone.Left, -42, 0), (ReplayDockZone.Right, 42, 0),
                (ReplayDockZone.Top, 0, -42), (ReplayDockZone.Bottom, 0, 42) })
            {
                window.UpdateLayout(); await Task.Delay(60);
                tab = Descendants<Thumb>(workspace).First(t => AutomationProperties.GetName(t) == "quick");
                from = tab.PointToScreen(new Point(Math.Max(4, tab.ActualWidth / 2), 18));
                var gameBounds = workspace.LayoutOf("game");
                to = canvas.PointToScreen(new Point(gameBounds.X + gameBounds.Width / 2 + dx, gameBounds.Y + gameBounds.Height / 2 + dy));
                SetCursorPos((int)from.X, (int)from.Y); mouse_event(2,0,0,0,0); await Task.Delay(60);
                SetCursorPos((int)to.X, (int)to.Y); await Task.Delay(100); mouse_event(4,0,0,0,0); await Task.Delay(100);
                var placed = workspace.Layout.State.Slots.Single(s => s.Panels.Contains("quick"));
                var game = workspace.Layout.State.Slots.Single(s => s.Panels.Contains("game"));
                Require(placed.Id != "home-quick" && placed.Id != game.Id && (zone switch {
                    ReplayDockZone.Left => placed.Bounds.X < game.Bounds.X,
                    ReplayDockZone.Right => placed.Bounds.X > game.Bounds.X,
                    ReplayDockZone.Top => placed.Bounds.Y < game.Bounds.Y,
                    _ => placed.Bounds.Y > game.Bounds.Y }), "Visible docking arrow did not match its action: " + zone);
                Require(workspace.Undo(), "Arrow drop undo failed");
            }
        }
        finally { mouse_event(4, 0, 0, 0, 0); SetCursorPos(previousPointer.X, previousPointer.Y); }
        Require(workspace.MovePanel("quick", "home-snapshot", ReplayDockZone.Center), "UI panel merge failed");
        window.UpdateLayout();
        Require(frames["quick"].IsVisible && !frames["snapshot"].IsVisible, "Active tab visibility incorrect");
        var focusedTab = Descendants<Thumb>(workspace).First(t => AutomationProperties.GetName(t) == "quick"); focusedTab.Focus();
        foreach (var key in new[] { Key.Left, Key.Right }) {
            var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            ((UIElement)Keyboard.FocusedElement).RaiseEvent(keyEvent);
            var expected = key == Key.Left ? "snapshot" : "quick";
            Require(Keyboard.FocusedElement is Thumb focused && AutomationProperties.GetName(focused) == expected && frames[expected].IsVisible,
                "Keyboard tab selection lost focus after re-render: " + key + ", " + (Keyboard.FocusedElement is DependencyObject element ? AutomationProperties.GetName(element) : "none"));
        }
        Require(workspace.ClosePanel("quick") && workspace.OpenPanel("quick"), "UI close/reopen failed");
        Require(workspace.SaveWorkspace("组合") && workspace.LoadWorkspace("组合"), "Named workspace save/load failed");
        foreach (var (id, frame) in frames)
            Require(ReferenceEquals(parents[id], VisualTreeHelper.GetParent(frame)) && labels[id].Text.EndsWith(id), "Docking rebuilt a business view");
        foreach (var size in new[] { new Size(1440,900), new Size(1080,680), new Size(800,500) })
        {
            window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); await Task.Delay(50);
            foreach (var (id, frame) in frames)
                Require(frame.Width + .5 >= Minimums[id].Width && frame.Height + .5 >= Minimums[id].Height, "Small window violated panel minimums");
        }
        window.Width = 1440; window.Height = 900;
        Require(workspace.Layout.Restore(state), "Restore view fixture failed");
        // Force a committed render after the external fixture restore.
        workspace.SaveWorkspace("默认验证"); workspace.LoadWorkspace("默认验证");
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "dock-panels.png"))) png.Save(stream);
        File.WriteAllText(Path.Combine(output, "dock-view.json"), JsonSerializer.Serialize(new {
            slots = workspace.Layout.State.Slots.Length, businessViewsPreserved = true, tabs = true, namedWorkspace = true,
            positions = defaults
        }));
        Console.WriteLine("PASS: panel UI, active tabs, reopen, named workspaces and stable business-view parents.");
        }
        finally { window.Close(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var item in Descendants<T>(child)) yield return item; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Pointer { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pointer point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
