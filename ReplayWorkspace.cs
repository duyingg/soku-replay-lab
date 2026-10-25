using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SokuReplayLab;

public sealed record ReplayPanelLayout(double X, double Y, double Width, double Height);

/// <summary>Persistent docking regions; business views retain a stable visual parent.</summary>
public sealed partial class ReplayWorkspace : Grid
{
    private sealed record View(string Id, string Title, Border Frame, Size Minimum,
        Func<double, ReplayPanelLayout> Default, ReplayPanelLayout? Saved, Func<bool>? BeforeClose);
    private readonly Canvas canvas = new();
    private readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
    private readonly Dictionary<string, View> views = [];
    private readonly Dictionary<string, Border> headers = [];
    private readonly Dictionary<string, Thumb> tabHandles = [];
    private readonly List<(Thumb Thumb, string First, string Second, bool Horizontal)> splitters = [];
    private readonly Dictionary<string, ReplayDockState> workspaces;
    private readonly Action<string, ReplayPanelLayout> legacyChanged;
    private readonly Action<ReplayDockState, Dictionary<string, ReplayDockState>>? saved;
    private readonly Func<bool>? canEdit;
    private readonly ReplayDockState? initial;
    private readonly ReplayDockState? initialDefault;
    private ReplayDockLayout? layout, resizing;
    private ReplayDockState? defaults;
    private Brush surface = Brushes.White, border = Brushes.Gray, titleBrush = Brushes.DarkSlateBlue,
        titleText = Brushes.White, accent = Brushes.DodgerBlue;
    private readonly Brush contentText;
    private readonly Brush onAccent;
    private bool updating;
    private Window? owner;
    public ReplayDockLayout Layout => layout ?? throw new InvalidOperationException("面板尚未注册完成。");
    public ReplayDockState DefaultLayout => ReplayDockLayout.Copy(defaults!);
    private ReplayDockLayout Current => resizing ?? Layout;
    private bool Allowed => canEdit?.Invoke() != false;

