using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;

namespace SokuReplayLab;

public sealed partial class ReplayWorkspace
{
    private readonly Popup preview = new() { AllowsTransparency = true, StaysOpen = true,
        IsHitTestVisible = false, Placement = PlacementMode.Relative };
    private readonly Popup dragHint = new() { AllowsTransparency = true, StaysOpen = true,
        IsHitTestVisible = false, Placement = PlacementMode.Relative };
    private string? movingPanel, movingSlot;
    private ReplayDockState? dragStart;
    private Point dragOrigin;
    private bool dragMoved, touchDrag;
    private DateTime dragTime;
    private (string Id, ReplayDockZone Zone, int? Index, bool Workspace)? drop;
    public bool IsDragging => dragStart is not null;
    private Thumb DragThumb(string text, string? panel, string slot, Brush? foreground = null)
    {
        var thumb = new Thumb { Cursor = Cursors.Hand, Focusable = true, MinWidth = 22, Height = 36,
            Foreground = foreground ?? titleText, ToolTip = panel is null ? "拖动整个面板组" : "拖动标题停靠到边缘，或拖到中央合并标签" };
        var root = new FrameworkElementFactory(typeof(Border)); root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.TextProperty, text);
        label.SetValue(TextBlock.ForegroundProperty, foreground ?? titleText); label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 7, 0));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(label); thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = root };
        AutomationProperties.SetName(thumb, panel is null ? "移动整个槽位" : views[panel].Title);
        thumb.DragStarted += (_, _) => {
            if (!Allowed) return;
            thumb.Focus(); movingPanel = panel; movingSlot = slot;
            dragStart = ReplayDockLayout.Copy(Layout.State);
            // Select without rebuilding this captured Thumb or its parent.
            if (panel is not null) { Layout.Activate(slot, panel); RenderBounds(); }
            dragOrigin = Mouse.GetPosition(canvas);
            dragTime = DateTime.UtcNow; touchDrag = Stylus.CurrentStylusDevice?.TabletDevice.Type == TabletDeviceType.Touch;
            dragMoved = false; drop = null;
        };
        thumb.DragDelta += (_, _) => UpdateDrag();
        thumb.DragCompleted += (_, e) => FinishDrag(e.Canceled);
        return thumb;
    }
    private void UpdateDrag()
    {
        if (dragStart is null || !Allowed) { CancelGesture(); return; }
        var point = Mouse.GetPosition(canvas);
        if ((!dragMoved && (point - dragOrigin).Length < 6) ||
            (touchDrag && DateTime.UtcNow - dragTime < TimeSpan.FromMilliseconds(500))) return;
        dragMoved = true; drop = null;
        dragHint.PlacementTarget = viewport;
        dragHint.Child = new Border { Background = titleBrush, Padding = new Thickness(8, 5, 8, 5),
            Child = new TextBlock { Text = movingPanel is null ? "移动整个槽位" : views[movingPanel].Title, Foreground = titleText } };
        dragHint.HorizontalOffset = point.X - viewport.HorizontalOffset + 14;
        dragHint.VerticalOffset = point.Y - viewport.VerticalOffset + 14;
        dragHint.IsOpen = true;
        if (point.X < 0 || point.Y < 0 || point.X > canvas.Width || point.Y > canvas.Height)
        { preview.IsOpen = false; return; }
        ReplayDockZone? outer = point.X < 22 ? ReplayDockZone.Left : point.X > canvas.Width - 22 ? ReplayDockZone.Right :
            point.Y < 18 ? ReplayDockZone.Top : point.Y > canvas.Height - 22 ? ReplayDockZone.Bottom : null;
        var target = Layout.State.Slots.LastOrDefault(s => {
            var b = PixelBounds(s.Bounds); return new Rect(b.X, b.Y, b.Width, b.Height).Contains(point);
        });
        if (outer is not null)
            target = Layout.State.Slots.FirstOrDefault(s => s.Id != movingSlot) ?? target;
        if (target is null || movingPanel is null && target.Id == movingSlot) { preview.IsOpen = false; return; }
        var bounds = outer is null ? PixelBounds(target.Bounds) : new ReplayPanelLayout(0, 0, canvas.Width, canvas.Height);
        var x = (point.X - bounds.X) / bounds.Width; var y = (point.Y - bounds.Y) / bounds.Height;
        var zone = x < .23 ? ReplayDockZone.Left : x > .77 ? ReplayDockZone.Right :
            y < .23 ? ReplayDockZone.Top : y > .77 ? ReplayDockZone.Bottom : ReplayDockZone.Center;
        // The five visible targets and their hit areas must describe the same action.
        foreach (var (candidate, dx, dy) in new[] { (ReplayDockZone.Center, 0, 0),
            (ReplayDockZone.Left, -42, 0), (ReplayDockZone.Right, 42, 0),
            (ReplayDockZone.Top, 0, -42), (ReplayDockZone.Bottom, 0, 42) })
            if (new Rect(bounds.X + bounds.Width / 2 + dx - 18, bounds.Y + bounds.Height / 2 + dy - 18, 36, 36).Contains(point))
                zone = candidate;
        if (outer is not null) zone = outer.Value;
        int? insertion = null;
        if (outer is null && movingPanel is not null && point.Y < bounds.Y + 38)
        {
            zone = ReplayDockZone.Center;
            insertion = target.Panels.Where(p => p != movingPanel).Count(p =>
                tabHandles.TryGetValue(p, out var tab) && tab.TranslatePoint(new Point(tab.ActualWidth / 2, 18), canvas).X < point.X);
        }
        var trial = new ReplayDockLayout(Layout.State with { Width = canvas.Width, Height = canvas.Height }, views.ToDictionary(v => v.Key, v => v.Value.Minimum));
        var valid = movingPanel is not null ? trial.MovePanel(movingPanel, target.Id, zone, insertion, outer is not null)
            : trial.MoveSlot(movingSlot!, target.Id, zone, outer is not null);
        if (movingPanel is not null && target.Id == movingSlot && zone == ReplayDockZone.Center) valid = true;
        if (!valid) { ShowPreview(bounds, zone, "空间不足或无效位置，松手取消"); return; }
        drop = (target.Id, zone, insertion, outer is not null);
        var placed = trial.State.Slots.FirstOrDefault(s => movingPanel is not null ? s.Panels.Contains(movingPanel) :
            zone == ReplayDockZone.Center ? s.Id == target.Id : s.Id == movingSlot);
        ShowPreview(bounds, zone, outer is not null ? "停靠到工作区边缘" : movingPanel is null ? "移动整个面板组" : "移动“" + views[movingPanel].Title + "”",
            placed is null ? null : new ReplayPanelLayout(placed.Bounds.X * trial.State.Width, placed.Bounds.Y * trial.State.Height, placed.Bounds.Width * trial.State.Width, placed.Bounds.Height * trial.State.Height));
    }
    private void ShowPreview(ReplayPanelLayout bounds, ReplayDockZone zone, string caption, ReplayPanelLayout? actual = null)
    {
        // Popup has its own HWND so docking feedback remains visible over the
        // embedded game's native child window (WPF airspace).
        var overlay = new Canvas { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false };
        var rect = new Rect(0, 0, bounds.Width, bounds.Height);
        if (zone == ReplayDockZone.Left) rect.Width /= 2;
        if (zone == ReplayDockZone.Right) { rect.X = rect.Width / 2; rect.Width /= 2; }
        if (zone == ReplayDockZone.Top) rect.Height /= 2;
        if (zone == ReplayDockZone.Bottom) { rect.Y = rect.Height / 2; rect.Height /= 2; }
        if (actual is not null) rect = new Rect(actual.X - bounds.X, actual.Y - bounds.Y, actual.Width, actual.Height);
        var fill = new Border { Background = accent, Opacity = .25, BorderBrush = titleText, BorderThickness = new Thickness(2) };
        Place(fill, rect.X, rect.Y, rect.Width, rect.Height); overlay.Children.Add(fill);
        var cx = bounds.Width / 2; var cy = bounds.Height / 2;
        foreach (var (z, dx, dy, text) in new[] { (ReplayDockZone.Center, 0, 0, "■"),
            (ReplayDockZone.Left, -42, 0, "←"), (ReplayDockZone.Right, 42, 0, "→"),
            (ReplayDockZone.Top, 0, -42, "↑"), (ReplayDockZone.Bottom, 0, 42, "↓") })
        {
            var cue = new Border { Background = z == zone ? accent : titleBrush, BorderBrush = titleText,
                BorderThickness = new Thickness(1), Child = new TextBlock { Text = text, Foreground = z == zone ? onAccent : titleText,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Place(cue, cx + dx - 18, cy + dy - 18, 36, 36); overlay.Children.Add(cue);
        }
        var description = new Border { Background = titleBrush, Padding = new Thickness(8),
            Child = new TextBlock { Text = caption, Foreground = titleText } };
        Canvas.SetLeft(description, 8); Canvas.SetTop(description, 8); overlay.Children.Add(description);
        preview.Child = overlay; preview.HorizontalOffset = bounds.X - viewport.HorizontalOffset;
        preview.VerticalOffset = bounds.Y - viewport.VerticalOffset; preview.IsOpen = true;
    }
    private void FinishDrag(bool cancelled)
    {
        if (dragStart is null) return;
        // MouseUp can arrive without a final DragDelta (fast motion, HWND
        // boundaries). Never commit the last preview at an obsolete position.
        if (!cancelled && dragMoved) UpdateDrag();
        if (dragStart is null) return;
        var target = drop; var panel = movingPanel; var slot = movingSlot;
        var commit = !cancelled && dragMoved && Allowed && target is not null;
        if (cancelled || dragMoved) Layout.RestoreSelection(dragStart);
        dragStart = null; movingPanel = movingSlot = null; drop = null; preview.IsOpen = false; dragHint.IsOpen = false;
        if (commit)
        {
            var destination = target!.Value;
            var changed = panel is not null ? MovePanel(panel, destination.Id, destination.Zone, destination.Index, destination.Workspace)
                : MoveSlot(slot!, destination.Id, destination.Zone, destination.Workspace);
            if (!changed) Render();
        }
        else { Render(); if (!cancelled && !dragMoved) Persist(); }
    }
    private void CancelGesture()
    {
        if (dragStart is null && resizing is null) return;
        if (dragStart is not null) Layout.RestoreSelection(dragStart);
        dragStart = null; movingPanel = movingSlot = null; drop = null; resizing = null;
        preview.IsOpen = false; dragHint.IsOpen = false;
        if (Mouse.Captured is Thumb thumb) thumb.ReleaseMouseCapture();
        if (layout is not null) Render();
    }
    private void AddSplitter(string first, string second, bool horizontal)
    {
        var thumb = new Thumb { Cursor = horizontal ? Cursors.SizeWE : Cursors.SizeNS,
            ToolTip = "拖动分隔线调整大小；双击恢复比例", Background = border };
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        visual.SetValue(UIElement.OpacityProperty, .4);
        thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual };
        AutomationProperties.SetName(thumb, "调整相邻槽位大小");
        thumb.MouseEnter += (_, _) => thumb.Background = accent;
        thumb.MouseLeave += (_, _) => thumb.Background = border;
        thumb.DragStarted += (_, _) => { if (Allowed) resizing = new(Layout.State with { Width = canvas.Width, Height = canvas.Height }, views.ToDictionary(v => v.Key, v => v.Value.Minimum)); };
        thumb.DragDelta += (_, e) => {
            if (resizing is null) return;
            if (!Allowed) { CancelGesture(); return; }
            var change = horizontal ? e.HorizontalChange / canvas.Width : e.VerticalChange / canvas.Height;
            if (resizing.ResizeBoundary(first, second, change, horizontal)) RenderBounds();
        };
        thumb.DragCompleted += (_, e) => {
            var result = resizing?.State; resizing = null;
            if (!e.Canceled && result is not null && Allowed && Layout.Restore(result, true)) Persist();
            // Keep the same Thumb after a click so WPF can recognize the
            // second click of a double-click on this separator.
            RenderBounds();
        };
        thumb.PreviewMouseDoubleClick += (_, e) => {
            Change(() => Layout.AtSize(canvas.Width, canvas.Height, () => Layout.ResetBoundary(first, second, horizontal)));
            e.Handled = true;
        };
        splitters.Add((thumb, first, second, horizontal)); canvas.Children.Add(thumb); Panel.SetZIndex(thumb, 20);
    }
}
