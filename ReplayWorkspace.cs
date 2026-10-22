using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SokuReplayLab;

public sealed record ReplayPanelLayout(double X, double Y, double Width, double Height);

/// <summary>Movable and resizable panels for the replay workspace.</summary>
public sealed class ReplayWorkspace : Grid
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private sealed record PanelEntry(string Id, Border Frame, double MinWidth, double MinHeight,
        Func<double, ReplayPanelLayout> Default, bool Saved);

    private readonly Canvas canvas = new();
    private readonly List<PanelEntry> panels = [];
    private readonly Action<string, ReplayPanelLayout> changed;
    private int topLayer;
    private bool initialized;

    public ReplayWorkspace(Action<string, ReplayPanelLayout> changed)
    {
        this.changed = changed;
        Height = 1030;
        ClipToBounds = true;
        Children.Add(canvas);
        SizeChanged += (_, _) => {
            if (canvas.ActualWidth < 300) return;
            if (!initialized)
            {
                initialized = true;
                foreach (var panel in panels.Where(x => !x.Saved))
                    Apply(panel, panel.Default(canvas.ActualWidth));
            }
            foreach (var panel in panels)
                FitToViewport(panel);
            UpdateExtent();
        };
    }

    public Border AddPanel(string id, string title, UIElement body, double minWidth,
        double minHeight, Func<double, ReplayPanelLayout> defaultLayout,
        ReplayPanelLayout? saved, Brush surface, Brush border, Brush titleBrush, Brush titleText)
    {
        var frame = new Border {
            MinWidth = minWidth, MinHeight = minHeight,
            Background = surface, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), ClipToBounds = true
        };
        var frameGrid = new Grid();
        frameGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        frameGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Child = frameGrid;
        var header = new Grid { Background = titleBrush };
        header.Children.Add(new TextBlock {
            Text = $"⠿  {title}", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = titleText, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false
        });
        var drag = new Thumb { Cursor = Cursors.SizeAll,
            ToolTip = "拖动标题移动面板" };
        drag.Template = TransparentThumb();
        header.Children.Add(drag);
        frameGrid.Children.Add(header);
        var content = new ScrollViewer {
            Content = body, Padding = new Thickness(12, 10, 12, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(content, 1);
        frameGrid.Children.Add(content);
        var grip = new Border {
            Width = 22, Height = 22, Background = titleBrush,
            CornerRadius = new CornerRadius(4, 0, 7, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock {
                Text = "◢", FontSize = 16, Foreground = titleText,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 1)
            }
        };
        Grid.SetRow(grip, 1); frameGrid.Children.Add(grip);

        var entry = new PanelEntry(id, frame, minWidth, minHeight, defaultLayout,
            saved is not null);
        panels.Add(entry);
        canvas.Children.Add(frame);
        Apply(entry, saved ?? defaultLayout(1100));
        Panel.SetZIndex(frame, ++topLayer);
        frame.PreviewMouseDown += (_, _) => Panel.SetZIndex(frame, ++topLayer);
        drag.DragDelta += (_, delta) => {
            var x = Math.Clamp(Canvas.GetLeft(frame) + delta.HorizontalChange, 0,
                Math.Max(0, canvas.ActualWidth - frame.Width));
            var y = Math.Clamp(Canvas.GetTop(frame) + delta.VerticalChange, 0, 2400);
            Canvas.SetLeft(frame, x); Canvas.SetTop(frame, y);
            UpdateExtent();
        };
        drag.DragCompleted += (_, _) => Persist(entry);
        AddHandle(Edge.Left, Cursors.SizeWE, HorizontalAlignment.Left,
            VerticalAlignment.Stretch, 8, double.NaN);
        AddHandle(Edge.Right, Cursors.SizeWE, HorizontalAlignment.Right,
            VerticalAlignment.Stretch, 8, double.NaN);
        AddHandle(Edge.Top, Cursors.SizeNS, HorizontalAlignment.Stretch,
            VerticalAlignment.Top, double.NaN, 7);
        AddHandle(Edge.Bottom, Cursors.SizeNS, HorizontalAlignment.Stretch,
            VerticalAlignment.Bottom, double.NaN, 8);
        AddHandle(Edge.Left | Edge.Top, Cursors.SizeNWSE,
            HorizontalAlignment.Left, VerticalAlignment.Top, 18, 18);
        AddHandle(Edge.Right | Edge.Top, Cursors.SizeNESW,
            HorizontalAlignment.Right, VerticalAlignment.Top, 18, 18);
        AddHandle(Edge.Left | Edge.Bottom, Cursors.SizeNESW,
            HorizontalAlignment.Left, VerticalAlignment.Bottom, 18, 18);
        AddHandle(Edge.Right | Edge.Bottom, Cursors.SizeNWSE,
            HorizontalAlignment.Right, VerticalAlignment.Bottom, 22, 22);

        void AddHandle(Edge edge, Cursor cursor, HorizontalAlignment horizontal,
            VerticalAlignment vertical, double width, double height)
        {
            var handle = new Thumb {
                Cursor = cursor, ToolTip = "拖动边缘调整面板大小",
                HorizontalAlignment = horizontal, VerticalAlignment = vertical,
                Width = width, Height = height, Template = TransparentThumb()
            };
            if ((edge & Edge.Top) == 0) Grid.SetRow(handle, 1);
            else if ((edge & Edge.Bottom) != 0) Grid.SetRowSpan(handle, 2);
            frameGrid.Children.Add(handle);
            Panel.SetZIndex(handle, edge is Edge.Left or Edge.Right or Edge.Top or Edge.Bottom ? 1 : 2);
            handle.DragDelta += (_, delta) => Resize(entry, edge,
                delta.HorizontalChange, delta.VerticalChange);
            handle.DragCompleted += (_, _) => Persist(entry);
        }
        return frame;
    }

    private void Resize(PanelEntry panel, Edge edge, double dx, double dy)
    {
        var frame = panel.Frame;
        var x = Canvas.GetLeft(frame);
        var y = Canvas.GetTop(frame);
        var width = frame.Width;
        var height = frame.Height;
        if ((edge & Edge.Left) != 0)
        {
            var change = Math.Clamp(dx, -x, width - panel.MinWidth);
            x += change;
            width -= change;
        }
        if ((edge & Edge.Right) != 0)
            width = Math.Clamp(width + dx, panel.MinWidth,
                Math.Max(panel.MinWidth, canvas.ActualWidth - x));
        if ((edge & Edge.Top) != 0)
        {
            var change = Math.Clamp(dy, -y, height - panel.MinHeight);
            y += change;
            height -= change;
        }
        if ((edge & Edge.Bottom) != 0)
            height = Math.Clamp(height + dy, panel.MinHeight, 1600);
        Canvas.SetLeft(frame, x);
        Canvas.SetTop(frame, y);
        frame.Width = width;
        frame.Height = height;
        UpdateExtent();
    }

    public void ResetLayout()
    {
        var width = Math.Max(300, canvas.ActualWidth);
        foreach (var panel in panels)
        {
            Apply(panel, panel.Default(width));
            Persist(panel);
        }
        UpdateExtent();
    }

    public ReplayPanelLayout LayoutOf(string id)
    {
        var panel = panels.First(x => x.Id == id);
        return Snapshot(panel);
    }

    private void FitToViewport(PanelEntry panel)
    {
        var frame = panel.Frame;
        frame.Width = Math.Max(panel.MinWidth,
            Math.Min(frame.Width, Math.Max(panel.MinWidth, canvas.ActualWidth)));
        Canvas.SetLeft(frame, Math.Clamp(Canvas.GetLeft(frame), 0,
            Math.Max(0, canvas.ActualWidth - frame.Width)));
    }

    private void Apply(PanelEntry panel, ReplayPanelLayout layout)
    {
        var frame = panel.Frame;
        frame.Width = Math.Max(panel.MinWidth, layout.Width);
        frame.Height = Math.Max(panel.MinHeight, layout.Height);
        Canvas.SetLeft(frame, Math.Max(0, layout.X));
        Canvas.SetTop(frame, Math.Max(0, layout.Y));
        if (canvas.ActualWidth > 0) FitToViewport(panel);
    }

    private void Persist(PanelEntry panel) => changed(panel.Id, Snapshot(panel));

    private static ReplayPanelLayout Snapshot(PanelEntry panel) => new(
        Canvas.GetLeft(panel.Frame), Canvas.GetTop(panel.Frame),
        panel.Frame.Width, panel.Frame.Height);

    private void UpdateExtent()
    {
        if (panels.Count == 0) return;
        Height = Math.Max(760, panels.Max(panel =>
            Canvas.GetTop(panel.Frame) + panel.Frame.Height) + 26);
    }

    private static ControlTemplate TransparentThumb()
    {
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = visual };
    }
}