    public ReplayWorkspace(Action<string, ReplayPanelLayout> changed,
        Action<ReplayDockState, Dictionary<string, ReplayDockState>>? saved = null,
        ReplayDockState? initial = null, IReadOnlyDictionary<string, ReplayDockState>? workspaces = null,
        Func<bool>? canEdit = null, Brush? accent = null, ReplayDockState? initialDefault = null, Brush? contentText = null, Brush? onAccent = null)
    {
        legacyChanged = changed; this.saved = saved; this.initial = initial; this.canEdit = canEdit; this.initialDefault = initialDefault;
        this.contentText = contentText ?? Brushes.Black;
        this.onAccent = onAccent ?? Brushes.White;
        this.workspaces = workspaces?.ToDictionary(x => x.Key, x => ReplayDockLayout.Copy(x.Value)) ?? [];
        if (accent is not null) this.accent = accent;
        Height = 760;
        viewport.Content = canvas; Children.Add(viewport);
        preview.PlacementTarget = viewport;
        viewport.SizeChanged += (_, _) => { if (layout is not null) { CancelGesture(); RenderBounds(); } };
        viewport.ScrollChanged += (_, e) => {
            if (ReferenceEquals(e.OriginalSource, viewport) && layout is not null &&
                (Math.Abs(e.ViewportWidthChange) > .1 || Math.Abs(e.ViewportHeightChange) > .1)) RenderBounds();
        };
        Loaded += (_, _) => {
            owner = Window.GetWindow(this);
            if (owner is not null) owner.Deactivated += OnDeactivate;
            CompleteRegistration(); UpdateViewportHeight();
        };
        Unloaded += (_, _) => { CancelGesture(); if (owner is not null) owner.Deactivated -= OnDeactivate; owner = null; };
        LayoutUpdated += (_, _) => UpdateViewportHeight();
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape && (IsDragging || resizing is not null)) { CancelGesture(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key is Key.Z or Key.Y) &&
                Keyboard.FocusedElement is not TextBoxBase && Allowed)
            { if (e.Key == Key.Z) Undo(); else Redo(); e.Handled = true; }
        };
    }
    private void OnDeactivate(object? sender, EventArgs e) => CancelGesture();
    private void UpdateViewportHeight()
    {
        var ancestor = VisualTreeHelper.GetParent(this);
        while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is ScrollViewer scroll && scroll.ViewportHeight > 200 && double.IsFinite(scroll.ViewportHeight))
        { var height = Math.Max(320, scroll.ViewportHeight - 34); if (Math.Abs(Height - height) > 1) Height = height; }
    }
    public Border AddPanel(string id, string title, UIElement body, double minWidth,
        double minHeight, Func<double, ReplayPanelLayout> defaultLayout,
        ReplayPanelLayout? previous, Brush surface, Brush border, Brush titleBrush, Brush titleText,
        Func<bool>? beforeClose = null)
    {
        if (layout is not null || views.ContainsKey(id)) throw new InvalidOperationException("面板重复注册或注册已结束。");
        this.surface = surface; this.border = border; this.titleBrush = titleBrush; this.titleText = titleText;
        // Never remove/reparent this border or its contents during docking.
        var frame = new Border { Background = surface, BorderBrush = border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true };
        UIElement content = id is "playback" or "bookmarks" ? new ScrollViewer { Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : body;
        frame.Child = new Border { Margin = new Thickness(0, 38, 0, 0), Padding = new Thickness(12, 10, 12, 12), Child = content };
        views.Add(id, new(id, title, frame, new(minWidth, minHeight), defaultLayout, previous, beforeClose));
        canvas.Children.Add(frame); return frame;
    }
    public void CompleteRegistration()
    {
        if (layout is not null || views.Count == 0) return;
        var width = Math.Max(900, viewport.ActualWidth > 300 ? viewport.ActualWidth : ActualWidth > 300 ? ActualWidth : 1100);
        ReplayDockState BuildState(bool useSaved)
        {
        var stateWidth = useSaved ? Math.Max(width, views.Values.Where(v => v.Saved is not null)
            .Select(v => v.Saved!.X + v.Saved.Width).DefaultIfEmpty(width).Max()) : width;
        var rectangles = views.Values.ToDictionary(v => v.Id, v => useSaved ? v.Saved ?? v.Default(stateWidth) : v.Default(stateWidth));
        // Preserve columns/order, aligning the old overlapping boundaries.
        foreach (var column in rectangles.Keys.GroupBy(id => Math.Round(rectangles[id].X / 24)).ToArray())
        {
            var ordered = column.OrderBy(id => rectangles[id].Y).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                var a = rectangles[ordered[i - 1]]; var b = rectangles[ordered[i]];
                if (a.Y + a.Height + 12 <= b.Y) continue;
                var edge = Math.Max(a.Y + views[ordered[i - 1]].Minimum.Height, (a.Y + a.Height + b.Y - 12) / 2);
                rectangles[ordered[i - 1]] = a with { Height = edge - a.Y };
                rectangles[ordered[i]] = b with { Y = edge + 12,
                    Height = Math.Max(views[ordered[i]].Minimum.Height, b.Y + b.Height - edge - 12) };
            }
        }
        stateWidth = Math.Max(stateWidth, rectangles.Values.Max(r => r.X + r.Width));
        var height = Math.Max(760, rectangles.Values.Max(r => r.Y + r.Height) + 26);
        var slots = rectangles.Select(x => new ReplayDockSlot("home-" + x.Key,
            new(x.Value.X / stateWidth, x.Value.Y / height, x.Value.Width / stateWidth, x.Value.Height / height), [x.Key], x.Key)).ToArray();
        return new(1, stateWidth, height, slots, slots.ToDictionary(s => s.Active!, s => s.Id), "默认布局");
        }
        defaults = BuildState(false);
        layout = new(defaults, views.ToDictionary(x => x.Key, x => x.Value.Minimum));
        defaults = ReplayDockLayout.Copy(layout.State);
        if (initialDefault is not null && layout.Restore(initialDefault)) defaults = ReplayDockLayout.Copy(layout.State);
        if (initial is not null) layout.Restore(initial);
        else if (views.Values.Any(v => v.Saved is not null)) layout.Restore(BuildState(true));
        Render();
        Persist();
    }
    public ReplayPanelLayout LayoutOf(string id)
    {
        var slot = Layout.State.Slots.FirstOrDefault(s => s.Id == id || s.Panels.Contains(id));
        return slot is null ? views[id].Saved ?? views[id].Default(Math.Max(900, canvas.Width)) : PixelBounds(slot.Bounds);
    }
    public bool MovePanel(string panel, string target, ReplayDockZone zone, int? index = null, bool workspace = false) => Change(() => Layout.AtSize(canvas.Width, canvas.Height, () => Layout.MovePanel(panel, target, zone, index, workspace)));
    public bool MoveSlot(string source, string target, ReplayDockZone zone, bool workspace = false) => Change(() => Layout.AtSize(canvas.Width, canvas.Height, () => Layout.MoveSlot(source, target, zone, workspace)));
    public bool OpenPanel(string panel, string? slot = null) => Change(() => Layout.AtSize(canvas.Width, canvas.Height, () => Layout.Open(panel, slot)));
    public bool ClosePanel(string panel) => Allowed && views.TryGetValue(panel, out var v) && v.BeforeClose?.Invoke() != false && Change(() => Layout.Close(panel));
    public bool Undo() => Change(() => Layout.Undo());
    public bool Redo() => Change(() => Layout.Redo());
    public bool DeleteSlot(string id, string? moveTo = null)
    {
        if (!Allowed) return false;
        var slot = Layout.State.Slots.FirstOrDefault(s => s.Id == id);
        if (slot is null || moveTo is null && slot.Panels.Any(p => views[p].BeforeClose?.Invoke() == false)) return false;
        return Change(() => Layout.Delete(id, moveTo));
    }
    public void ResetLayout()
    {
        if (!Allowed || defaults is null) return;
        if (MessageBox.Show(Window.GetWindow(this), "恢复默认布局会替换当前自定义布局，是否继续？", "恢复默认布局",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Change(() => Layout.Restore(defaults, true));
    }
    private bool Change(Func<bool> action)
    {
        CompleteRegistration();
        if (!Allowed || IsDragging || resizing is not null || !action()) return false;
        Render(); Persist(); return true;
    }
    private void Persist()
    {
        foreach (var slot in Layout.State.Slots) foreach (var panel in slot.Panels) legacyChanged(panel, PixelBounds(slot.Bounds));
        saved?.Invoke(ReplayDockLayout.Copy(Layout.State), workspaces.ToDictionary(x => x.Key, x => ReplayDockLayout.Copy(x.Value)));
    }
    public void UpdateEditAvailability()
    {
        var enabled = Allowed;
        if (!enabled && (IsDragging || resizing is not null)) CancelGesture();
        foreach (var header in headers.Values)
        {
            header.IsEnabled = enabled;
            header.ToolTip = enabled ? null : "当前回放任务执行中，完成后可调整面板布局。";
            ToolTipService.SetShowOnDisabled(header, true);
        }
        foreach (var splitter in splitters) splitter.Thumb.IsEnabled = enabled;
    }
    private ReplayPanelLayout PixelBounds(ReplayPanelLayout r) => new(r.X * canvas.Width, r.Y * canvas.Height, r.Width * canvas.Width, r.Height * canvas.Height);
    private void Render()
    {
        if (layout is null) return;
        var focusedPanel = tabHandles.FirstOrDefault(x => ReferenceEquals(x.Value, Keyboard.FocusedElement)).Key;
        var focusedSlot = focusedPanel is null ? null : Layout.State.Slots.FirstOrDefault(s => s.Panels.Contains(focusedPanel));
        foreach (var h in headers.Values) canvas.Children.Remove(h);
        foreach (var s in splitters) canvas.Children.Remove(s.Thumb);
        headers.Clear(); tabHandles.Clear(); splitters.Clear();
        foreach (var slot in Layout.State.Slots)
        { var h = BuildHeader(slot); headers[slot.Id] = h; canvas.Children.Add(h); Panel.SetZIndex(h, 10); }
        foreach (var boundary in Layout.Boundaries()) AddSplitter(boundary.First, boundary.Second, boundary.Across);
        RenderBounds();
        UpdateEditAvailability();
        if (focusedSlot?.Active is string active && tabHandles.TryGetValue(active, out var nextFocus))
        { UpdateLayout(); nextFocus.Focus(); }
    }
    private void RenderBounds()
    {
        if (layout is null || updating) return;
        updating = true;
        try
        {
            var required = Current.RequiredSize();
            var width = Math.Max(required.Width, viewport.ViewportWidth > 100 ? viewport.ViewportWidth : Current.State.Width);
            var height = Math.Max(required.Height, viewport.ViewportHeight > 100 ? viewport.ViewportHeight : Current.State.Height);
            Current.Arrange(width, height); canvas.Width = Current.State.Width; canvas.Height = Current.State.Height;
            var activePanels = Current.State.Slots.Select(s => s.Active).ToHashSet();
            foreach (var view in views.Values)
                view.Frame.Visibility = activePanels.Contains(view.Id) ? Visibility.Visible : Visibility.Hidden;
            foreach (var slot in Current.State.Slots)
            {
                var b = PixelBounds(slot.Bounds);
                if (headers.TryGetValue(slot.Id, out var h)) Place(h, b.X, b.Y, b.Width, slot.Panels.Length == 0 ? b.Height : 38);
                foreach (var panel in slot.Panels)
                { var f = views[panel].Frame; Place(f, b.X, b.Y, b.Width, b.Height); }
            }
            foreach (var (thumb, first, second, horizontal) in splitters)
            {
                var b = PixelBounds(Current.BoundaryBounds(first, second, horizontal));
                if (horizontal) Place(thumb, b.X + b.Width / 2 - 5, b.Y, 10, b.Height);
                else Place(thumb, b.X, b.Y + b.Height / 2 - 5, b.Width, 10);
            }
        }
        finally { updating = false; }
    }
    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); element.Width = Math.Max(1, width); element.Height = Math.Max(1, height); }
    private Border BuildHeader(ReplayDockSlot slot)
    {
        var grid = new Grid { Background = titleBrush };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(25) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(DragThumb("⠿", null, slot.Id));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var panel in slot.Panels)
        {
            var tab = new DockPanel { Background = slot.Active == panel ? accent : Brushes.Transparent, MaxWidth = 230 };
            var close = SmallButton("×", "关闭“" + views[panel].Title + "”", () => ClosePanel(panel));
            close.Foreground = slot.Active == panel ? onAccent : titleText;
            DockPanel.SetDock(close, Dock.Right); tab.Children.Add(close);
            var label = DragThumb(views[panel].Title, panel, slot.Id, slot.Active == panel ? onAccent : titleText);
            tabHandles[panel] = label;
            label.PreviewKeyDown += (_, e) => {
                if (e.Key is Key.Enter or Key.Space) { Change(() => Layout.Activate(slot.Id, panel)); e.Handled = true; }
                else if (e.Key is Key.Left or Key.Right)
                { var i = Array.IndexOf(slot.Panels, panel); Change(() => Layout.Activate(slot.Id, slot.Panels[(i + slot.Panels.Length + (e.Key == Key.Left ? -1 : 1)) % slot.Panels.Length])); e.Handled = true; }
            };
            tab.Children.Add(label); tabs.Children.Add(tab);
        }
        var tabsScroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(tabsScroll, 1); grid.Children.Add(tabsScroll);
        if (slot.Panels.Length == 0)
        {
            grid.RowDefinitions.Add(new() { Height = new GridLength(38) }); grid.RowDefinitions.Add(new());
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock { Text = "在设置 → 回放面板中开启面板", Foreground = contentText,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
            var emptyBody = new Border { Background = surface, Child = empty };
            Grid.SetRow(emptyBody, 1); Grid.SetColumnSpan(emptyBody, 2); grid.Children.Add(emptyBody);
        }
        return new Border { Background = titleBrush, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7, 7, 0, 0), ClipToBounds = true, Child = grid };
    }
    private Button SmallButton(string text, string name, Action action)
    {
        var button = new Button { Content = text, ToolTip = name, Foreground = titleText, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(5, 3, 5, 3), MinWidth = 24, VerticalAlignment = VerticalAlignment.Center };
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Control.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(ContentControl.Content)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(label);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button;
    }
}
