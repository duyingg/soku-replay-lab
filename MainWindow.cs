using Microsoft.Win32;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SokuReplayLab;

public sealed partial class MainWindow : Window
{
    private static readonly AppTheme palette = new();
    private static Brush Navy => palette.Navy;
    private static Brush Blue => palette.Accent;
    private static Brush Light => palette.Canvas;
    private static Brush Border => palette.Border;
    private static Brush Muted => palette.Muted;
    private static Brush Red => palette.Danger;
    private static Brush Surface => palette.Surface;
    private static Brush TextColor => palette.Text;
    private static Brush Strong => palette.Strong;
    private readonly string root = FindRoot();
    private readonly StackPanel navigation = new();
    private readonly StackPanel content = new();
    private sealed record CachedPage(UIElement[] Children, object? Toolbar,
        ReplaySession? Session, ReplaySession[] Samples, int PressureMoves);
    private readonly Dictionary<int, CachedPage> pageCache = [];
    private bool navigationRender;
    private string? navigationShape;
    private readonly List<(int Page, Button Button, TextBlock Icon, TextBlock Name)> navigationButtons = [];
    private readonly TextBlock connection = new();
    private Grid? shellGrid;
    private DockPanel? mainDock;
    private DockPanel? sidebarDock;
    private ScrollViewer? pageScroller;
    private ContentControl? curveToolbarHost;
    private Grid? topbarGrid;
    private StackPanel? topbarActions;
    private ReplaySession? session;
    private GameInstance? detectedGame;
    private readonly List<ReplaySession> samples = [];
    private int page;
    private int cursor;
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer probeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Slider? replaySlider;
    private Button? transportPlayButton;
    private TextBlock? transportTimeLabel;
    private readonly List<(double Rate, Button Control)> transportSpeeds = [];
    private Action? refreshReplayFrame;
    private bool updatingTransport;
    private TextBlock? liveStatusLabel;
    private TextBlock? probeStatusLabel;
    private TextBlock? escapeProgressLabel;
    private LiveGameReader? liveReader;
    private LiveGameStatus? liveStatus;
    private ProbeClient? probe;
    private GameSurfaceHost? gameSurface;
    private readonly List<UIElement> parkedReplayElements = [];
    private int renderedPage = -1;
    private StackPanel? replayFramePanel;
    private StackPanel? replayFrameControls;
    private ReplayWorkspace? replayWorkspace;
    private ContentControl? replayPlaybackContent;
    private ContentControl? replaySnapshotContent;
    private ContentControl? replayQuickContent;
    private ContentControl? replayBookmarksContent;
    private WrapPanel? replayP1Hand;
    private WrapPanel? replayP2Hand;
    private TextBlock? replayPictureTitle;
    private TextBlock? replayFrameCountText;
    private TextBlock? pageTitleText;
    private TextBlock? topNoticeText;
    private Border? topNotice;
    private string? importProgressText;
    private string? hiddenNoticeMessage;
    private string? dismissedNoticeMessage;
    private bool dismissedImportProgress;
    private bool embedFailed;
    private ProbeSnapshot? probeStatus;
    private readonly HashSet<int> nativeSnapshotSlots = [];
    private int nativeSnapshotPid = -1;
    private string? nativeSnapshotSource;
    private bool probeConnecting;
    private bool gameStarting;
    private bool shutdownInProgress, shutdownReady;
    private int launchedGamePid = -1;
    private string? launchedGameExe;
    private int managedGamePid = -1;
    private string? managedGameExe;
    internal bool SmokeDisableEscapeWorker { get; set; }
    private bool branchBusy, escapeBusy,
        fullCaptureBusy, fullCaptureCancelRequested, sessionLoading;
    private bool escapeOnlySuccess;
    private int escapeBatchDone, escapeBatchTotal;
    private int branchAnchor;
    private ReplaySession? branchOriginal;
    private readonly List<EscapeTrialResult> escapeResults = [];
    private EscapeTrialResult? selectedEscapeResult;
    private int escapeDefender = -1;
    private string escapeMove = "4A";
    private readonly HashSet<string> addedEscapeMoves = [];
    private readonly HashSet<string> enabledEscapeMoves = ["4A"];
    private bool playing;
    private double playbackSpeed = 1;
    private double playbackRemainder;
    private readonly Stopwatch playbackClock = new();
    private readonly Dictionary<string, ReplayPanelLayout> replayPanelLayouts = [];
    private ReplayDockState? replayDockState;
    private ReplayDockState? replayDockDefaultState;
    private readonly Dictionary<string, ReplayDockState> replayDockWorkspaces = [];
    private string? gameDirectory;
    private string? replayDirectory;
    private int gapThreshold = -1;
    private int minPressureMoves = 1;
    private bool comboShowDashes = true;
    private int statsSide = -1;
    private string settingsSection = "基础修改";
    private int statsRound = -1;
    private string statsCategory = "全表";
    private string multiCategory = "全表";
    private int auditSide = -1;
    private string auditKind = "全部";
    private int interpretSide = -1;
    private ReplaySession? interpretedSession;
    private int interpretedPressureMoves = -1;
    private IReadOnlyList<InterpretationTip>? interpretedTips;
    private bool expert;
    private string skin = "经典";
    private readonly bool[] enabled = Enumerable.Repeat(true, 10).ToArray();
    private readonly bool[] chartEnabled = Enumerable.Repeat(true, 5).ToArray();
    private int curveCardWidth = 36;
    private bool showReplayTools = true;
    private bool autoConnectOnLaunch = true;
    private readonly Dictionary<string, SortedSet<int>> bookmarks = new(StringComparer.OrdinalIgnoreCase);
    private string SettingsPath => System.IO.Path.Combine(root, "review_settings_new.json");
    private string? message;
    private static readonly string[] Pages =
        ["回放", "对局统计", "多局统计", "曲线", "连段", "压制总表",
         "逃压推演", "REP 解读", "判定明细", "设置"];
    private static readonly string[] Groups =
        ["播放与操作", "数据分析", "数据分析", "数据分析", "战术分析",
         "战术分析", "战术分析", "复盘证据", "复盘证据", "配置"];
    private static readonly string[] PageIcons =
        ["▣", "◫", "▦", "⌁", "⌘", "▤", "◇", "✦", "≡", "⚙"];

    private string PlayerIdentity(int side)
    {
        var header = session?.Header;
        var character = side == 0 ? header?.Player1 : header?.Player2;
        var profileName = side == 0 ? header?.ProfileName1 : header?.ProfileName2;
        return $"{side + 1}P · {character ?? "角色未知"} · {profileName ?? "机签未记录"}";
    }

    public MainWindow()
    {
        playbackTimer.Tick += (_, _) => AdvancePlayback();
        liveTimer.Tick += (_, _) => RefreshLiveStatus();
        probeTimer.Tick += (_, _) => RefreshProbeStatus();
        Closing += OnClosing;
        Closed += (_, _) => {
            gameFocusTimer.Stop(); gameKeyboardFocus?.Dispose(); gameKeyboardFocus = null;
            playbackTimer.Stop(); liveTimer.Stop(); probeTimer.Stop();
            liveReader?.Dispose(); probe?.Dispose();
        };
        PreviewMouseDown += (_, e) => { if (gameKeyboardFocus is not null) e.Handled = true; };
        gameFocusTimer.Tick += (_, _) => {
            if (gameKeyboardFocus is { IsGameWindowAlive: false }) ReturnGameFocus();
        };
        LoadSettings();
        ConfigureThemeResources();
        Title = "非想天则 · 复盘工作台";
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1440, Math.Max(1, workArea.Width * .96));
        Height = Math.Min(900, Math.Max(1, workArea.Height * .96));
        MinWidth = Math.Min(1080, Width);
        MinHeight = Math.Min(680, Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        Background = Light;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var shell = new Grid { Background = Light };
        shellGrid = shell;
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = shell;
        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        shell.Children.Add(sidebar);
        var main = new DockPanel { Background = Light };
        mainDock = main;
        Grid.SetColumn(main, 1);
        shell.Children.Add(main);
        var topbar = BuildTopbar();
        DockPanel.SetDock(topbar, Dock.Top);
        main.Children.Add(topbar);
        curveToolbarHost = new ContentControl {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(29, 10, 29, 0)
        };
        DockPanel.SetDock(curveToolbarHost, Dock.Top);
        main.Children.Add(curveToolbarHost);
        var transport = BuildTransportBar();
        DockPanel.SetDock(transport, Dock.Bottom);
        main.Children.Add(transport);
        var scroller = new ScrollViewer {
            Background = Light,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
        pageScroller = scroller;
        content.Margin = new Thickness(29, 24, 29, 22);
        main.Children.Add(scroller);
        BuildTopNotice(shell);
        Render();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "SokuReplayLab.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }
    private static TextBlock Text(string value, double size = 13, Brush? color = null, bool bold = false)
        => new() { Text = value, FontSize = size, Foreground = color ?? TextColor,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Border Box(UIElement child, Brush? background = null, Thickness? padding = null)
        => new() { Child = child, Background = background ?? Surface,
            CornerRadius = new CornerRadius(11), BorderBrush = Border, BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(18), Margin = new Thickness(0, 0, 0, 15) };
    private void BuildTopNotice(Grid shell)
    {
        var row = new DockPanel();
        topNoticeText = Text("", 12, palette.NoticeText);
        var close = Button("×", () => {
            if (importProgressText is not null) dismissedImportProgress = true;
            else dismissedNoticeMessage = message;
            UpdateTopNotice();
        });
        close.Margin = new Thickness(14, 0, 0, 0);
        close.MinWidth = 30;
        close.Padding = new Thickness(4, 2, 4, 2);
        close.ToolTip = "关闭提示";
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(topNoticeText);
        topNotice = new Border {
            Child = row, Background = palette.Notice,
            BorderBrush = Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(13, 8, 9, 8),
            Margin = new Thickness(24, 75, 24, 0), MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect {
                BlurRadius = 12, ShadowDepth = 3, Opacity = .2
            }
        };
        Grid.SetColumn(topNotice, 1);
        Panel.SetZIndex(topNotice, 100);
        shell.Children.Add(topNotice);
    }
    private static StackPanel Vertical(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private static ControlTemplate RoundedButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.Name = "chrome";
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(System.Windows.Controls.Border.BackgroundProperty,
            new Binding(nameof(System.Windows.Controls.Button.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(System.Windows.Controls.Border.BorderBrushProperty,
            new Binding(nameof(System.Windows.Controls.Button.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(System.Windows.Controls.Border.BorderThicknessProperty,
            new Binding(nameof(System.Windows.Controls.Button.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(System.Windows.Controls.Border.PaddingProperty,
            new Binding(nameof(System.Windows.Controls.Button.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.HorizontalAlignmentProperty,
            new Binding(nameof(System.Windows.Controls.Button.HorizontalContentAlignment)) {
                RelativeSource = RelativeSource.TemplatedParent });
        content.SetBinding(ContentPresenter.VerticalAlignmentProperty,
            new Binding(nameof(System.Windows.Controls.Button.VerticalContentAlignment)) {
                RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
        var hover = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .82, "chrome"));
        template.Triggers.Add(hover);
        return template;
    }
    private static Button Button(string caption, Action click, bool primary = false)
    {
        var button = new Button {
            Content = caption, Background = primary ? Blue : Surface,
            Foreground = primary ? palette["OnAccent"] : TextColor,
            BorderBrush = primary ? Blue : Border, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12, FontWeight = FontWeights.SemiBold,
            MinHeight = 32, Template = RoundedButtonTemplate()
        };
        button.Click += (_, _) => click();
        return button;
    }
    private static FrameworkElement Heading(string title)
        => Text(title, 25, Strong, true);
    private static Border Card(string title, UIElement body)
    {
        var panel = Vertical(Text(title, 14, TextColor, true), body);
        ((FrameworkElement)panel.Children[0]).Margin = new Thickness(0, 0, 0, 13);
        return Box(panel);
    }
    private static UIElement Metric(string label, string value, string note, Brush? color = null)
    {
        var panel = Vertical(Text(label, 11, Muted),
            Text(value, 25, color ?? Strong, true), Text(note, 10, Muted));
        return Box(panel, Surface, new Thickness(16, 12, 16, 12));
    }
    private UIElement SideOverview(int side,
        params (string Label, string Value, string Note)[] metrics)
    {
        var title = Text(PlayerIdentity(side), 14, side == 0 ? Blue : Red, true);
        title.Margin = new Thickness(0, 0, 0, 8);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < Math.Min(4, metrics.Length); i++)
        {
            var (label, value, note) = metrics[i];
            var metric = (FrameworkElement)Metric(label, value, note,
                side == 0 ? Blue : Red);
            metric.Margin = new Thickness(i % 2 == 0 ? 0 : 6, 0,
                i % 2 == 0 ? 6 : 0, 10);
            Grid.SetColumn(metric, i % 2);
            Grid.SetRow(metric, i / 2);
            grid.Children.Add(metric);
        }
        return Vertical(title, grid);
    }
    private static Grid Columns(params (UIElement element, double width)[] items)
    {
        var grid = new Grid();
        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(items[i].width, GridUnitType.Star) });
            if (items[i].element is FrameworkElement fe)
                fe.Margin = new Thickness(i == 0 ? 0 : 8, 0, i == items.Length - 1 ? 0 : 8, 0);
            Grid.SetColumn(items[i].element, i);
            grid.Children.Add(items[i].element);
        }
        return grid;
    }
    private UIElement OverviewColumns(UIElement left, UIElement right)
    {
        var overview = Columns((left, 1), (right, 1));
        overview.HorizontalAlignment = HorizontalAlignment.Left;
        overview.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ActualWidth)) {
            Source = pageScroller,
            Converter = new OverviewWidthConverter()
        });
        return overview;
    }
    private sealed class OverviewWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double width && width > 0 ? Math.Max(720, width - 58) :
                DependencyProperty.UnsetValue;
        public object ConvertBack(object value, Type targetType, object parameter,
            CultureInfo culture) => Binding.DoNothing;
    }
    private DataGrid Table(string[] columns, IEnumerable<string[]> rows)
    {
        var allRows = rows.ToArray();
        var data = new DataTable();
        foreach (var column in columns) data.Columns.Add(column);
        // The outer page owns scrolling. Materialize only the first viewport of
        // rows, then extend the table when its bottom approaches the viewport.
        const int rowBatch = 20;
        var loadedCount = Math.Min(allRows.Length, rowBatch);
        for (var i = 0; i < loadedCount; i++)
            data.Rows.Add(allRows[i]);
        var grid = new DataGrid {
            Tag = allRows.Length,
            ItemsSource = data.DefaultView, IsReadOnly = true, AutoGenerateColumns = true,
            CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserSortColumns = false, CanUserResizeColumns = false,
            CanUserReorderColumns = false, CanUserResizeRows = false,
            Focusable = false, IsTabStop = false,
            EnableRowVirtualization = false, EnableColumnVirtualization = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Border,
            RowBackground = Surface, AlternatingRowBackground = palette.Alternating,
            Foreground = TextColor, Background = Surface,
            BorderThickness = new Thickness(0), MinHeight = 90,
            MinWidth = Math.Max(480, columns.Sum(column =>
                column is "动作路线" or "细节" or "依据" or "招式" or "招式清单" or "路线"
                    ? 180 : Math.Clamp(column.Length * 13 + 18, 72, 180)) + 20),
            RowHeight = 28, ColumnHeaderHeight = 32,
            SelectionMode = DataGridSelectionMode.Single, FontSize = 13
        };
        grid.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        grid.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        grid.PreviewMouseLeftButtonDown += (_, args) => args.Handled = true;
        grid.PreviewMouseRightButtonDown += (_, args) => args.Handled = true;
        grid.PreviewKeyDown += (_, args) => args.Handled = true;
        grid.AutoGeneratingColumn += (_, args) =>
        {
            args.Column.MaxWidth = 650;
            args.Column.MinWidth = Math.Clamp(args.PropertyName.Length * 13 + 18, 72, 180);
            if (args.PropertyName is "动作路线" or "细节" or "依据" or "招式")
                args.Column.MinWidth = 150;
            if (args.Column is DataGridTextColumn column)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(7, 0, 7, 0)));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                column.ElementStyle = style;
            }
        };
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, palette.Alternating));
        header.Setters.Add(new Setter(Control.ForegroundProperty, TextColor));
        header.Setters.Add(new Setter(Control.BorderBrushProperty, Border));
        grid.ColumnHeaderStyle = header;
        grid.PreviewMouseWheel += (_, args) =>
        {
            if (pageScroller is null) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                pageScroller.ScrollToHorizontalOffset(pageScroller.HorizontalOffset - args.Delta);
            else
                pageScroller.ScrollToVerticalOffset(pageScroller.VerticalOffset - args.Delta);
            args.Handled = true;
        };
        if (loadedCount < allRows.Length && pageScroller is not null)
        {
            var attached = false;
            var appending = false;
            void AppendVisibleRows()
            {
                if (appending || !grid.IsLoaded || !grid.IsVisible || grid.ActualHeight < 1 ||
                    loadedCount >= allRows.Length ||
                    pageScroller is null || pageScroller.ViewportHeight <= 0) return;
                var bottom = grid.TranslatePoint(new Point(0, grid.ActualHeight), pageScroller).Y;
                if (bottom < -96 || bottom > pageScroller.ViewportHeight + 96) return;
                var end = Math.Min(allRows.Length, loadedCount + rowBatch);
                appending = true;
                try
                {
                    for (; loadedCount < end; loadedCount++)
                        data.Rows.Add(allRows[loadedCount]);
                }
                finally { appending = false; }
                if (loadedCount == allRows.Length && attached)
                {
                    pageScroller.ScrollChanged -= OnPageScroll;
                    attached = false;
                }
            }
            void OnPageScroll(object sender, ScrollChangedEventArgs args)
            {
                AppendVisibleRows();
            }
            grid.Loaded += (_, _) =>
            {
                if (attached || loadedCount == allRows.Length || pageScroller is null) return;
                pageScroller.ScrollChanged += OnPageScroll;
                attached = true;
                Dispatcher.BeginInvoke(new Action(AppendVisibleRows), DispatcherPriority.Loaded);
            };
            grid.Unloaded += (_, _) =>
            {
                if (!attached || pageScroller is null) return;
                pageScroller.ScrollChanged -= OnPageScroll;
                attached = false;
            };
        }
        return grid;
    }
    private void BindTimeline(DataGrid grid,
        IReadOnlyDictionary<string, IReadOnlyList<int>> timelineColumns, Action<int> jump,
        Action<int, int>? jumpRow = null)
    {
        grid.AutoGeneratedColumns += (_, _) =>
        {
            foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
            {
                if (column.Header is not string name || !timelineColumns.ContainsKey(name)) continue;
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(7, 0, 7, 0)));
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Blue));
                style.Setters.Add(new Setter(TextBlock.CursorProperty, Cursors.Hand));
                style.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline));
                column.ElementStyle = style;
            }
        };
        grid.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, args) => {
                var source = args.OriginalSource as DependencyObject;
                DataGridCell? cell = null;
                DataGridRow? row = null;
                for (var current = source; current is not null && current != grid;
                     current = VisualTreeHelper.GetParent(current))
                {
                    if (current is DataGridCell foundCell) cell = foundCell;
                    if (current is DataGridRow foundRow) row = foundRow;
                }
                if (cell?.Column.Header is not string name || row is null ||
                    !timelineColumns.TryGetValue(name, out var ordinals)) return;
                var index = row.GetIndex();
                if (index < 0 || index >= ordinals.Count || ordinals[index] < 0 ||
                    row.Item is not DataRowView view ||
                    string.IsNullOrWhiteSpace(view[name]?.ToString())) return;
                var ordinal = ordinals[index];
                Dispatcher.BeginInvoke(new Action(() => {
                    if (jumpRow is not null) jumpRow(index, ordinal);
                    else jump(ordinal);
                }), DispatcherPriority.Input);
            }), true);
    }
    private static string Time(int ordinal) => EventCsvExporter.FormatTime(ordinal);
    private static string WeatherName(int weather) => weather switch {
        0 => "快晴", 1 => "雾雨", 2 => "昙天", 3 => "苍天", 4 => "冰雹",
        5 => "花昙", 6 => "浓雾", 7 => "雪", 8 => "天気雨", 9 => "疏雨",
        10 => "风雨", 11 => "晴岚", 12 => "川雾", 13 => "台风", 14 => "平静",
        15 => "钻石尘", 16 => "黄砂", 17 => "烈日", 18 => "梅雨", 19 => "极光",
        20 => "黄昏", 21 => "无天气", _ => $"未知天气 {weather}"
    };

    private UIElement BuildSidebar()
    {
        var dock = new DockPanel { Background = Navy };
        sidebarDock = dock;
        var foot = Vertical(Text("SOKU REPLAY LAB", 11, palette["SidebarAccent"], true),
            Text("本地逐帧分析", 11, palette["SidebarMuted"]));
        foot.Margin = new Thickness(21, 15, 15, 20);
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);
        var brand = Vertical(Text("SOKU REPLAY LAB", 10, palette["SidebarAccent"], true),
            Text("非想天则复盘", 18, palette["SidebarText"], true),
            Text("从回放到证据的工作台", 11, palette["SidebarMuted"]));
        brand.Margin = new Thickness(20, 27, 10, 28);
        DockPanel.SetDock(brand, Dock.Top);
        dock.Children.Add(brand);
        var scroll = new ScrollViewer { Content = navigation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        navigation.Margin = new Thickness(12, 4, 12, 5);
        dock.Children.Add(scroll);
        return dock;
    }
    private UIElement BuildTopbar()
    {
        var grid = new Grid { Height = 65, Background = Surface };
        topbarGrid = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(29, 0, 12, 0) };
        pageTitleText = Text(Pages[page], 25, Strong, true);
        pageTitleText.TextWrapping = TextWrapping.NoWrap;
        titleRow.Children.Add(pageTitleText);
        replayFrameCountText = Text("", 12, Muted);
        replayFrameCountText.Margin = new Thickness(14, 3, 0, 0);
        titleRow.Children.Add(replayFrameCountText);
        grid.Children.Add(titleRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 29, 0) };
        topbarActions = actions;
        connection.Foreground = palette["Warning"];
        connection.FontSize = 12;
        connection.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(new Border { Child = connection,
            Background = palette.Alternating, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 12, 0) });
        actions.Children.Add(Button("启动游戏", StartGame));
        actions.Children.Add(Button("连接游戏", ConnectGame));
        actions.Children.Add(Button("导入 REP / 轨迹", Import, true));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }
    private UIElement BuildTransportBar()
    {
        var grid = new Grid { Height = 64, Background = Surface,
            Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Bottom };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transportPlayButton = Button("播放", TogglePlayback, true);
        transportPlayButton.Margin = new Thickness(25, 0, 12, 0);
        transportPlayButton.VerticalAlignment = VerticalAlignment.Center;
        transportPlayButton.MinWidth = 72;
        Grid.SetColumn(transportPlayButton, 0);
        var transportActions = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        transportActions.Children.Add(transportPlayButton);
        takeoverButton = Button("接管", () => _ = GiveGameFocusAsync());
        takeoverButton.Margin = new Thickness(0, 0, 12, 0);
        transportActions.Children.Add(takeoverButton);
        grid.Children.Add(transportActions);
        replaySlider = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        replaySlider.ValueChanged += (_, _) => {
            if (updatingTransport || session?.Frames.Count is not > 0) return;
            if (probe is null && detectedGame is null)
            {
                cursor = Math.Clamp((int)Math.Round(replaySlider.Value), 0, session.Frames.Count - 1);
                UpdateTransportTime();
                refreshReplayFrame?.Invoke();
            }
        };
        replaySlider.PreviewMouseLeftButtonUp += (_, _) => CommitTransportSeek();
        replaySlider.KeyUp += (_, e) => {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                CommitTransportSeek();
        };
        Grid.SetColumn(replaySlider, 1);
        grid.Children.Add(replaySlider);
        transportTimeLabel = Text("— / —", 12, Muted);
        transportTimeLabel.Margin = new Thickness(0, 0, 20, 0);
        Grid.SetColumn(transportTimeLabel, 2);
        grid.Children.Add(transportTimeLabel);
        var speeds = new WrapPanel { VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 21, 0) };
        foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.5, 2.0 })
        {
            var selectedRate = rate;
            var control = Button($"{rate:0.##}×", () => {
                playbackSpeed = selectedRate;
                SaveSettings();
                UpdateTransport();
                SetGameSpeed(selectedRate);
            });
            control.Padding = new Thickness(9, 5, 9, 5);
            transportSpeeds.Add((rate, control));
            speeds.Children.Add(control);
        }
        Grid.SetColumn(speeds, 3);
        grid.Children.Add(speeds);
        return grid;
    }
    private void CommitTransportSeek()
    {
        if (session?.Frames.Count is not > 0) return;
        SeekGameAt((int)Math.Round(replaySlider?.Value ?? cursor));
    }
    private void UpdateTransportTime()
    {
        if (transportTimeLabel is null) return;
        transportTimeLabel.Text = session?.Frames.Count is > 0
            ? $"{Time(cursor)} / {Time(session.Frames.Count - 1)}" : "— / —";
    }
    private void UpdateTransport()
    {
        replayWorkspace?.UpdateEditAvailability();
        if (replaySlider is null) return;
        updatingTransport = true;
        try
        {
            var count = session?.Frames.Count ?? 0;
            var canControlSession = (probe is null && detectedGame is null) || session?.Header is not null &&
                string.Equals(System.IO.Path.GetExtension(session.Source), ".rep", StringComparison.OrdinalIgnoreCase);
            if (takeoverButton is not null) {
                takeoverButton.IsEnabled = detectedGame is not null && gameKeyboardFocus is null &&
                    !givingGameFocus && !returningGameFocus && !nativeTransportBusy &&
                    !fullCaptureBusy && !branchBusy && !escapeBusy && !sessionLoading;
                takeoverButton.Content = gameKeyboardFocus is null ? "接管" : $"接管中 · {ExitTakeoverKeyLabel} 退出";
            }
            replaySlider.IsEnabled = count > 0 && canControlSession && !PlaybackOperationBlocked;
            replaySlider.Maximum = Math.Max(1, count - 1);
            if (!replaySlider.IsMouseCaptureWithin && pendingSeekTarget is null && !TakeoverTransition)
                replaySlider.Value = count > 0 ? Math.Clamp(cursor, 0, count - 1) : 0;
            if (transportPlayButton is not null)
            {
                transportPlayButton.IsEnabled = canControlSession && !PlaybackOperationBlocked && !nativeTransportBusy &&
                    (count > 0 || probeStatus?.Status is 2 or 3);
                transportPlayButton.Content = NativeSessionMatches && probeStatus?.Status == 2 || playing ? "暂停" : "播放";
            }
            UpdateTransportTime();
            foreach (var (rate, control) in transportSpeeds)
            {
                control.IsEnabled = canControlSession && !PlaybackOperationBlocked;
                var selected = Math.Abs(playbackSpeed - rate) < .001;
                control.Background = selected ? Blue : Surface;
                control.Foreground = selected ? palette["OnAccent"] : TextColor;
                control.BorderBrush = selected ? Blue : Border;
            }
        }
        finally { updatingTransport = false; }
    }
    private void BuildNavigation()
    {
        var shape = expert + ":" + string.Join(',', enabled);
        if (navigationShape == shape)
        {
            foreach (var item in navigationButtons)
            {
                var selected = item.Page == page;
                item.Button.Background = selected ? Blue : Brushes.Transparent;
                item.Button.Foreground = selected ? palette["OnAccent"] : palette["SidebarText"];
                item.Icon.Foreground = selected ? palette["OnAccent"] : palette["SidebarAccent"];
                item.Name.Foreground = selected ? palette["OnAccent"] : palette["SidebarText"];
            }
            return;
        }
        navigationShape = shape;
        navigationButtons.Clear();
        navigation.Children.Clear();
        string? last = null;
        for (var i = 0; i < Pages.Length; i++)
        {
            if (i != 0 && (!enabled[i] || !expert && i == 8)) continue;
            if (Groups[i] != last)
            {
                last = Groups[i];
                var group = Text(last, 11, palette["SidebarMuted"], true);
                group.Margin = new Thickness(11, 17, 0, 7);
                navigation.Children.Add(group);
            }
            var index = i;
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(Text(PageIcons[i], 15,
                page == i ? palette["OnAccent"] : palette["SidebarAccent"], true));
            var name = Text(Pages[i], 13,
                page == i ? palette["OnAccent"] : palette["SidebarText"], true);
            name.Margin = new Thickness(11, 0, 0, 0);
            label.Children.Add(name);
            var button = new Button {
                Content = label, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 9, 6, 9), Margin = new Thickness(0, 1, 0, 1),
                Background = page == i ? Blue : Brushes.Transparent,
                Foreground = page == i ? palette["OnAccent"] : palette["SidebarText"],
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                FontWeight = FontWeights.SemiBold, FontSize = 13,
                Template = RoundedButtonTemplate()
            };
            button.Click += (_, _) => NavigateToPage(index);
            navigationButtons.Add((index, button, (TextBlock)label.Children[0], name));
            navigation.Children.Add(button);
        }
    }
    private void NavigateToPage(int index)
    {
        if (page == index) return;
        page = index;
        navigationRender = true;
        try { Render(); }
        finally { navigationRender = false; }
    }
    private void Render()
    {
        if (gameKeyboardFocus is not null) { UpdateTopNotice(); return; }
        if (!navigationRender) pageCache.Remove(page);
        var watch = Stopwatch.StartNew();
        try { RenderCore(); }
        finally
        {
            watch.Stop();
            if (watch.ElapsedMilliseconds >= 20)
                ConnectionTrace.Write("ui.render", detail:
                    $"page={page} duration_ms={watch.ElapsedMilliseconds} frames={session?.Frames.Count ?? 0}");
        }
    }
    private void RenderCore()
    {
        bool SurfaceMatchesGame() => !embedFailed && gameSurface?.IsAttached == true &&
            detectedGame is not null && gameSurface.GamePid == detectedGame.Pid &&
            gameSurface.GameWindow == detectedGame.Window;
        if (navigationRender && renderedPage is > 0 and < 9 && renderedPage != page)
            pageCache[renderedPage] = new CachedPage(content.Children.Cast<UIElement>()
                .Where(element => element.Visibility != Visibility.Collapsed &&
                    !parkedReplayElements.Contains(element)).ToArray(),
                renderedPage == 3 ? curveToolbarHost?.Content : null,
                session, samples.ToArray(), minPressureMoves);
        var retained = pageCache.Values.SelectMany(cached => cached.Children).ToHashSet();
        void ClearPageContent()
        {
            for (var i = content.Children.Count - 1; i >= 0; i--)
            {
                var element = content.Children[i];
                if (parkedReplayElements.Contains(element)) continue;
                if (retained.Contains(element)) element.Visibility = Visibility.Collapsed;
                else content.Children.RemoveAt(i);
            }
        }
        if (page == 0 && parkedReplayElements.Count > 0 && SurfaceMatchesGame())
        {
            ClearPageContent();
            foreach (var element in parkedReplayElements) element.Visibility = Visibility.Visible;
            parkedReplayElements.Clear();
            renderedPage = 0;
            if (pageScroller is not null)
            {
                pageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                pageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                pageScroller.ScrollToHome();
            }
        }
        if (page == 0 && renderedPage == 0 && !embedFailed &&
            gameSurface is not null && detectedGame is not null &&
            gameSurface.GamePid == detectedGame.Pid &&
            gameSurface.GameWindow == detectedGame.Window &&
            replayWorkspace is not null && replayPlaybackContent is not null &&
            replaySnapshotContent is not null && replayQuickContent is not null &&
            replayBookmarksContent is not null && replayFramePanel is not null &&
            replayFrameControls is not null && replayP1Hand is not null &&
            replayP2Hand is not null)
        {
            BuildNavigation();
            connection.Text = probe is null
                ? $"● PID {detectedGame.Pid} · 只读状态"
                : $"● PID {detectedGame.Pid} · 探针已接";
            RefreshReplayPageInPlace();
            UpdateTransport();
            return;
        }
        var parkReplay = page != 0 && renderedPage == 0 &&
            parkedReplayElements.Count == 0 && SurfaceMatchesGame();
        if (parkReplay)
        {
            parkedReplayElements.AddRange(content.Children.Cast<UIElement>()
                .Where(element => !retained.Contains(element)));
            foreach (var element in parkedReplayElements) element.Visibility = Visibility.Collapsed;
        }
        else if (gameSurface is not null &&
            (page == 0 || parkedReplayElements.Count == 0 || !SurfaceMatchesGame()))
        {
            if (!gameSurface.Detach())
            {
                message = "游戏窗口尚未还原；为保护游戏进程，当前页面保持不变。";
                return;
            }
            gameSurface = null;
            parkedReplayElements.Clear();
        }
        renderedPage = -1;
        if (parkedReplayElements.Count == 0)
        {
            replayFramePanel = replayFrameControls = null;
            replayWorkspace = null;
            replayPlaybackContent = replaySnapshotContent = replayQuickContent =
                replayBookmarksContent = null;
            replayP1Hand = replayP2Hand = null;
            replayPictureTitle = null;
            refreshReplayFrame = null;
            liveStatusLabel = null;
            probeStatusLabel = null;
        }
        if (pageScroller is not null)
        {
            pageScroller.VerticalScrollBarVisibility = page == 0
                ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
            pageScroller.HorizontalScrollBarVisibility = page == 0
                ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            pageScroller.ScrollToHome();
        }
        if (curveToolbarHost is not null)
        {
            curveToolbarHost.Content = null;
            curveToolbarHost.Visibility = Visibility.Collapsed;
        }
        BuildNavigation();
        connection.Text = detectedGame is null ? "● 游戏未连接" : probe is null
            ? $"● PID {detectedGame.Pid} · 只读状态"
            : $"● PID {detectedGame.Pid} · 探针已接";
        if (pageTitleText is not null) pageTitleText.Text = Pages[page];
        if (replayFrameCountText is not null)
        {
            replayFrameCountText.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
            replayFrameCountText.Text = page == 0 && session?.Header is not null
                ? $"REP 输入记录 {session.Header.InputFrames:N0} 帧" : "";
        }
        // Keep cached visual trees attached. Removing/readding a DataGrid
        // recreates its row/cell containers even when the data was cached.
        ClearPageContent();
        content.Margin = new Thickness(29, 12, 29, 22);
        if (navigationRender && pageCache.TryGetValue(page, out var cached) &&
            ReferenceEquals(cached.Session, session) && cached.Samples.SequenceEqual(samples) &&
            cached.PressureMoves == minPressureMoves)
        {
            foreach (var element in cached.Children)
            {
                if (!content.Children.Contains(element)) content.Children.Add(element);
                element.Visibility = Visibility.Visible;
            }
            if (page == 3 && curveToolbarHost is not null)
            {
                curveToolbarHost.Content = cached.Toolbar;
                curveToolbarHost.Visibility = Visibility.Visible;
            }
            renderedPage = page;
            UpdateTopNotice();
            UpdateTransport();
            return;
        }
        UpdateTopNotice();
        switch (page)
        {
            case 0: ReplayPage(); break;
            case 1: StatsPage(); break;
            case 2: MultiPage(); break;
            case 3: CurvesPage(); break;
            case 4: CombosPage(); break;
            case 5: PressurePage(); break;
            case 6: EscapePage(); break;
            case 7: InterpretPage(); break;
            case 8: EventsPage(); break;
            case 9: SettingsPage(); break;
        }
        renderedPage = page;
        UpdateTransport();
    }
    private void RefreshReplayPageInPlace()
    {
        if (replayFrameCountText is not null)
            replayFrameCountText.Text = session?.Header is null ? "" :
                $"REP 输入记录 {session.Header.InputFrames:N0} 帧";
        UpdateTopNotice();
        if (replayFramePanel is not null && replayFrameControls is not null &&
            replayP1Hand is not null && replayP2Hand is not null)
            PopulateReplayFramePanel(replayFramePanel, replayFrameControls,
                replayP1Hand, replayP2Hand);
        PopulateReplayTools();
    }
    private void UpdateTopNotice()
    {
        if (topNotice is null || topNoticeText is null) return;
        var text = importProgressText ??
            (message == hiddenNoticeMessage ? null : message);
        if (importProgressText is not null && dismissedImportProgress ||
            importProgressText is null && text == dismissedNoticeMessage)
            text = null;
        topNoticeText.Text = text ?? "";
        topNotice.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetImportProgress(int importedFrames, int totalFrames)
    {
        importProgressText = $"已导入 {Math.Clamp(importedFrames, 0, Math.Max(0, totalFrames)):N0} / REP 总共 {Math.Max(0, totalFrames):N0} 帧";
        UpdateTopNotice();
    }
    private void Empty(string detail) => content.Children.Add(Card("当前状态", Text(detail, 13, Muted)));
    private void Jump(int ordinal)
    {
        if (PlaybackOperationBlocked) return;
        page = 0;
        Render();
        SeekGameAt(ordinal);
    }
    private void SeekGameAt(int ordinal) => _ = SeekPlaybackAsync(ordinal);
    private void PausePlayback()
    {
        playing = false;
        playbackTimer.Stop();
        playbackRemainder = 0;
        playbackClock.Reset();
    }
    private void AdvancePlayback()
    {
        if (probe is not null || detectedGame is not null) { PausePlayback(); return; }
        if (!playing || session is null || session.Frames.Count == 0) { PausePlayback(); return; }
        var elapsed = playbackClock.Elapsed.TotalSeconds;
        playbackClock.Restart();
        playbackRemainder += elapsed * ReplayTiming.FramesPerSecond * playbackSpeed;
        var advance = (int)playbackRemainder;
        playbackRemainder -= advance;
        cursor = Math.Min(session.Frames.Count - 1, cursor + advance);
        if (replaySlider is not null) replaySlider.Value = cursor;
        if (cursor >= session.Frames.Count - 1) { PausePlayback(); Render(); }
    }
    private void SeekRelative(int frames)
    {
        if (session?.Frames.Count is not > 0) return;
        SeekGameAt(Math.Clamp((pendingSeekTarget ?? cursor) + frames, 0, session.Frames.Count - 1));
    }
    private void TogglePlayback()
    {
        if (page == 6 && (branchOriginal is null || selectedEscapeResult is not null &&
            session?.Source != selectedEscapeResult.ReplayPath))
        {
            if (escapeBusy || branchBusy || fullCaptureBusy) return;
            var result = selectedEscapeResult ?? escapeResults.LastOrDefault();
            if (result is null)
            {
                message = "请先计算并选择逃压推演结果，播放将使用对应的技能分支。";
                Render();
            }
            else _ = PlayEscapeResultAsync(result);
            return;
        }
        if (probe is not null)
        {
            _ = ToggleNativePlaybackAsync();
            return;
        }
        if (detectedGame is not null)
        {
            message = "请先连接探针，播放控制才能与游戏画面同步。";
            UpdateTopNotice();
            return;
        }
        if (PlaybackOperationBlocked) return;
        if (playing) PausePlayback();
        else
        {
            if (session?.Frames.Count is not > 0) return;
            if (cursor >= session.Frames.Count - 1) cursor = 0;
            playing = true;
            playbackClock.Restart();
            playbackTimer.Start();
        }
        Render();
    }
    private void ReplayPage()
    {
        var pictureTitle = Text(session?.Frames.Count > 0 ? $"轨迹位置 · {Time(cursor)}" : "等待游戏画面",
            18, palette["PanelText"], true);
        replayPictureTitle = pictureTitle;
        FrameworkElement pictureBody;
        if (probe is not null && detectedGame is not null && !embedFailed)
        {
            gameSurface = new GameSurfaceHost(detectedGame, error => {
                embedFailed = true;
                message = error;
            }, stateChanged: () => Dispatcher.BeginInvoke(new Action(() => {
                if (!shutdownInProgress && !shutdownReady) Render();
            }), DispatcherPriority.Background));
            pictureBody = gameSurface;
        }
        else
        {
            var placeholder = Vertical(pictureTitle,
                Text(embedFailed ? "游戏画面未能嵌入；可在下方重新尝试。" :
                    "连接游戏后在此显示实机画面，离线时仍可查看轨迹与分析。",
                    12, palette["PanelMuted"]));
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            pictureBody = new Border { Background = palette.Panel, Child = placeholder };
        }
        pictureBody.HorizontalAlignment = HorizontalAlignment.Center;
        pictureBody.VerticalAlignment = VerticalAlignment.Center;
        var p1Hand = new WrapPanel { Orientation = Orientation.Vertical };
        var p2Hand = new WrapPanel { Orientation = Orientation.Vertical };
        UIElement HandColumn(string title, WrapPanel cards)
        {
            var titleBlock = Text(title, 11, palette["PanelText"], true);
            titleBlock.HorizontalAlignment = HorizontalAlignment.Center;
            titleBlock.Margin = new Thickness(0, 0, 0, 8);
            return new Border { Background = palette["PanelRaised"], Padding = new Thickness(5),
                Child = new ScrollViewer { Content = Vertical(titleBlock, cards),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        }
        var pictureLayout = new Grid();
        pictureLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        pictureLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pictureLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        pictureLayout.Children.Add(HandColumn("1P 抽牌顺序", p1Hand));
        Grid.SetColumn(pictureBody, 1); pictureLayout.Children.Add(pictureBody);
        var p2Column = HandColumn("2P 抽牌顺序", p2Hand);
        Grid.SetColumn(p2Column, 2); pictureLayout.Children.Add(p2Column);
        var picture = new Border { Background = palette.Panel,
            CornerRadius = new CornerRadius(8), Child = pictureLayout };
        FrameworkElement? gamePanelFrame = null;
        void ApplyPictureSize()
        {
            var availableWidth = picture.ActualWidth > 0
                ? picture.ActualWidth - 176
                : (gamePanelFrame?.ActualWidth ?? 620) - 200;
            var availableHeight = gamePanelFrame is null ? 420
                : gamePanelFrame.ActualHeight - 38 - 24 -
                    (replayFramePanel?.ActualHeight ?? 0) -
                    (replayFrameControls?.ActualHeight ?? 0) - 20;
            var width = Math.Clamp(Math.Min(availableWidth,
                (availableHeight - 20) * 4 / 3), 160, 1200);
            var height = width * 3 / 4;
            if (double.IsNaN(pictureBody.Width) || Math.Abs(pictureBody.Width - width) > .5)
                pictureBody.Width = width;
            if (double.IsNaN(pictureBody.Height) || Math.Abs(pictureBody.Height - height) > .5)
                pictureBody.Height = height;
            if (double.IsNaN(picture.Height) || Math.Abs(picture.Height - height - 20) > .5)
                picture.Height = height + 20;
        }
        picture.SizeChanged += (_, _) => ApplyPictureSize();
        var left = Vertical(picture);
        replayFramePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        replayFrameControls = new StackPanel();
        replayP1Hand = p1Hand;
        replayP2Hand = p2Hand;
        left.Children.Insert(0, replayFramePanel);
        left.Children.Add(replayFrameControls);
        replayFramePanel.SizeChanged += (_, _) => ApplyPictureSize();
        replayFrameControls.SizeChanged += (_, _) => ApplyPictureSize();
        ApplyPictureSize();
        PopulateReplayFramePanel(replayFramePanel, replayFrameControls, p1Hand, p2Hand);
        replayPlaybackContent = new ContentControl();
        replaySnapshotContent = new ContentControl();
        replayQuickContent = new ContentControl();
        replayBookmarksContent = new ContentControl();
        PopulateReplayTools();
        ReplayWorkspace? workspace = null;
        workspace = new ReplayWorkspace((id, layout) => {
            if (!ReferenceEquals(replayWorkspace, workspace)) return;
            replayPanelLayouts[id] = layout;
        }, (state, workspaces) => {
            if (!ReferenceEquals(replayWorkspace, workspace)) return;
            replayDockState = state;
            replayDockDefaultState = workspace!.DefaultLayout;
            replayDockWorkspaces.Clear();
            foreach (var (name, saved) in workspaces) replayDockWorkspaces[name] = saved;
            SaveSettings();
        }, replayDockState, replayDockWorkspaces,
            () => !ReplayControlInFlight && !PlaybackOperationBlocked, Blue, replayDockDefaultState, TextColor, palette["OnAccent"]);
        replayWorkspace = workspace;
        static bool Wide(double width) => width >= 920;
        static double MainWidth(double width) => Wide(width)
            ? Math.Max(540, Math.Round((width - 12) * .66)) : Math.Max(540, width);
        static double SideX(double width) => Wide(width) ? MainWidth(width) + 12 : 0;
        static double SideWidth(double width) => Wide(width)
            ? Math.Max(300, width - SideX(width)) : Math.Max(300, width);
        static double SideTop(double width, double wideTop) => Wide(width)
            ? wideTop : 662 + wideTop;
        ReplayPanelLayout? Saved(string id) => replayPanelLayouts.GetValueOrDefault(id);
        FrameworkElement Panel(string id, string title, UIElement body, double minWidth, double minHeight,
            Func<double, ReplayPanelLayout> fallback) => replayWorkspace.AddPanel(id, title,
                body, minWidth, minHeight, fallback, Saved(id), Surface, Border, Navy, palette["SidebarText"]);
        gamePanelFrame = Panel("game", "对局画面与当前状态", left, 420, 360,
            width => new ReplayPanelLayout(0, 0, MainWidth(width), 650));
        gamePanelFrame.SizeChanged += (_, _) => ApplyPictureSize();
        ApplyPictureSize();
        Panel("playback", "压制缝提示", replayPlaybackContent, 300, 220,
            width => new ReplayPanelLayout(SideX(width), SideTop(width, 0),
                SideWidth(width), 300));
        Panel("snapshot", "固定快照", replaySnapshotContent, 300, 180,
            width => new ReplayPanelLayout(SideX(width), SideTop(width, 312),
                SideWidth(width), 230));
        Panel("quick", "快速定位", replayQuickContent, 300, 180,
            width => new ReplayPanelLayout(SideX(width), SideTop(width, 554),
                SideWidth(width), 240));
        Panel("bookmarks", "时间标记", replayBookmarksContent, 300, 160,
            width => new ReplayPanelLayout(SideX(width), SideTop(width, 806),
                SideWidth(width), 180));
        replayWorkspace.CompleteRegistration();
        content.Children.Add(replayWorkspace);
    }
    private void PopulateReplayFramePanel(StackPanel framePanel, StackPanel frameControls,
        WrapPanel p1Hand, WrapPanel p2Hand)
    {
        framePanel.Children.Clear();
        frameControls.Children.Clear();
        p1Hand.Children.Clear();
        p2Hand.Children.Clear();
        refreshReplayFrame = null;
        if (session?.Frames.Count > 0)
        {
            var selectedSession = session;
            var position = Text($"{Time(cursor)} / {Time(selectedSession.Frames.Count - 1)}", 12, Muted);
            var p1State = Text("", 12, TextColor);
            var p2State = Text("", 12, TextColor);
            var renderedHands = new string?[2];
            void RefreshFrame()
            {
                if (!ReferenceEquals(session, selectedSession)) return;
                var frame = selectedSession.Frames[Math.Clamp(cursor, 0, selectedSession.Frames.Count - 1)];
                if (replayPictureTitle is not null) replayPictureTitle.Text = $"轨迹位置 · {Time(cursor)}";
                position.Text = $"{Time(cursor)} / {Time(selectedSession.Frames.Count - 1)}";
                p1State.Text = $"HP {frame.P1.Hp:N0} · 灵力 {frame.P1.Spirit:N0}\n" +
                    $"{ActionCatalog.Display(frame.P1.Action, frame.P1.Character)} [{frame.P1.Action}]";
                p2State.Text = $"HP {frame.P2.Hp:N0} · 灵力 {frame.P2.Spirit:N0}\n" +
                    $"{ActionCatalog.Display(frame.P2.Action, frame.P2.Character)} [{frame.P2.Action}]";
                foreach (var (panel, fighter, side) in new[] { (p1Hand, frame.P1, 0), (p2Hand, frame.P2, 1) })
                {
                    var handKey = fighter.Character + ":" + (fighter.HandIds is null ? "?" : string.Join(',', fighter.HandIds));
                    if (renderedHands[side] == handKey) continue;
                    renderedHands[side] = handKey;
                    panel.Children.Clear();
                    if (fighter.HandIds is null || fighter.HandIds.Length == 0)
                    {
                        panel.Children.Add(Text("暂无抽牌", 10, palette["SidebarMuted"]));
                        continue;
                    }
                    foreach (var id in fighter.HandIds)
                    {
                        var art = CardArt.Get(fighter.Character, id);
                        if (art is null) continue;
                        panel.Children.Add(new Image {
                            Source = art, Width = 25, Height = 40,
                            Margin = new Thickness(2, 0, 2, 5),
                            ToolTip = CardTimeline.CardName(fighter.Character, id)
                        });
                    }
                }
            }
            refreshReplayFrame = RefreshFrame;
            framePanel.Children.Add(position);
            UIElement SideState(string label, TextBlock value, Brush accent)
            {
                var panel = Vertical(Text(label, 12, accent, true), value);
                return Box(panel, palette.Alternating, new Thickness(12, 9, 12, 9));
            }
            framePanel.Children.Add(Columns(
                (SideState(PlayerIdentity(0), p1State, Blue), 1),
                (SideState(PlayerIdentity(1), p2State, Red), 1)));
            RefreshFrame();
            var controls = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) };
            controls.Children.Add(Button("后退 1 帧", () => SeekRelative(-1)));
            controls.Children.Add(Button("前进 1 帧", () => SeekRelative(1)));
            controls.Children.Add(Button("后退 1 秒", () => SeekRelative(-ReplayTiming.FramesPerSecond)));
            controls.Children.Add(Button("前进 1 秒", () => SeekRelative(ReplayTiming.FramesPerSecond)));
            if (showReplayTools) frameControls.Children.Add(controls);
        }
        else framePanel.Children.Add(Text("导入 REP 或轨迹后在此显示当前帧的双方状态。", 12, Muted));
    }
    private void PopulateReplayTools()
    {
        if (replayPlaybackContent is null || replaySnapshotContent is null ||
            replayQuickContent is null || replayBookmarksContent is null) return;
        // Keep every event available without constructing a button for every
        // item on each refresh. Recycling only creates the visible rows.
        var quick = new ListBox {
            ItemsSource = session?.Events.Select(e => new {
                e.Ordinal, Label = $"{Time(e.Ordinal)} · {e.Kind}\n{e.Detail}"
            }).ToArray(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = TextColor
        };
        VirtualizingStackPanel.SetIsVirtualizing(quick, true);
        VirtualizingStackPanel.SetVirtualizationMode(quick, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(quick, true);
        ScrollViewer.SetVerticalScrollBarVisibility(quick, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(quick, ScrollBarVisibility.Disabled);
        var row = new FrameworkElementFactory(typeof(Button));
        row.SetBinding(FrameworkElement.TagProperty, new Binding("Ordinal"));
        row.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        row.SetValue(Control.BackgroundProperty, Surface);
        row.SetValue(Control.ForegroundProperty, TextColor);
        row.SetValue(Control.BorderBrushProperty, Border);
        row.SetValue(Control.PaddingProperty, new Thickness(9, 6, 9, 6));
        row.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, _) => {
            if (((Button)sender).Tag is int ordinal) Jump(ordinal);
        }));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding("Label"));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        label.SetValue(TextBlock.FontSizeProperty, 12.0);
        row.AppendChild(label);
        quick.ItemTemplate = new DataTemplate { VisualTree = row };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6)));
        quick.ItemContainerStyle = itemStyle;
        var replayCommands = new WrapPanel();
        if (branchOriginal is not null && !branchBusy)
            replayCommands.Children.Add(Button("返回原回放", ReturnOriginalBranch));
        replayPlaybackContent.Content = Vertical(replayCommands, GapControls());
        replaySnapshotContent.Content = new ScrollViewer {
            Content = SnapshotControls(),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        replayQuickContent.Content = quick.Items.Count > 0 ? quick :
            Text("载入含逐帧状态的轨迹后显示事件。", 12, Muted);
        replayBookmarksContent.Content = BookmarkControls();
    }
    private sealed record ReplaySnapshot(int Ordinal, int Defender, string Kind, int GapLength);
    private bool GapMeetsThreshold(ReplaySession current, PressureGap gap)
    {
        var needed = gapThreshold < 0
            ? PressureGapDetector.FourAStartup(current.Frames[Math.Clamp(gap.Start, 0,
                current.Frames.Count - 1)].Side(gap.Defender).Character)
            : gapThreshold;
        return gap.Length >= needed || needed == 0 && gap.MashableZero;
    }
    private UIElement SnapshotControls()
    {
        var panel = new StackPanel();
        if (session?.Frames.Count is not > 0) return panel;
        var current = session;
        var points = current.Pressures.Select(x =>
                new ReplaySnapshot(x.Start, x.Defender, "压制起点", 0))
            .Concat(current.PressureGaps.Where(x => GapMeetsThreshold(current, x))
                .Select(x => new ReplaySnapshot(x.Start, x.Defender, "可行动空隙", x.Length)))
            .Concat(probeStatus?.Status == 3 && branchOriginal is null &&
                    probeStatus.Ordinal >= 0
                ? [new ReplaySnapshot(probeStatus.Ordinal, 0, "当前暂停帧", 0)]
                : [])
            .Concat(nativeSnapshotSlots.Select(x =>
                new ReplaySnapshot(x, 0, "原生内存点", 0)))
            .Where(x => x.Ordinal >= 0 && x.Ordinal < current.Frames.Count)
            .DistinctBy(x => (x.Ordinal, x.Defender, x.Kind))
            .OrderBy(x => x.Ordinal).ToArray();
        if (points.Length == 0) return panel;
        var selected = Array.FindLastIndex(points, x => x.Ordinal <= cursor);
        if (selected < 0) selected = 0;
        var active = points[selected];
        var navigator = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        if (selected > 0)
            navigator.Children.Add(Button("上一处", () => Jump(points[selected - 1].Ordinal)));
        navigator.Children.Add(Button("进入当前", () => Jump(active.Ordinal), true));
        if (selected + 1 < points.Length)
            navigator.Children.Add(Button("下一处", () => Jump(points[selected + 1].Ordinal)));
        panel.Children.Add(navigator);
        var nativeReady = NativeSessionMatches && probe is not null && probeStatus?.Status == 3 &&
            session.Header is not null && branchOriginal is null &&
            nativeSnapshotPid == probe.Pid &&
            string.Equals(nativeSnapshotSource, session.Source,
                StringComparison.OrdinalIgnoreCase);
        if (NativeSessionMatches && probe is not null && probeStatus?.Status == 3 && session.Header is not null &&
            branchOriginal is null && nativeSnapshotSlots.Count == 0)
        {
            nativeSnapshotPid = probe.Pid;
            nativeSnapshotSource = session.Source;
            nativeReady = true;
        }
        if (nativeReady)
        {
            var pausedOrdinal = probeStatus!.Ordinal;
            if (!nativeSnapshotSlots.Contains(pausedOrdinal))
                panel.Children.Add(Button("保存当前暂停帧的游戏内存",
                    async () => await SaveNativeSnapshotAsync(pausedOrdinal), true));
            foreach (var slot in nativeSnapshotSlots.OrderBy(x => x))
            {
                var savedOrdinal = slot;
                panel.Children.Add(Button($"直接恢复 {Time(savedOrdinal)} 的内存点",
                    async () => await RestoreNativeSnapshotAsync(savedOrdinal)));
            }
        }
        foreach (var point in points.Skip(Math.Max(0, selected - 2)).Take(6))
        {
            var target = point;
            panel.Children.Add(Button($"{Time(point.Ordinal)}  {point.Kind} · {point.Defender + 1}P" +
                (point.Kind == "可行动空隙" ? $" · {point.GapLength} 帧" : ""),
                () => Jump(target.Ordinal)));
        }
        return panel;
    }

    private void ClearNativeSnapshots()
    {
        nativeSnapshotSlots.Clear();
        nativeSnapshotPid = -1;
        nativeSnapshotSource = null;
    }

    private async Task SaveNativeSnapshotAsync(int ordinal)
    {
        if (PlaybackOperationBlocked || ReplayControlInFlight || !NativeSessionMatches || probe is null || probeStatus?.Status != 3 || probeStatus.Ordinal != ordinal ||
            session?.Header is null || branchOriginal is not null) return;
        if (nativeSnapshotSlots.Count >= 16 && !nativeSnapshotSlots.Contains(ordinal))
        {
            message = "本次回放最多保存 16 个原生内存点。";
            Render();
            return;
        }
        nativeTransportBusy = true;
        await playbackControlGate.WaitAsync();
        try
        {
            if (PlaybackOperationBlocked) return;
            probeStatus = await probe.SendAsync(ProbeCommand.SaveSnapshot, target: ordinal);
            nativeSnapshotPid = probe.Pid;
            nativeSnapshotSource = session.Source;
            nativeSnapshotSlots.Add(ordinal);
            message = $"已保存第 {ordinal:N0} 帧的原生游戏状态。";
        }
        catch (Exception ex) { message = $"原生快照保存失败：{ex.Message}"; }
        finally { nativeTransportBusy = false; playbackControlGate.Release(); UpdateTransport(); }
        Render();
    }

    private async Task RestoreNativeSnapshotAsync(int ordinal)
    {
        if (probe is null || probeStatus?.Status != 3 ||
            !nativeSnapshotSlots.Contains(ordinal) ||
            nativeSnapshotPid != probe.Pid ||
            !string.Equals(nativeSnapshotSource, session?.Source,
                StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await SeekPlaybackAsync(ordinal);
            if (probe.Read().Status != 3 || probe.Read().Ordinal != ordinal || cursor != ordinal) return;
            message = $"已直接恢复第 {ordinal:N0} 帧的游戏内存状态。";
        }
        catch (Exception ex)
        {
            nativeSnapshotSlots.Remove(ordinal);
            message = $"原生快照恢复失败：{ex.Message}";
        }
        Render();
    }

    private UIElement BookmarkControls()
    {
        var panel = new StackPanel();
        if (session?.Frames.Count is not > 0)
        {
            panel.Children.Add(Text("导入逐帧轨迹后可保存常看的时间点。", 12, Muted));
            return panel;
        }
        var source = System.IO.Path.GetFullPath(session.Source);
        var positions = bookmarks.GetValueOrDefault(source);
        panel.Children.Add(Button("保存当前位置", () =>
        {
            if (!bookmarks.TryGetValue(source, out var saved))
                bookmarks[source] = saved = [];
            if (saved.Count >= 12 && !saved.Contains(cursor))
            {
                message = "每份回放最多保存 12 个时间标记。";
                Render();
                return;
            }
            saved.Add(cursor);
            SaveSettings();
            Render();
        }, true));
        if (positions is null || positions.Count == 0)
        {
            panel.Children.Add(Text("尚无时间标记。", 12, Muted));
            return panel;
        }
        foreach (var ordinal in positions.Where(x => x < session.Frames.Count).ToArray())
        {
            var selected = ordinal;
            var row = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            row.Children.Add(Button(Time(ordinal), () => Jump(selected)));
            row.Children.Add(Button("移除", () =>
            {
                positions.Remove(selected);
                SaveSettings();
                Render();
            }));
            panel.Children.Add(row);
        }
        panel.Children.Add(Text("时间标记只保存位置；跳转游戏回放时会按当前位置重新定位。", 11, Muted));
        return panel;
    }

    private UIElement GapControls()
    {
        var panel = new StackPanel();
        var choices = new WrapPanel();
        foreach (var (label, frames) in new[] {
            ("自动", -1), ("0 帧", 0), ("4 帧", 4),
            ("6 帧", 6), ("8 帧", 8), ("10 帧", 10) })
        {
            var selected = frames;
            choices.Children.Add(Button(label, () => {
                gapThreshold = selected;
                SaveSettings();
                Render();
            }, gapThreshold == frames));
        }
        panel.Children.Add(choices);
        if (session?.Frames.Count is not > 0)
        {
            panel.Children.Add(Text("导入逐帧轨迹后显示可定位的防御空隙。", 12, Muted));
            return panel;
        }
        var current = session;
        var eligible = current.PressureGaps.Where(gap => GapMeetsThreshold(current, gap)).ToArray();
        panel.Children.Add(Text(gapThreshold < 0
            ? $"以防守角色 4A 的首个生效帧为门槛 · 共 {eligible.Length} 处"
            : $"至少 {gapThreshold} 个自由帧 · 共 {eligible.Length} 处",
            12, Muted));
        var upcoming = eligible.Where(gap => gap.Start >= cursor).Take(8).ToArray();
        if (upcoming.Length == 0) upcoming = eligible.Take(8).ToArray();
        foreach (var gap in upcoming)
        {
            var selectedGap = gap;
            panel.Children.Add(Button(
                $"{Time(gap.Start)}  {gap.Defender + 1}P 防守 · {gap.Length} 帧",
                () => Jump(selectedGap.Start)));
        }
        if (eligible.Length == 0)
            panel.Children.Add(Text("当前门槛下没有符合条件的压制缝。", 12, Muted));
        return panel;
    }

    private async void ReturnOriginalBranch()
    {
        if (PlaybackOperationBlocked || ReplayControlInFlight || probe is null || branchOriginal is null || branchBusy) return;
        branchBusy = true;
        ClearNativeSnapshots();
        Render();
        await playbackControlGate.WaitAsync();
        try
        {
            if (shutdownInProgress || shutdownReady) return;
            nativeReplaySource = null;
            nativePlaybackReleased = true;
            probeStatus = await ReplayBranchRunner.RestoreAsync(probe,
                branchOriginal, root, branchAnchor);
            session = branchOriginal;
            nativeReplaySource = session.Source;
            nativePlaybackReleased = false;
            probeStatus = await probe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
            AdoptPlaybackPosition(probeStatus);
            branchOriginal = null;
            message = $"已返回原回放第 {cursor} 帧。";
        }
        catch (Exception ex) { message = $"返回原回放失败：{ex.Message}"; }
        finally { branchBusy = false; playbackControlGate.Release(); Render(); }
    }
    private void StatsPage()
    {
        if (session?.Frames.Count == 0 || session is null) { Empty("REP 头已读取；尚无该 REP 的逐帧引擎轨迹，不能推算伤害和动作。"); return; }
        var filters = new WrapPanel();
        foreach (var (caption, side) in new[] { ("双方", -1), ("1P", 0), ("2P", 1) })
        {
            var selectedSide = side;
            filters.Children.Add(Button(caption, () => { statsSide = selectedSide; SaveSettings(); Render(); }, statsSide == side));
        }
        filters.Children.Add(Text("     回合：", 12, Muted));
        filters.Children.Add(Button("全部", () => { statsRound = -1; SaveSettings(); Render(); }, statsRound == -1));
        foreach (var round in session.Stats.Select(x => x.Round).Where(x => x >= 0).Distinct().Order())
        {
            var selectedRound = round;
            filters.Children.Add(Button($"第 {round + 1} 回合",
                () => { statsRound = selectedRound; SaveSettings(); Render(); }, statsRound == round));
        }
        var categories = new WrapPanel();
        foreach (var name in new[] { "全表", "时间、资源与输入", "命中与伤害",
            "攻防判定", "防御", "主动压制", "受身与逃压", "移动与招式", "招式使用" })
        {
            var selected = name;
            categories.Children.Add(Button(name, () => { statsCategory = selected; SaveSettings(); Render(); },
                statsCategory == name));
        }
        categories.Margin = new Thickness(0, 9, 0, 0);
        var export = Button("导出当前筛选为 CSV", ExportStats);
        export.Margin = new Thickness(0, 9, 0, 0);
        content.Children.Add(Card("逐本数据筛选", Vertical(filters, categories, export)));
        UIElement SideReport(int side)
        {
            var report = LegacyStatsSideReport(session, side);
            return Card(PlayerIdentity(side) + " · 逐本数据", report);
        }
        if (statsSide < 0)
            content.Children.Add(Columns((SideReport(0), 1), (SideReport(1), 1)));
        else content.Children.Add(SideReport(statsSide));
    }
    private static IEnumerable<(int Side, int Round, string Move, int Count)> MoveUsage(ReplaySession data)
        => ReplayInterpretation.MoveCounts(data)
            .Select(x => (x.Key.Side, x.Key.Round, x.Key.Move, x.Value));
    private void MultiPage()
    {
        var list = samples.Count > 0 ? samples : session is null ? [] : [session];
        var picker = new WrapPanel();
        picker.Children.Add(Button("添加 REP / 轨迹", Import));
        if (session is not null)
            picker.Children.Add(Button("移除当前", () => _ = RemoveCurrentSampleAsync()));
        if (list.Count > 0)
            picker.Children.Add(Button("清空列表", () => _ = ClearAllSamplesAsync()));
        picker.Children.Add(Button("导出全部样本统计为 CSV", ExportMultiStats));
        if (list.Count > 0)
        {
            var samplePicker = new ComboBox {
                Width = 260, Margin = new Thickness(0, 0, 8, 0),
                ItemsSource = list.Select(x => System.IO.Path.GetFileName(x.Source)).ToArray(),
                SelectedIndex = Math.Max(0, list.FindIndex(x =>
                    string.Equals(x.Source, session?.Source, StringComparison.OrdinalIgnoreCase)))
            };
            picker.Children.Add(samplePicker);
            picker.Children.Add(Button("打开所选 REP", () => {
                if (samplePicker.SelectedIndex is >= 0 && samplePicker.SelectedIndex < list.Count)
                    _ = Load(list[samplePicker.SelectedIndex].Source);
            }));
        }
        content.Children.Add(Card("分析样本", picker));
        if (list.Count == 0) { Empty("添加样本后比较。"); return; }
        var valid = list.Where(x => x.Frames.Count > 0 && x.RoundCount > 0).ToArray();
        var sampleTable = Table(
            ["REP", "1P", "2P", "回合", "1P 伤害", "2P 伤害"],
            list.Select(x => new[] { System.IO.Path.GetFileName(x.Source), x.Header?.Player1 ?? "未知",
                x.Header?.Player2 ?? "未知", x.RoundCount.ToString(), x.Frames.Count == 0 ? "数据不足" : x.Total(0).Damage.ToString(),
                x.Frames.Count == 0 ? "数据不足" : x.Total(1).Damage.ToString() }));
        content.Children.Add(Card("REP 列表", sampleTable));
        var categories = new[] { "全表", "时间与输入", "伤害与连段", "防守与压制", "资源与行动" };
        var categoryPicker = new WrapPanel();
        foreach (var category in categories)
        {
            var selected = category;
            categoryPicker.Children.Add(Button(category, () => {
                multiCategory = selected;
                SaveSettings();
                Render();
            }, multiCategory == category));
        }
        content.Children.Add(Card("比较类别", categoryPicker));
        var metrics = MultiMetrics().Where(x => multiCategory == "全表" ||
            x.Category == multiCategory).ToArray();
        static string Display(double? value, MultiMetric metric) => value is null
            ? "数据不足" : value.Value.ToString($"N{metric.Digits}") + metric.Unit;
        var averages = metrics.Select(metric =>
        {
            var p1 = valid.Select(x => metric.Value(x, 0)).Where(x => x.HasValue)
                .Select(x => x!.Value).ToArray();
            var p2 = valid.Select(x => metric.Value(x, 1)).Where(x => x.HasValue)
                .Select(x => x!.Value).ToArray();
            return new[] { metric.Name,
                Display(p1.Length == 0 ? null : p1.Average(), metric), p1.Length.ToString(),
                Display(p2.Length == 0 ? null : p2.Average(), metric), p2.Length.ToString() };
        }).ToArray();
        UIElement SideAverage(int side)
        {
            var rows = averages.Select(row => new[] { row[0], row[side == 0 ? 1 : 3],
                row[side == 0 ? 2 : 4] + "/" + list.Count });
            var table = Table(["统计项", "每份 REP 平均", "样本"], rows);
            return Card($"{side + 1}P · 多局平均", table);
        }
        content.Children.Add(Columns((SideAverage(0), 1), (SideAverage(1), 1)));
        content.Children.Add(Columns(
            (Metric("总对局", list.Count.ToString(), "已加入样本"), 1),
            (Metric("有效回合", valid.Sum(x => x.RoundCount).ToString(), "存在引擎轨迹"), 1),
            (Metric("1P 平均伤害", valid.Length == 0 ? "—" : valid.Average(x => x.Total(0).Damage / Math.Max(1,x.RoundCount)).ToString("N0"), "每回合"), 1),
            (Metric("2P 平均伤害", valid.Length == 0 ? "—" : valid.Average(x => x.Total(1).Damage / Math.Max(1,x.RoundCount)).ToString("N0"), "每回合", Red), 1)));
        var details = list.SelectMany(item => metrics.Select(metric => new[] {
            System.IO.Path.GetFileName(item.Source), metric.Name,
            Display(item.Frames.Count == 0 || item.RoundCount == 0
                ? null : metric.Value(item, 0), metric),
            Display(item.Frames.Count == 0 || item.RoundCount == 0
                ? null : metric.Value(item, 1), metric)
        }));
        content.Children.Add(Card("逐份 REP 指标", Table(
            ["REP", "指标", "1P", "2P"], details)));
    }
    private async Task RemoveCurrentSampleAsync()
    {
        if (PlaybackOperationBlocked || ReplayControlInFlight || session is null || sessionLoading || branchBusy || escapeBusy ||
            fullCaptureBusy) return;
        PausePlayback();
        var removedSource = (branchOriginal ?? session).Source;
        bool IsRemoved(ReplaySession item) => string.Equals(System.IO.Path.GetFullPath(item.Source),
            System.IO.Path.GetFullPath(removedSource), StringComparison.OrdinalIgnoreCase);
        var next = samples.LastOrDefault(x => !IsRemoved(x));
        if (next is not null)
        {
            // Keep the current sample until its replacement has actually loaded.
            await Load(next.Source);
            if (session is not null && string.Equals(System.IO.Path.GetFullPath(session.Source),
                    System.IO.Path.GetFullPath(next.Source), StringComparison.OrdinalIgnoreCase))
            { samples.RemoveAll(IsRemoved); Render(); }
            return;
        }
        samples.RemoveAll(IsRemoved);
        nativeTransportBusy = true;
        await playbackControlGate.WaitAsync();
        session = null;
        cursor = 0;
        ResetReplayContext();
        try
        {
            if (probe is not null && probe.Read().Status is 2 or 3)
                probeStatus = await probe.SendAsync(ProbeCommand.Release);
            message = "已移除最后一份分析样本。";
        }
        catch (Exception ex) { message = $"样本已移除，但游戏回放未能释放：{ex.Message}"; }
        finally { nativeTransportBusy = false; playbackControlGate.Release(); UpdateTransport(); }
        Render();
    }
    private async Task ClearAllSamplesAsync()
    {
        if (PlaybackOperationBlocked || ReplayControlInFlight || sessionLoading || branchBusy || escapeBusy || fullCaptureBusy) return;
        PausePlayback();
        samples.Clear();
        nativeTransportBusy = true;
        await playbackControlGate.WaitAsync();
        session = null;
        cursor = 0;
        ResetReplayContext();
        try
        {
            if (probe is not null && probe.Read().Status is 2 or 3)
                probeStatus = await probe.SendAsync(ProbeCommand.Release);
            message = "多局样本列表已清空。";
        }
        catch (Exception ex) { message = $"样本已清空，但游戏回放未能释放：{ex.Message}"; }
        finally { nativeTransportBusy = false; playbackControlGate.Release(); UpdateTransport(); }
        Render();
    }
    private sealed record MultiMetric(string Category, string Name, string Unit, int Digits,
        Func<ReplaySession, int, double?> Value);
    private EscapeAttemptResult[] FilteredEscapeAttempts(ReplaySession data, int side,
        int round = -1)
    {
        var eligible = data.Pressures.Where(p => p.ConfirmedMoves >= minPressureMoves &&
            p.Defender == side && (round < 0 || p.Round == round))
            .Select(p => (p.Round, p.Start)).ToHashSet();
        return data.EscapeAttempts.Where(a => a.Defender == side &&
            (round < 0 || a.Round == round) &&
            eligible.Contains((a.Round, a.PressureStart))).ToArray();
    }
    private int PressureEventCount(ReplaySession data, int side, string kind, int round = -1)
    {
        var defenderEvent = kind == "被抓乱动";
        var eligible = data.Pressures.Where(p => p.ConfirmedMoves >= minPressureMoves &&
            (defenderEvent ? p.Defender : p.Attacker) == side &&
            (round < 0 || p.Round == round)).Select(p => (p.Round, p.Start)).ToHashSet();
        return data.Events.Count(e => e.Side == side && e.Kind == kind &&
            eligible.Contains((e.Round, e.Ordinal)));
    }
    private IReadOnlyList<MultiMetric> MultiMetrics() => [
        new("时间与输入", "对战秒数", " 秒", 1, (x, side) => x.Total(side).Seconds),
        new("时间与输入", "按键沿", "", 0, (x, side) => x.Total(side).Inputs),
        new("时间与输入", "有效输入", "", 0, (x, side) => x.Total(side).EffectiveInputs),
        new("时间与输入", "多余输入", "", 0, (x, side) => x.Total(side).RedundantInputs),
        new("时间与输入", "有效输入率", "%", 1, (x, side) => x.Total(side).Inputs > 0
            ? 100.0 * x.Total(side).EffectiveInputs / x.Total(side).Inputs : null),
        new("时间与输入", "每秒有效输入", "", 2, (x, side) => x.Total(side).Seconds > 0
            ? x.Total(side).EffectiveInputs / x.Total(side).Seconds : null),
        new("时间与输入", "前冲 66", "", 0, (x, side) => x.Total(side).ForwardDashes),
        new("时间与输入", "后冲 44", "", 0, (x, side) => x.Total(side).BackDashes),
        new("时间与输入", "DD 尝试", "", 0, (x, side) => x.Total(side).DdAttempts),
        new("时间与输入", "DD 成功", "", 0, (x, side) => x.Total(side).DdSuccess),
        new("时间与输入", "方向 D 合计", "", 0, (x, side) =>
            x.Total(side).DDirections.Values.Sum()),
        new("时间与输入", "大跳", "", 0, (x, side) =>
            x.Total(side).HighJumps.Values.Sum()),
        new("时间与输入", "小跳", "", 0, (x, side) =>
            x.Total(side).SmallJumps.Values.Sum()),
        new("时间与输入", "落地取消", "", 0, (x, side) =>
            x.Total(side).LandingCancelsKnown ? x.Total(side).LandingCancels : null),
        new("时间与输入", "移动距离/秒", "", 3, (x, side) => x.Total(side).Seconds > 0
            ? x.Total(side).Distance / 1280 / x.Total(side).Seconds : null),
        new("伤害与连段", "造成伤害", "", 0, (x, side) => x.Total(side).Damage),
        new("伤害与连段", "确认命中伤害", "", 0, (x, side) => x.Total(side).AttackDamage),
        new("伤害与连段", "磨血", "", 0, (x, side) => x.Total(side).ChipDamage),
        new("伤害与连段", "天气伤害", "", 0, (x, side) => x.Total(side).WeatherDamage),
        new("伤害与连段", "确认连段", "", 0, (x, side) => x.Total(side).Hits),
        new("伤害与连段", "低伤害弹幕命中", "", 0, (x, side) =>
            x.Total(side).ProjectileLowDamageKnown ? x.Total(side).ProjectileLowDamageHits : null),
        new("伤害与连段", "低伤害符卡参与", "", 0, (x, side) =>
            x.Total(side).LowDamageSpellHits),
        new("伤害与连段", "极限体术命中", "", 0, (x, side) =>
            x.Total(side).ExtremeMeleeKnown ? x.Total(side).ExtremeMeleeHits : null),
        new("伤害与连段", "每段连段伤害", "", 0, (x, side) => {
            var values = x.Combos.Where(combo => combo.Side == side).Select(combo => combo.Damage).ToArray();
            return values.Length == 0 ? null : values.Average();
        }),
        new("防守与压制", "承受伤害", "", 0, (x, side) => x.Total(side).TakenDamage),
        new("防守与压制", "防御接触", "", 0, (x, side) => x.Total(side).Blocks),
        new("防守与压制", "压起身失败", "", 0, (x, side) => x.Total(side).WakeupPressureFailures),
        new("防守与压制", "压起身", "", 0, (x, side) => x.Total(side).WakeupPressures),
        new("防守与压制", "压起身成功", "", 0, (x, side) => x.Total(side).WakeupPressureSuccess),
        new("防守与压制", "抓受身", "", 0, (x, side) => x.Total(side).TechChases),
        new("防守与压制", "抓受身成功", "", 0, (x, side) => x.Total(side).TechChaseSuccess),
        new("防守与压制", "凹尝试", "", 0, (x, side) =>
            x.Total(side).ReversalsKnown ? x.Total(side).ReversalsUsed : null),
        new("防守与压制", "凹命中", "", 0, (x, side) =>
            x.Total(side).ReversalsKnown ? x.Total(side).ReversalsHit : null),
        new("防守与压制", "防御硬直", " 秒", 1, (x, side) => x.Total(side).GuardFrames * ReplayTiming.SecondsPerFrame),
        new("防守与压制", "每段防御秒数", " 秒", 2, (x, side) =>
            x.Total(side).DefenseEpisodes == 0 ? 0 :
                x.Total(side).GuardFrames * ReplayTiming.SecondsPerFrame / x.Total(side).DefenseEpisodes),
        new("防守与压制", "发起压制", "", 0, (x, side) =>
            x.Pressures.Count(p => p.Attacker == side && p.ConfirmedMoves >= minPressureMoves)),
        new("防守与压制", "压制秒数", " 秒", 2, (x, side) =>
            x.Pressures.Where(p => p.Attacker == side && p.ConfirmedMoves >= minPressureMoves)
                .Sum(p => p.End - p.Start + 1) * ReplayTiming.SecondsPerFrame),
        new("防守与压制", "每段压制秒数", " 秒", 2, (x, side) => {
            var applied = x.Pressures.Where(p => p.Attacker == side &&
                p.ConfirmedMoves >= minPressureMoves).ToArray();
            return applied.Length == 0 ? 0 :
                applied.Sum(p => p.End - p.Start + 1) * ReplayTiming.SecondsPerFrame / applied.Length;
        }),
        new("防守与压制", "被压制", "", 0, (x, side) =>
            x.Pressures.Count(p => p.Defender == side && p.ConfirmedMoves >= minPressureMoves)),
        new("防守与压制", "压制被打断", "", 0, (x, side) =>
            PressureEventCount(x, side, "压制被打断")),
        new("防守与压制", "压制抓乱动", "", 0, (x, side) =>
            PressureEventCount(x, side, "压制抓乱动")),
        new("防守与压制", "压制抓 DD", "", 0, (x, side) =>
            PressureEventCount(x, side, "压制抓DD")),
        new("防守与压制", "逃压尝试", "", 0, (x, side) =>
            FilteredEscapeAttempts(x, side).Length),
        new("防守与压制", "逃压成功", "", 0, (x, side) =>
            FilteredEscapeAttempts(x, side).Count(a => a.Outcome == "成功")),
        new("防守与压制", "逃压失败", "", 0, (x, side) =>
            FilteredEscapeAttempts(x, side).Count(a => a.Outcome == "失败")),
        new("防守与压制", "错防", "", 0, (x, side) =>
            x.Total(side).GuardLevelKnown ? x.Total(side).WrongGuards : null),
        new("防守与压制", "蓄力择防对", "", 0, (x, side) =>
            x.Total(side).ChargedMixupsKnown ? x.Total(side).ChargedMixupsBlocked : null),
        new("资源与行动", "使用卡牌", "", 0, (x, side) => x.Total(side).CardsUsed),
        new("资源与行动", "低灵力", " 秒", 1, (x, side) => x.Total(side).LowSpiritFrames * ReplayTiming.SecondsPerFrame),
        new("资源与行动", "灵力耗尽", "", 0, (x, side) => x.Total(side).EmptySpirit),
        new("资源与行动", "破灵珠", "", 0, (x, side) =>
            x.Frames.Any(frame => frame.Side(side).MaxSpirit >= 0) ? x.Total(side).BrokenOrbs : null),
        new("资源与行动", "受身", "", 0, (x, side) => x.Total(side).Techs),
        new("资源与行动", "受身未自由行动被打", "", 0, (x, side) =>
            x.Total(side).TechTrapped),
        new("防守与压制", "被弹幕起手", "", 0, (x, side) =>
            x.Total(side).ProjectileOpenerKnown ? x.Total(side).TakenProjectileOpeners : null),
        new("防守与压制", "同向被攻击", "", 0, (x, side) =>
            x.Total(side).SameDirectionHits),
        new("防守与压制", "技能前摇被攻击", "", 0, (x, side) =>
            x.Total(side).PunishPhaseKnown ? x.Total(side).StartupPunished : null),
        new("防守与压制", "技能硬直被攻击", "", 0, (x, side) =>
            x.Total(side).PunishPhaseKnown ? x.Total(side).RecoveryPunished : null),
        new("防守与压制", "被凹", "", 0, (x, side) =>
            x.Total(side).PunishPhaseKnown ? x.Total(side).ReversalPunished : null),
        new("资源与行动", "出圈", "", 0, (x, side) => x.Total(side).LimitBreaks)
    ];
    private static UIElement OpeningBars((int Defender, string Move, int Count)[] values)
    {
        if (values.Length == 0) return Text("没有确认命中的连段。", 12, Muted);
        var panel = new StackPanel();
        var maximum = values.Max(x => x.Count);
        foreach (var item in values)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            var label = Text($"{(item.Defender == 0 ? "1P" : "2P")} 被攻击 · {item.Move}", 12);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, 0); row.Children.Add(label);
            var bar = new ProgressBar { Maximum = maximum, Value = item.Count,
                Height = 13, Margin = new Thickness(8, 0, 8, 0),
                Foreground = item.Defender == 0 ? Blue : Red, Background = Light,
                BorderThickness = new Thickness(0) };
            Grid.SetColumn(bar, 1); row.Children.Add(bar);
            var count = Text(item.Count.ToString(), 12, Muted);
            Grid.SetColumn(count, 2); row.Children.Add(count);
            panel.Children.Add(row);
        }
        return panel;
    }
    private void CombosPage()
    {
        if (session?.Frames.Count == 0 || session is null) { Empty("需要逐帧引擎轨迹，REP 输入本身不能确认连段。"); return; }
        var comboTools = new WrapPanel();
        comboTools.Children.Add(Button("连段显示冲刺", () => {
            comboShowDashes = !comboShowDashes; SaveSettings(); Render();
        }, comboShowDashes));
        var missedLegend = Text("   橙色行：连段末尾出招但未确认接上", 12,
            palette["Warning"]);
        missedLegend.VerticalAlignment = VerticalAlignment.Center;
        comboTools.Children.Add(missedLegend);
        content.Children.Add(Card("连段显示", comboTools));
        var rows = session.Combos.OrderBy(x => x.Start).ToArray();
        UIElement ComboOverview(int side)
        {
            var own = rows.Where(x => x.Side == side).ToArray();
            var finished = own.Count(x => x.Circle || x.Knockdown);
            return SideOverview(side,
                ("连段总数", own.Length.ToString(), "确认命中的连段"),
                ("连段成功率", own.Length == 0 ? "—" :
                    $"{finished * 100.0 / own.Length:0.0}%", "出圈或倒地 / 连段总数"),
                ("连段平均收益", own.Length == 0 ? "—" :
                    own.Average(x => x.Damage).ToString("N0"), "平均实际 HP 损失"),
                ("连段最高收益", own.Length == 0 ? "—" :
                    own.Max(x => x.Damage).ToString("N0"), "单段实际 HP 损失"));
        }
        content.Children.Add(OverviewColumns(ComboOverview(0), ComboOverview(1)));
        UIElement SideCombos(int side)
        {
            var own = rows.Where(x => x.Side == side).ToArray();
            var shown = new List<string[]>();
            var ordinals = new List<int>();
            foreach (var combo in own)
            {
                shown.Add([Time(combo.Start), combo.Position, combo.Hits.ToString(),
                    combo.Damage.ToString(), combo.Limit.ToString(),
                    ComboDisplayRoute(session, combo, side, comboShowDashes)]);
                ordinals.Add(combo.Start);
                var missed = ComboMissedActions(session, combo, side);
                if (missed.Length == 0 || combo.Limit >= 100) continue;
                shown.Add(["", "", "", "", "", "↳ 未接上：" + string.Join(" ", missed)]);
                ordinals.Add(combo.Start);
            }
            var table = Table(
                ["时间", "位置", "Hit", "伤害", "总 Lim", "路线"],
                shown);
            table.RowHeight = double.NaN;
            table.MinRowHeight = 28;
            table.AutoGeneratingColumn += (_, args) => {
                args.Column.MinWidth = args.PropertyName switch {
                    "时间" => 72, "位置" => 58, "Hit" => 36,
                    "伤害" => 55, "总 Lim" => 60, _ => 140
                };
                args.Column.Width = args.PropertyName switch {
                    "时间" => new DataGridLength(72),
                    "位置" => new DataGridLength(60),
                    "Hit" => new DataGridLength(38),
                    "伤害" => new DataGridLength(57),
                    "总 Lim" => new DataGridLength(60),
                    _ => new DataGridLength(1, DataGridLengthUnitType.Star)
                };
                if (args.PropertyName == "路线" && args.Column is DataGridTextColumn textColumn)
                {
                    var wrap = new Style(typeof(TextBlock));
                    wrap.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(7, 4, 7, 4)));
                    wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                    textColumn.ElementStyle = wrap;
                }
            };
            table.LoadingRow += (_, args) => {
                if (args.Row.Item is DataRowView row &&
                    row["路线"]?.ToString()?.StartsWith("↳ 未接上：", StringComparison.Ordinal) == true)
                    args.Row.Foreground = palette["Warning"];
            };
            BindTimeline(table, new Dictionary<string, IReadOnlyList<int>> {
                ["时间"] = ordinals
            }, Jump);
            return Card($"{side + 1}P 连段 · {own.Length} 条 · 点击时间定位", table);
        }
        content.Children.Add(Columns((SideCombos(0), 1), (SideCombos(1), 1)));
    }
    private static string ComboDisplayRoute(ReplaySession data, Combo combo, int side,
        bool showDashes)
    {
        if (!showDashes) return ActionCatalog.RouteCompact(combo.Route,
            data.Frames[Math.Clamp(combo.Start, 0, data.Frames.Count - 1)].Side(side).Character);
        var tokens = new List<(int Ordinal, int Priority, string Label)>();
        var previousHitAction = -1;
        foreach (var ordinal in combo.Impacts)
        {
            var fighter = data.Frames[Math.Clamp(ordinal, 0, data.Frames.Count - 1)].Side(side);
            if (fighter.Action == previousHitAction) continue;
            tokens.Add((ordinal, 1, ActionCatalog.Display(fighter.Action, fighter.Character)));
            previousHitAction = fighter.Action;
        }
        for (var ordinal = Math.Max(0, combo.Start); ordinal < Math.Min(combo.End, data.Frames.Count); ordinal++)
        {
            var fighter = data.Frames[ordinal].Side(side);
            if (fighter.Action is not (200 or 201 or 202 or 203 or 206 or 207 or 218)) continue;
            if (ordinal > combo.Start && data.Frames[ordinal - 1].Side(side).Action == fighter.Action) continue;
            tokens.Add((ordinal, 0, ActionCatalog.Display(fighter.Action, fighter.Character)));
        }
        return tokens.Count == 0 ? ActionCatalog.RouteCompact(combo.Route,
            data.Frames[Math.Clamp(combo.Start, 0, data.Frames.Count - 1)].Side(side).Character)
            : string.Join(" ", tokens.OrderBy(x => x.Ordinal).ThenBy(x => x.Priority)
                .Select(x => x.Label));
    }
    private static string[] ComboMissedActions(ReplaySession data, Combo combo, int side)
    {
        if (combo.Impacts.Count == 0 || combo.End <= combo.Impacts[^1] + 1) return [];
        var missed = new List<string>();
        for (var ordinal = combo.Impacts[^1] + 1;
             ordinal < Math.Min(combo.End, data.Frames.Count); ordinal++)
        {
            var fighter = data.Frames[ordinal].Side(side);
            var action = fighter.Action;
            if (action is not (>= 300 and <= 432 or >= 500 and <= 593 or >= 600 and <= 669))
                continue;
            if (ordinal > 0 && data.Frames[ordinal - 1].Side(side).Action == action) continue;
            var label = ActionCatalog.Display(action, fighter.Character);
            if (label.StartsWith("动作 ", StringComparison.Ordinal) ||
                label.StartsWith("技能动作 ", StringComparison.Ordinal)) continue;
            missed.Add(label);
        }
        return missed.ToArray();
    }
    private void PressurePage()
    {
        if (session?.Frames.Count == 0 || session is null) { Empty("需要逐帧防御接触才能识别压制段。"); return; }
        var selector = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var n in new[] {1,2,3})
        {
            var count = n;
            var threshold = Button($"至少 {n} 招", () => {
                if (escapeBusy) return;
                if (minPressureMoves != count) escapeResults.Clear();
                minPressureMoves = count;
                SaveSettings();
                Render();
            }, n == minPressureMoves);
            threshold.IsEnabled = !escapeBusy;
            selector.Children.Add(threshold);
        }
        content.Children.Add(Card("最少连续招式", selector));
        var selected = PressureSelection.Select(session, minPressureMoves);
        UIElement PressureOverview(int side)
        {
            var own = selected.Where(x => x.Attacker == side).ToArray();
            var punishKnown = session.Total(side).PunishPhaseKnown;
            var punished = punishKnown ? own.Count(pressure =>
                pressure.EndReason == "被反击" && session.Events.Any(e =>
                    e.Side == side && e.Round == pressure.Round &&
                    e.Kind == "技能硬直被攻击" &&
                    e.Ordinal >= pressure.Start && e.Ordinal <= pressure.End)) : 0;
            return SideOverview(side,
                ("压制总数", own.Length.ToString(), $"至少 {minPressureMoves} 招"),
                ("压制被确反次数", punishKnown ? punished.ToString() : "数据不足",
                    "压制中技能硬直被确认命中"),
                ("压制平均持续时长", own.Length == 0 ? "—" :
                    $"{own.Average(x => Math.Max(0, x.End - x.Start)) * ReplayTiming.SecondsPerFrame:0.00}s",
                    "压制起点至结束"),
                ("压制最高持续时长", own.Length == 0 ? "—" :
                    $"{own.Max(x => Math.Max(0, x.End - x.Start)) * ReplayTiming.SecondsPerFrame:0.00}s",
                    "单段最长"));
        }
        content.Children.Add(OverviewColumns(PressureOverview(0), PressureOverview(1)));
        UIElement SidePressures(int side)
        {
            var own = selected.Where(x => x.Attacker == side).OrderBy(x => x.Start).ToArray();
            var shown = new List<string[]>();
            var ordinals = new List<int>();
            foreach (var pressure in own)
            {
                var character = session.Frames[Math.Clamp(pressure.Start, 0,
                    session.Frames.Count - 1)].Side(side).Character;
                const string marker = "↳ 未接上：";
                var markerIndex = pressure.Route.IndexOf(marker, StringComparison.Ordinal);
                var mainRoute = markerIndex < 0 ? pressure.Route :
                    pressure.Route[..markerIndex].TrimEnd();
                shown.Add([Time(pressure.Start), pressure.EndReason,
                    pressure.Contacts.ToString(), ActionCatalog.RouteCompact(mainRoute, character)]);
                ordinals.Add(pressure.Start);
                if (markerIndex < 0) continue;
                var missed = pressure.Route[(markerIndex + marker.Length)..].Trim();
                if (int.TryParse(missed, out var action))
                    missed = ActionCatalog.Display(action, character);
                shown.Add(["", "", "", marker + missed]);
                ordinals.Add(pressure.Start);
            }
            var pressureTable = Table(
                ["时间", "结束原因", "接触点", "招式清单"],
                shown);
            pressureTable.RowHeight = double.NaN;
            pressureTable.MinRowHeight = 28;
            pressureTable.AutoGeneratingColumn += (_, args) => {
                args.Column.MinWidth = args.PropertyName switch {
                    "时间" => 72, "结束原因" => 72,
                    "接触点" => 58, _ => 140
                };
                args.Column.Width = args.PropertyName switch {
                    "时间" => new DataGridLength(72),
                    "结束原因" => new DataGridLength(88),
                    "接触点" => new DataGridLength(64),
                    _ => new DataGridLength(1, DataGridLengthUnitType.Star)
                };
                if (args.PropertyName == "招式清单" && args.Column is DataGridTextColumn textColumn)
                {
                    var wrap = new Style(typeof(TextBlock));
                    wrap.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(7, 4, 7, 4)));
                    wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                    textColumn.ElementStyle = wrap;
                }
            };
            pressureTable.LoadingRow += (_, args) => {
                if (args.Row.Item is DataRowView row &&
                    row["招式清单"]?.ToString()?.StartsWith("↳ 未接上：", StringComparison.Ordinal) == true)
                    args.Row.Foreground = palette["Warning"];
            };
            BindTimeline(pressureTable, new Dictionary<string, IReadOnlyList<int>> {
                ["时间"] = ordinals
            }, Jump);
            var gaps = session.PressureGaps.Where(x => x.Defender == 1 - side &&
                own.Any(p => p.Round == x.Round && p.Defender == x.Defender &&
                    p.Start <= x.Start && x.NextContact <= p.End)).ToArray();
            var gapTable = Table(["可行动起点", "下次接触", "空隙帧", "判断来源", "4A 生效帧"],
                gaps.Select(x => new[] { Time(x.Start), Time(x.NextContact),
                    x.Length == 0 && x.MashableZero ? "0（末帧估计）" : x.Length.ToString(),
                    x.Source, PressureGapDetector.FourAStartup(
                        session.Frames[x.Start].Side(x.Defender).Character).ToString() }));
            BindTimeline(gapTable, new Dictionary<string, IReadOnlyList<int>> {
                ["可行动起点"] = gaps.Select(x => x.Start).ToArray(),
                ["下次接触"] = gaps.Select(x => x.NextContact).ToArray()
            }, Jump);
            var gapTitle = Text($"{2 - side}P 防守空隙 · {gaps.Length} 处", 12, Muted, true);
            gapTitle.Margin = new Thickness(0, 18, 0, 9);
            return Card($"{side + 1}P 发起压制 · {own.Length} 段 · 点击时间定位",
                Vertical(pressureTable, gapTitle, gapTable));
        }
        content.Children.Add(Columns((SidePressures(0), 1), (SidePressures(1), 1)));
    }
    public async Task<(int Completed, int Total, int Success, int Failures,
        int Unresolved, int ExecutionErrors, string Message,
        string[] Verdicts)> SmokeEscapeBatchAsync(int count, int skip = 0, string move = "4A")
    {
        if (session is null) throw new InvalidOperationException("未导入原回放。");
        escapeMove = move;
        escapeDefender = -1;
        var pressures = PressureSelection.Select(session, minPressureMoves)
            .Skip(skip).Take(count).ToArray();
        if (pressures.Length != count) throw new InvalidOperationException("符合压制总表设置的压制不足。");
        await RunPressureEscapeAsync(pressures.Select(p => new PressureEscapeJob(p, move)).ToArray());
        var recent = escapeBatchDone == 0 ? [] : escapeResults.TakeLast(escapeBatchDone).ToArray();
        return (escapeBatchDone, recent.Length,
            recent.Count(x => x.Verdict.Result == "成功"),
            recent.Count(x => x.Verdict.Result is "未找到" or "受击" or "仍受压" or "未发动"),
            recent.Count(x => x.Verdict.Result is "未判定" or "不可执行" or "已取消"),
            recent.Count(x => x.Verdict.Result == "执行错误"),
            message ?? "", recent.Select(x => $"{x.Anchor}:{x.Defender}:{x.Move}:" +
                $"{x.Verdict.Result}:{x.Verdict.Reason}:{x.MatchedInputFrames}").ToArray());
    }

    private async Task OpenEscapeResultAsync(EscapeTrialResult result, int? ordinal = null, bool play = false)
    {
        if (PlaybackOperationBlocked || ReplayControlInFlight) return;
        if (probe is null || !File.Exists(result.ReplayPath))
        {
            message = "该结果的分支 REP 不存在，或游戏探针未连接。";
            Render(); return;
        }
        escapeBusy = true;
        PausePlayback();
        ClearNativeSnapshots();
        Render();
        await playbackControlGate.WaitAsync();
        try
        {
            if (shutdownInProgress || shutdownReady) return;
            var header = ReplayHeader.Read(result.ReplayPath);
            var hash = header.Hash;
            var trace = System.IO.Path.Combine(root, "trace_" + hash + ".csv");
            nativeReplaySource = null;
            nativePlaybackReleased = true;
            await probe.SendAsync(ProbeCommand.Load, replayPath: result.ReplayPath,
                tracePath: trace);
            probeStatus = await probe.SendAsync(ProbeCommand.Seek, target: result.End);
            session = await Task.Run(() => SessionLoader.Load(result.ReplayPath, root, trace,
                preloadedHeader: header));
            nativeReplaySource = session.Source;
            nativePlaybackReleased = false;
            cursor = Math.Clamp(ordinal ?? result.Anchor, 0, result.End);
            probeStatus = await probe.SendAsync(ProbeCommand.Seek, target: cursor);
            probeStatus = await probe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
            if (play) probeStatus = await probe.SendAsync(ProbeCommand.Play);
            AdoptPlaybackPosition(probeStatus);
            branchOriginal = result.Original;
            branchAnchor = result.Anchor;
            selectedEscapeResult = result;
            page = 0;
            message = $"已载入 {Time(result.Anchor)} 的 {result.Move} 分支。";
        }
        catch (Exception ex)
        {
            try
            {
                probeStatus = await ReplayBranchRunner.RestoreAsync(probe,
                    result.Original, root, result.Anchor);
                session = result.Original;
                nativeReplaySource = session.Source;
                nativePlaybackReleased = false;
                probeStatus = await probe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
                AdoptPlaybackPosition(probeStatus);
                branchOriginal = null;
                message = $"载入推演分支失败，已返回原回放：{ex.Message}";
            }
            catch (Exception recovery)
            {
                nativeReplaySource = null;
                nativePlaybackReleased = true;
                message = $"载入推演分支失败，返回原回放也失败：{ex.Message}；{recovery.Message}";
            }
        }
        finally { escapeBusy = false; playbackControlGate.Release(); Render(); }
    }

    private Task PlayEscapeResultAsync(EscapeTrialResult result)
        => OpenEscapeResultAsync(result, play: true);

    internal async Task<object> SmokeEscapeResultPlaybackAsync()
    {
        var result = escapeResults.LastOrDefault(r => r.Verdict.Result == "成功") ??
            escapeResults.LastOrDefault(r => r.MatchedInputFrames == r.End - r.Anchor && File.Exists(r.ReplayPath)) ??
            throw new InvalidDataException("没有可核验的推演结果。");
        var count = escapeResults.Count;
        StoreEscapeResult(result);
        if (escapeResults.Count != count) throw new InvalidDataException("重复结果未去重。");
        page = 6;
        selectedEscapeResult = result;
        await PlayEscapeResultAsync(result);
        if (probe is null || session?.Source != result.ReplayPath ||
            branchOriginal?.Source != result.Original.Source || probeStatus?.Status != 2 ||
            session.Header?.Hash == result.Original.Header?.Hash)
            throw new InvalidDataException("逃压播放未使用技能分支。");
        await probe.SendAsync(ProbeCommand.Pause);
        probeStatus = await probe.SendAsync(ProbeCommand.Seek, target: result.End);
        var trace = System.IO.Path.Combine(root, "trace_" + session.Header!.Hash + ".csv");
        var actual = TraceReader.Read(trace);
        var expected = ReplayInputAligner.ReadInputWindow(result.ReplayPath,
            result.Original.Frames, result.Anchor, result.Defender, result.End - result.Anchor);
        var matched = Enumerable.Range(0, expected.Length).Count(index =>
            actual[result.Anchor + 1 + index].Side(result.Defender).Input == expected[index]);
        if (matched != expected.Length) throw new InvalidDataException("播放分支未应用预期逃压输入。");
        var simulatedTrace = System.IO.Path.ChangeExtension(result.ReplayPath, ".csv");
        var stateMismatches = 0;
        if (File.Exists(simulatedTrace))
        {
            var simulated = TraceReader.ReadTailWithPrefix(simulatedTrace,
                result.Original.Frames, result.Anchor);
            for (var i = result.Anchor; i <= result.End; i++)
                if (!ReplayBranchRunner.SameState(simulated[i], actual[i])) stateMismatches++;
            if (stateMismatches > 0)
                throw new InvalidDataException($"技能 REP 播放与推演状态不一致：{stateMismatches} 帧。");
        }
        return new { ResultCount = count, PlaybackSource = session.Source,
            OriginalSource = result.Original.Source, result.Move, result.Anchor,
            AppliedInputFrames = matched, StateMismatches = stateMismatches, SourceIsBranch = true };
    }
    private void InterpretPage()
    {
        if (session?.Frames.Count == 0 || session is null) { Empty("需要实际统计结果才能生成可验证提示。"); return; }
        var filter = new WrapPanel();
        foreach (var (caption, selectedSide) in new[] { ("全部", -1), ("1P", 0), ("2P", 1) })
            filter.Children.Add(Button(caption, () => {
                interpretSide = selectedSide;
                SaveSettings();
                Render();
            }, interpretSide == selectedSide));
        content.Children.Add(Card("解读角色", filter));
        if (!ReferenceEquals(interpretedSession, session) ||
            interpretedPressureMoves != minPressureMoves || interpretedTips is null)
        {
            interpretedTips = ReplayInterpretation.Evaluate(session, minPressureMoves);
            interpretedSession = session;
            interpretedPressureMoves = minPressureMoves;
        }
        var tips = interpretedTips.Where(x => interpretSide == -1 || x.Player == "本局" ||
            x.Player == $"{interpretSide + 1}P");
        var interpretation = Table(
            ["角色", "类别", "观察", "依据"],
            tips.Select(x => new[] { x.Player, x.Category, x.Observation, x.Evidence }));
        interpretation.RowHeight = double.NaN;
        interpretation.MinRowHeight = 34;
        interpretation.MinWidth = 720;
        double RemainingWidth()
        {
            var viewport = pageScroller?.ActualWidth is > 0
                ? pageScroller.ActualWidth : Math.Max(1000, Width - 214);
            return Math.Max(400, viewport - 100 - 70 - 122);
        }
        void FitColumns()
        {
            if (interpretation.Columns.Count != 4) return;
            var remaining = RemainingWidth();
            interpretation.Columns[2].Width = new DataGridLength(remaining * .55);
            interpretation.Columns[3].Width = new DataGridLength(remaining * .45);
        }
        SizeChangedEventHandler resize = (_, _) => FitColumns();
        var resizeAttached = false;
        interpretation.Loaded += (_, _) => {
            FitColumns();
            if (resizeAttached) return;
            SizeChanged += resize;
            resizeAttached = true;
        };
        interpretation.AutoGeneratedColumns += (_, _) => FitColumns();
        interpretation.Unloaded += (_, _) => {
            if (!resizeAttached) return;
            SizeChanged -= resize;
            resizeAttached = false;
        };
        interpretation.AutoGeneratingColumn += (_, args) =>
        {
            args.Column.Width = args.PropertyName switch {
                "角色" => new DataGridLength(70),
                "类别" => new DataGridLength(122),
                "观察" => new DataGridLength(RemainingWidth() * .55),
                _ => new DataGridLength(RemainingWidth() * .45)
            };
            args.Column.MinWidth = args.PropertyName is "角色" or "类别" ? 70 : 180;
            if (args.PropertyName is "观察" or "依据") args.Column.MaxWidth = double.PositiveInfinity;
            if (args.Column is DataGridTextColumn column)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.PaddingProperty,
                    new Thickness(8, 5, 8, 5)));
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                column.ElementStyle = style;
            }
        };
        content.Children.Add(Card("本局解读 · 按当前压制招数门槛重算", interpretation));
    }
    private void EventsPage()
    {
        if (session?.Frames.Count == 0 || session is null)
        {
            Empty("载入逐帧引擎轨迹后显示判定事件。");
            return;
        }
        var filter = new WrapPanel();
        foreach (var (caption, side) in new[] { ("全部角色", -1), ("1P", 0), ("2P", 1) })
            filter.Children.Add(Button(caption, () => { auditSide = side; SaveSettings(); Render(); }, auditSide == side));
        foreach (var kind in new[] { "全部" }.Concat(session.Events.Select(x => x.Kind)
            .Distinct().OrderBy(x => x)))
        {
            var selectedKind = kind;
            filter.Children.Add(Button(kind, () => { auditKind = selectedKind; SaveSettings(); Render(); }, auditKind == kind));
        }
        filter.Children.Add(Button("导出筛选结果", ExportEvents));
        content.Children.Add(Card("筛选与导出", filter));
        var filtered = FilteredEvents().ToArray();
        var eventTable = Table(
            ["时间", "回合", "角色", "事件", "细节", "依据"],
            filtered.Select(x => new[] { Time(x.Ordinal),
                (x.Round+1).ToString(), x.Side switch { 0 => "1P", 1 => "2P", _ => "全局" },
                x.Kind, x.Detail, x.Reason }));
        BindTimeline(eventTable, new Dictionary<string, IReadOnlyList<int>> {
            ["时间"] = filtered.Select(x => x.Ordinal).ToArray()
        }, Jump);
        content.Children.Add(Card($"事件时间线 · {filtered.Length:N0} 条 · 点击时间定位",
            eventTable));
    }
    private IEnumerable<ReplayEvent> FilteredEvents()
    {
        if (session is null) return [];
        var eligible = session.Pressures.Where(p =>
            p.ConfirmedMoves >= minPressureMoves)
            .Select(p => (p.Round, p.Defender, p.Start)).ToHashSet();
        var attempts = session.EscapeAttempts.Where(a =>
            eligible.Contains((a.Round, a.Defender, a.PressureStart)))
            .Select(a => (a.Round, a.Defender, a.Start)).ToHashSet();
        return session.Events.Where(x =>
            (auditSide == -1 || x.Side == auditSide) &&
            (auditKind == "全部" || x.Kind == auditKind) &&
            (!x.Kind.StartsWith("逃压", StringComparison.Ordinal) ||
             attempts.Contains((x.Round, x.Side, x.Ordinal))));
    }
    private void ExportEvents()
    {
        if (session is null) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "判定明细.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            EventCsvExporter.Export(dialog.FileName, FilteredEvents());
            message = $"已导出筛选后的判定明细：{dialog.FileName}";
        }
        catch (Exception ex) { message = $"导出失败：{ex.Message}"; }
        Render();
    }
    private void ExportStats()
    {
        if (session is null) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "对局统计.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var count = StatsCsvExporter.Export(dialog.FileName, session, statsSide, statsRound,
                minPressureMoves);
            message = $"已导出 {count} 行对局统计：{dialog.FileName}";
        }
        catch (Exception ex) { message = $"统计导出失败：{ex.Message}"; }
        Render();
    }
    private void ExportMultiStats()
    {
        IReadOnlyList<ReplaySession> exports = samples.Count > 0 ? samples :
            session is null ? [] : [session];
        if (exports.Count == 0) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "多局统计.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var count = StatsCsvExporter.ExportMany(dialog.FileName, exports, minPressureMoves:
                minPressureMoves);
            message = $"已导出 {exports.Count} 份 REP、{count} 行统计：{dialog.FileName}";
        }
        catch (Exception ex) { message = $"多局统计导出失败：{ex.Message}"; }
        Render();
    }
    private static readonly (string Id, string Title, Size Minimum)[] ReplayPanelOptions = [
        ("game", "对局画面与当前状态", new(420, 360)),
        ("playback", "压制缝提示", new(300, 220)),
        ("snapshot", "固定快照", new(300, 180)),
        ("quick", "快速定位", new(300, 180)),
        ("bookmarks", "时间标记", new(300, 160))
    ];
    private ReplayDockLayout EditableReplayLayout()
    {
        var slots = ReplayPanelOptions.Select((p, i) => new ReplayDockSlot("home-" + p.Id,
            i == 0 ? new(0, 0, .66, 1) : new ReplayPanelLayout(.67, (i - 1) * .25, .33, .25),
            [p.Id], p.Id)).ToArray();
        var fallback = new ReplayDockState(1, 1100, 1000, slots,
            slots.ToDictionary(s => s.Active!, s => s.Id));
        var model = new ReplayDockLayout(fallback, ReplayPanelOptions.ToDictionary(p => p.Id, p => p.Minimum));
        if (replayDockDefaultState is not null) model.Restore(replayDockDefaultState);
        if (replayDockState is not null) model.Restore(replayDockState);
        return model;
    }
    private bool SetReplayPanelEnabled(string id, bool enabled)
    {
        if (ReplayControlInFlight || PlaybackOperationBlocked) return false;
        if (replayWorkspace is not null)
        {
            var open = replayWorkspace.Layout.State.Slots.Any(s => s.Panels.Contains(id));
            return open == enabled || (enabled ? replayWorkspace.OpenPanel(id) : replayWorkspace.ClosePanel(id));
        }
        var model = EditableReplayLayout();
        var visible = model.State.Slots.Any(s => s.Panels.Contains(id));
        if (visible == enabled) return true;
        if (!(enabled ? model.Open(id) : model.Close(id))) return false;
        replayDockState = ReplayDockLayout.Copy(model.State);
        SaveSettings();
        return true;
    }
    private void ReplayPanelSettingsPage()
    {
        var state = replayWorkspace?.Layout.State ?? EditableReplayLayout().State;
        var options = new StackPanel();
        var feedback = Text("", 12, Muted);
        foreach (var panel in ReplayPanelOptions)
        {
            var check = new CheckBox { Content = panel.Title,
                IsChecked = state.Slots.Any(s => s.Panels.Contains(panel.Id)),
                Margin = new Thickness(0, 0, 0, 14), FontSize = 13 };
            var updating = false;
            void ChangeVisibility()
            {
                if (updating) return;
                if (SetReplayPanelEnabled(panel.Id, check.IsChecked == true))
                { feedback.Text = "已保存，返回回放页即可使用。"; return; }
                updating = true; check.IsChecked = check.IsChecked != true; updating = false;
                feedback.Text = "当前回放任务执行中，完成后可调整面板。";
            }
            check.Checked += (_, _) => ChangeVisibility();
            check.Unchecked += (_, _) => ChangeVisibility();
            options.Children.Add(check);
        }
        content.Children.Add(Card("回放面板", Vertical(
            Text("勾选开启面板；回放页点击 × 关闭。拖动面板标题调整位置，拖动分隔线调整大小。", 12, Muted),
            options, feedback)));
    }
    private void SettingsPage()
    {
        var sections = new WrapPanel();
        foreach (var name in new[] { "基础修改", "皮肤", "快捷键", "回放面板", "功能启用", "大小" })
        {
            var selected = name;
            sections.Children.Add(Button(name, () => {
                settingsSection = selected;
                SaveSettings();
                Render();
            }, settingsSection == name));
        }
        content.Children.Add(Card("设置分项", sections));
        if (settingsSection == "基础修改")
        {
            var mode = new StackPanel { Orientation = Orientation.Horizontal };
            mode.Children.Add(Button("默认模式", () => { expert = false; SaveSettings(); Render(); }, !expert));
            mode.Children.Add(Button("专家模式", () => { expert = true; SaveSettings(); Render(); }, expert));
            content.Children.Add(Card("工作模式", mode));
            var connect = new CheckBox {
                Content = "启动游戏后自动连接", IsChecked = autoConnectOnLaunch,
                Width = 220, Margin = new Thickness(0, 0, 8, 14), FontSize = 13,
                ToolTip = "点击启动游戏后，等待主窗口就绪并自动连接探针"
            };
            connect.Checked += (_, _) => { autoConnectOnLaunch = true; SaveSettings(); };
            connect.Unchecked += (_, _) => { autoConnectOnLaunch = false; SaveSettings(); };
            content.Children.Add(Card("游戏连接", connect));
            content.Children.Add(Card("当前配置", Text(
                $"{(expert ? "专家" : "默认")}模式 · {skin}皮肤 · 已启用页面 {enabled.Count(x => x)} / 10 · " +
                $"压制缝门槛 {(gapThreshold < 0 ? "自动" : $"{gapThreshold} 帧")}。更改会自动保存。",
                12, Muted)));
        }
        else if (settingsSection == "皮肤") SkinSettingsPage();
        else if (settingsSection == "快捷键") ShortcutSettingsPage();
        else if (settingsSection == "回放面板") ReplayPanelSettingsPage();
        else if (settingsSection == "功能启用")
        {
            var toggles = new WrapPanel();
            for (var i = 1; i < 9; i++)
            {
                var index = i;
                var check = new CheckBox { Content = Pages[i], IsChecked = enabled[i],
                    Width = 180, Margin = new Thickness(0, 0, 8, 14), FontSize = 13 };
                check.Checked += (_, _) => { enabled[index] = true; SaveSettings(); BuildNavigation(); };
                check.Unchecked += (_, _) => {
                    enabled[index] = false;
                    SaveSettings();
                    BuildNavigation();
                };
                toggles.Children.Add(check);
            }
            content.Children.Add(Card("分析页面", toggles));
            var options = new WrapPanel();
            var replayToggle = new CheckBox { Content = "回放工具栏", IsChecked = showReplayTools,
                Width = 180, Margin = new Thickness(0, 0, 8, 14), FontSize = 13 };
            replayToggle.Checked += (_, _) => { showReplayTools = true; SaveSettings(); };
            replayToggle.Unchecked += (_, _) => { showReplayTools = false; SaveSettings(); };
            options.Children.Add(replayToggle);
            var chartNames = new[] { "血量曲线", "灵力曲线", "卡槽曲线", "天气曲线", "状态曲线" };
            for (var i = 0; i < chartNames.Length; i++)
            {
                var index = i;
                var chart = new CheckBox { Content = chartNames[i], IsChecked = chartEnabled[i],
                    Width = 180, Margin = new Thickness(0, 0, 8, 14), FontSize = 13 };
                chart.Checked += (_, _) => { chartEnabled[index] = true; SaveSettings(); };
                chart.Unchecked += (_, _) => { chartEnabled[index] = false; SaveSettings(); };
                options.Children.Add(chart);
            }
            content.Children.Add(Card("回放工具与图表", options));
        }
        else
        {
            var cardSizeLabel = Text($"卡牌宽 {curveCardWidth} · 高 {CurveCardHeight(curveCardWidth)} 像素", 12, Muted);
            var cardSize = new Slider {
                Minimum = 24, Maximum = 64, Value = curveCardWidth,
                TickFrequency = 2, IsSnapToTickEnabled = true,
                Width = 340, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 9, 0, 9)
            };
            cardSize.ValueChanged += (_, _) => {
                var selected = (int)Math.Round(cardSize.Value / 2) * 2;
                if (selected == curveCardWidth) return;
                curveCardWidth = selected;
                cardSizeLabel.Text = $"卡牌宽 {curveCardWidth} · 高 {CurveCardHeight(curveCardWidth)} 像素";
                SaveSettings();
            };
            content.Children.Add(Card("卡槽曲线卡牌尺寸",
                Vertical(cardSizeLabel, cardSize,
                    Text("返回曲线页即可看到调整后的卡牌；排列间距会随尺寸变化。", 12, Muted))));
        }
    }
    private void ApplySkin(string name, bool persist = true)
    {
        if (!AppTheme.IsPreset(name)) return;
        skin = name;
        palette.Apply(skin, customColors.GetValueOrDefault(skin));
        pageCache.Clear();
        UpdateWindowChrome(this);
        if (persist) SaveSettings();
        Render();
    }
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(SettingsPath));
            if (saved is null) return;
            expert = saved.Expert;
            if (Enum.TryParse<Key>(saved.ExitTakeoverKey, out var exitKey) && IsExitTakeoverKey(exitKey))
                exitTakeoverKey = exitKey;
            if (AppTheme.IsPreset(saved.Skin)) skin = saved.Skin;
            if (saved.CustomColors is not null)
                foreach (var (preset, overrides) in saved.CustomColors)
                    if (AppTheme.IsPreset(preset) && overrides is not null)
                        customColors[preset] = overrides.Where(x =>
                            AppTheme.Tokens.Any(t => t.Key == x.Key) && AppTheme.TryColor(x.Value, out _))
                            .ToDictionary(x => x.Key, x => x.Value.ToUpperInvariant());
            palette.Apply(skin, customColors.GetValueOrDefault(skin));
            if (saved.Enabled is { Length: 10 })
                Array.Copy(saved.Enabled, enabled, 10);
            if (saved.Charts is { Length: 5 })
                Array.Copy(saved.Charts, chartEnabled, 5);
            if (saved.CurveCardWidth is >= 24 and <= 64)
                curveCardWidth = saved.CurveCardWidth.Value;
            if (saved.PlaybackSpeed is 0.5 or 0.75 or 1 or 1.5 or 2)
                playbackSpeed = saved.PlaybackSpeed.Value;
            if (saved.StatsSide is >= -1 and <= 1) statsSide = saved.StatsSide.Value;
            if (saved.StatsRound is >= -1 and <= 100) statsRound = saved.StatsRound.Value;
            if (saved.StatsCategory is "全表" or "时间、资源与输入" or "命中与伤害" or
                "攻防判定" or "防御" or "主动压制" or "受身与逃压" or "移动与招式" or "招式使用")
                statsCategory = saved.StatsCategory;
            if (saved.MultiCategory is "全表" or "时间与输入" or "伤害与连段" or
                "防守与压制" or "资源与行动") multiCategory = saved.MultiCategory;
            if (saved.ComboShowDashes.HasValue) comboShowDashes = saved.ComboShowDashes.Value;
            if (saved.MinPressureMoves is >= 1 and <= 3) minPressureMoves = saved.MinPressureMoves.Value;
            if (saved.EscapeDefender is >= -1 and <= 1) escapeDefender = saved.EscapeDefender.Value;
            if (saved.EscapeOnlySuccess.HasValue) escapeOnlySuccess = saved.EscapeOnlySuccess.Value;
            if (!string.IsNullOrWhiteSpace(saved.EscapeMove) && saved.EscapeMove.Length <= 32)
                escapeMove = saved.EscapeMove;
            if (saved.InterpretSide is >= -1 and <= 1) interpretSide = saved.InterpretSide.Value;
            if (saved.AuditSide is >= -1 and <= 1) auditSide = saved.AuditSide.Value;
            if (!string.IsNullOrWhiteSpace(saved.AuditKind) && saved.AuditKind.Length <= 64)
                auditKind = saved.AuditKind;
            if (saved.SettingsSection is "基础修改" or "皮肤" or "快捷键" or "回放面板" or "功能启用" or "大小")
                settingsSection = saved.SettingsSection;
            showReplayTools = saved.ShowReplayTools;
            replayDockState = saved.ReplayDock;
            replayDockDefaultState = saved.ReplayDockDefault;
            if (saved.ReplayWorkspaces is not null)
                foreach (var (name, workspace) in saved.ReplayWorkspaces.Take(30))
                    if (workspace is not null && name.Length is > 0 and <= 60)
                        replayDockWorkspaces[name] = workspace;
            autoConnectOnLaunch = saved.AutoConnectOnLaunch ?? true;
            if (saved.ReplayPanels is not null)
                foreach (var (id, layout) in saved.ReplayPanels)
                    if (id is "game" or "playback" or "snapshot" or "quick" or "bookmarks" &&
                        layout is not null &&
                        double.IsFinite(layout.X) && double.IsFinite(layout.Y) &&
                        double.IsFinite(layout.Width) && double.IsFinite(layout.Height) &&
                        layout.X is >= 0 and <= 3000 && layout.Y is >= 0 and <= 2400 &&
                        layout.Width is >= 250 and <= 2000 && layout.Height is >= 150 and <= 1600)
                        replayPanelLayouts[id] = layout;
            if (Directory.Exists(saved.GameDirectory)) gameDirectory = saved.GameDirectory;
            if (Directory.Exists(saved.ReplayDirectory)) replayDirectory = saved.ReplayDirectory;
            if (new[] { -1, 0, 4, 6, 8, 10 }.Contains(saved.GapThreshold))
                gapThreshold = saved.GapThreshold;
            if (saved.Bookmarks is not null)
                foreach (var (source, positions) in saved.Bookmarks.Take(100))
                    if (System.IO.Path.IsPathFullyQualified(source) && positions is not null)
                        bookmarks[source] = new SortedSet<int>(positions.Where(x => x >= 0).Take(12));
            if (saved.EscapeMoves is not null)
            {
                enabledEscapeMoves.Clear();
                foreach (var move in saved.EscapeMoves.Take(100))
                    if (!string.IsNullOrWhiteSpace(move) && move.Length <= 32)
                        enabledEscapeMoves.Add(move);
            }
            if (saved.AddedEscapeMoves is not null)
                foreach (var move in saved.AddedEscapeMoves.Take(100))
                    if (!string.IsNullOrWhiteSpace(move) && move.Length <= 32)
                        addedEscapeMoves.Add(move);
        }
        catch { /* Invalid settings use defaults. */ }
    }
    private void InvalidateSettingsViews()
    {
        // Global display settings affect every page. Page-local filters and
        // playback/bookmark persistence must not discard unrelated views.
        if (page == 9) pageCache.Clear();
        else pageCache.Remove(page);
    }
    private void SaveSettings()
    {
        InvalidateSettingsViews();
        try
        {
            var stagedSettings = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
            File.WriteAllText(stagedSettings,
                JsonSerializer.Serialize(new SavedSettings(expert, enabled, chartEnabled,
                    showReplayTools, gapThreshold,
                    bookmarks.ToDictionary(x => x.Key, x => x.Value.ToArray()), skin,
                    enabledEscapeMoves.Order().ToArray(), addedEscapeMoves.Order().ToArray(),
                    gameDirectory, replayDirectory, autoConnectOnLaunch,
                    replayPanelLayouts, curveCardWidth, playbackSpeed, statsSide,
                    statsRound, statsCategory, multiCategory, comboShowDashes,
                    minPressureMoves, escapeDefender, escapeOnlySuccess, escapeMove,
                    interpretSide, auditSide, auditKind, settingsSection, customColors, exitTakeoverKey.ToString(),
                    replayDockState, replayDockWorkspaces, replayDockDefaultState)));
            File.Move(stagedSettings, SettingsPath, overwrite: true);
            }
            finally { if (File.Exists(stagedSettings)) File.Delete(stagedSettings); }
        }
        catch (Exception ex) { message = $"设置保存失败：{ex.Message}"; }
    }
    private sealed record SavedSettings(bool Expert, bool[] Enabled,
        bool[]? Charts = null, bool ShowReplayTools = true,
        int GapThreshold = -1,
        Dictionary<string, int[]>? Bookmarks = null, string Skin = "经典",
        string[]? EscapeMoves = null, string[]? AddedEscapeMoves = null,
        string? GameDirectory = null, string? ReplayDirectory = null,
        bool? AutoConnectOnLaunch = null,
        Dictionary<string, ReplayPanelLayout>? ReplayPanels = null,
        int? CurveCardWidth = null, double? PlaybackSpeed = null,
        int? StatsSide = null, int? StatsRound = null, string? StatsCategory = null,
        string? MultiCategory = null, bool? ComboShowDashes = null,
        int? MinPressureMoves = null, int? EscapeDefender = null,
        bool? EscapeOnlySuccess = null, string? EscapeMove = null,
        int? InterpretSide = null, int? AuditSide = null, string? AuditKind = null,
        string? SettingsSection = null,
        Dictionary<string, Dictionary<string, string>>? CustomColors = null,
        string? ExitTakeoverKey = null,
        ReplayDockState? ReplayDock = null,
        Dictionary<string, ReplayDockState>? ReplayWorkspaces = null,
        ReplayDockState? ReplayDockDefault = null);
    private async void Import()
    {
        var dialog = new OpenFileDialog {
            Filter = "REP 与逐帧轨迹|*.rep;*.csv|REP 文件|*.rep|CSV 轨迹|*.csv",
            Multiselect = true,
            InitialDirectory = replayDirectory ?? root,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != true) return;
        replayDirectory = System.IO.Path.GetDirectoryName(dialog.FileName);
        SaveSettings();
        foreach (var file in dialog.FileNames) await Load(file);
    }
    private async void ConnectGame() => await ConnectGameAsync(true);

    private static bool NeedsCompleteCapture(ReplaySession replay) =>
        replay.Header is not null && (replay.Frames.Count < 100 ||
            replay.Frames[^1].MatchState != 5);

    private async Task<bool> ConnectGameAsync(bool analyzePendingReplay, int? targetPid = null)
    {
        if (shutdownInProgress || shutdownReady || ReplayControlInFlight || branchBusy || escapeBusy || fullCaptureBusy || probeConnecting) return false;
        probeConnecting = true;
        ConnectionTrace.Write("connect.begin", targetPid ?? 0);
        try
        {
            if (probe is not null && detectedGame is not null && liveReader?.IsAlive == true &&
                (targetPid is null || detectedGame.Pid == targetPid))
            {
                if (analyzePendingReplay && session is not null && NeedsCompleteCapture(session))
                    await CaptureFullReplayAsync();
                else message = $"已连接游戏 PID {detectedGame.Pid}。";
                Render();
                return true;
            }
            var games = await Task.Run(GameDiscovery.FindProcesses)
                .WaitAsync(TimeSpan.FromSeconds(8));
            ConnectionTrace.Write("discovery.done", targetPid ?? 0, $"candidates={games.Count}");
            if (shutdownInProgress || shutdownReady) return false;
            AttachGame(targetPid is null ? games.FirstOrDefault() :
                games.FirstOrDefault(game => game.Pid == targetPid));
            ConnectionTrace.Write("reader.attached", detectedGame?.Pid ?? 0,
                $"hwnd={detectedGame?.Window.ToInt64() ?? 0}");
            var directXFailed = detectedGame is not null &&
                await Task.Run(() => GameDiscovery.HasDirectXError(detectedGame.Pid))
                    .WaitAsync(TimeSpan.FromSeconds(5));
            if (detectedGame is null || directXFailed || detectedGame.Window == 0)
            {
                ConnectionTrace.Write("connect.no-window", detectedGame?.Pid ?? 0,
                    $"directx={directXFailed}");
                message = directXFailed
                    ? $"游戏 PID {detectedGame!.Pid} 出现 DirectX 初始化错误，无法连接。"
                    : detectedGame is null
                        ? "没有找到经过版本校验的非想天则 1.10a 进程。请先启动游戏。"
                        : $"已找到游戏 PID {detectedGame.Pid}，但尚未识别到主窗口。";
                Render();
                return false;
            }
            message = $"已找到游戏 PID {detectedGame.Pid}，正在连接探针…";
            Render();
            ClearNativeSnapshots();
            ConnectionTrace.Write("probe.attach.begin", detectedGame.Pid);
            var attachedProbe = await ProbeClient.AttachAsync(detectedGame, root);
            if (shutdownInProgress || shutdownReady) { attachedProbe.Dispose(); return false; }
            probe?.Dispose();
            probe = attachedProbe;
            nativeReplaySource = null;
            nativePlaybackReleased = true;
            ConnectionTrace.Write("probe.attach.done", detectedGame.Pid);
            probeStatus = probe.Read();
            embedFailed = false;
            probeTimer.Start();
            if (analyzePendingReplay && session?.Header is not null &&
                string.Equals(System.IO.Path.GetExtension(session.Source), ".rep",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (NeedsCompleteCapture(session)) await CaptureFullReplayAsync();
                else
                {
                    await LoadCurrentRepIntoGameAsync();
                    message = $"已连接游戏 PID {detectedGame.Pid}，并载入当前 REP。";
                }
            }
            else message = $"已连接游戏 PID {detectedGame.Pid}；导入 REP 后会自动分析。";
            Render();
            ConnectionTrace.Write("connect.done", detectedGame.Pid);
            return true;
        }
        catch (Exception ex)
        {
            ConnectionTrace.Write("connect.failed", detectedGame?.Pid ?? targetPid ?? 0,
                ex.GetType().Name + ": " + ex.Message);
            message = $"连接游戏失败：{ex.Message}";
            Render();
            return false;
        }
        finally { probeConnecting = false; }
    }
    private async void StartGame()
    {
        if (shutdownInProgress || shutdownReady || probeConnecting || ReplayControlInFlight || branchBusy || escapeBusy || fullCaptureBusy || sessionLoading || gameStarting) return;
        gameStarting = true;
        ConnectionTrace.Write("launch.begin");
        try
        {
            var existing = await Task.Run(GameDiscovery.FindProcesses)
                .WaitAsync(TimeSpan.FromSeconds(8));
            if (shutdownInProgress || shutdownReady) return;
            if (existing.Count > 0)
            {
                if (await Task.Run(() => existing.Any(g =>
                    GameDiscovery.HasDirectXError(g.Pid)))
                    .WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    message = "已运行的游戏出现 DirectX 初始化错误，当前不能连接探针。";
                    Render();
                    return;
                }
                AttachGame(existing[0]);
                gameDirectory = System.IO.Path.GetDirectoryName(existing[0].Exe);
                SaveSettings();
                if (autoConnectOnLaunch)
                    await ConnectGameAsync(true, existing[0].Pid);
                else
                {
                    message = $"游戏已运行（PID {existing[0].Pid}），不会再启动第二个实例。";
                    Render();
                }
                return;
            }
            var dialog = new OpenFileDialog {
                Filter = "非想天则主程序|th123.exe|可执行文件|*.exe",
                Title = "选择非想天则 1.10a 主程序",
                InitialDirectory = gameDirectory ?? root,
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) != true) return;
            gameDirectory = System.IO.Path.GetDirectoryName(dialog.FileName);
            SaveSettings();
            var process = await Task.Run(() => GameDiscovery.Launch(dialog.FileName));
            launchedGamePid = process.Id;
            launchedGameExe = dialog.FileName;
            ConnectionTrace.Write("launch.process-created", process.Id);
            message = $"正在等待游戏 PID {process.Id} 的主窗口和 DirectX 初始化…";
            Render();
            GameInstance? game = null;
            var directXFailed = false;
            var stableWindowChecks = 0;
            var launchDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            for (var attempt = 0; attempt < 60 && !process.HasExited &&
                 DateTimeOffset.UtcNow < launchDeadline; attempt++)
            {
                var (candidate, hasError) = await Task.Run(() => (
                    GameDiscovery.Find().FirstOrDefault(x => x.Pid == process.Id),
                    GameDiscovery.HasDirectXError(process.Id)))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                directXFailed = hasError;
                if (directXFailed) break;
                stableWindowChecks = candidate is not null && game?.Window == candidate.Window
                    ? stableWindowChecks + 1 : candidate is null ? 0 : 1;
                game = candidate;
                if (stableWindowChecks >= 10) break;
                await Task.Delay(250);
            }
            if (directXFailed)
            {
                ConnectionTrace.Write("launch.directx-error", process.Id);
                message = "游戏出现 DirectX 初始化错误，已保留游戏进程供检查。";
            }
            else if (game is not null && !process.HasExited && stableWindowChecks >= 10)
            {
                AttachGame(game);
                if (autoConnectOnLaunch)
                    await ConnectGameAsync(true, process.Id);
                else message = $"游戏 PID {process.Id} 的主窗口已就绪，已建立只读连接。";
            }
            else message = process.HasExited
                ? $"游戏 PID {process.Id} 在建立主窗口前退出。"
                : $"游戏 PID {process.Id} 尚无稳定主窗口；可稍后点“连接游戏”。";
        }
        catch (Exception ex)
        {
            ConnectionTrace.Write("launch.failed", 0, ex.GetType().Name + ": " + ex.Message);
            message = $"启动游戏失败：{ex.Message}";
        }
        finally { gameStarting = false; Render(); }
    }
    private void AttachGame(GameInstance? instance)
    {
        if (instance?.Pid == detectedGame?.Pid && liveReader?.IsAlive == true) {
            detectedGame = instance;
            if (instance is not null)
            {
                managedGamePid = instance.Pid;
                managedGameExe = instance.Exe;
            }
            return;
        }
        if (probe is not null && probe.Pid != instance?.Pid)
        {
            if (liveReader?.IsAlive == true)
                throw new InvalidOperationException("先在回放页释放当前探针，再切换游戏进程。");
            probeTimer.Stop();
            probe.Dispose();
            probe = null;
            probeStatus = null;
            nativeReplaySource = null;
            nativePlaybackReleased = true;
            ClearNativeSnapshots();
        }
        liveTimer.Stop();
        liveReader?.Dispose();
        liveReader = null;
        liveStatus = null;
        detectedGame = null;
        if (instance is null) return;
        managedGamePid = instance.Pid;
        managedGameExe = instance.Exe;
        liveReader = new LiveGameReader(instance);
        detectedGame = instance;
        liveTimer.Start();
        RefreshLiveStatus();
    }
    private void RefreshLiveStatus()
    {
        if (liveReader is null) return;
        if (!liveReader.IsAlive)
        {
            AttachGame(null);
            connection.Text = "● 游戏未连接";
            if (liveStatusLabel is not null) liveStatusLabel.Text = "游戏进程已退出。";
            return;
        }
        liveStatus = liveReader.TryRead(out var current) ? current : null;
        if (liveStatusLabel is not null) liveStatusLabel.Text = LiveStatusText();
    }
    private string LiveStatusText()
    {
        if (liveStatus is null) return "已连接游戏；当前未进入对局，或这一帧的状态无法完整读取。";
        var state = liveStatus;
        return $"引擎帧 {state.EngineFrame:N0} · 第 {state.Round + 1} 回合 · {WeatherName(state.Weather)}\n" +
            $"1P  HP {state.Player1.Hp:N0} · 灵力 {state.Player1.Spirit:N0} · {ActionCatalog.Display(state.Player1.Action, state.Player1.Character)} [{state.Player1.Action}] · 位置 ({state.Player1.X:0}, {state.Player1.Y:0})\n" +
            $"2P  HP {state.Player2.Hp:N0} · 灵力 {state.Player2.Spirit:N0} · {ActionCatalog.Display(state.Player2.Action, state.Player2.Character)} [{state.Player2.Action}] · 位置 ({state.Player2.X:0}, {state.Player2.Y:0})";
    }
    private async void LoadRepIntoGame()
    {
        if (PlaybackOperationBlocked || ReplayControlInFlight) return;
        if (probe is null || session?.Header is null) {
            message = "先导入 REP，并连接探针。"; Render(); return;
        }
        nativeTransportBusy = true;
        await playbackControlGate.WaitAsync();
        try
        {
            if (shutdownInProgress || shutdownReady) return;
            await LoadCurrentRepIntoGameAsync();
            message = "游戏已载入 REP；探针正在记录逐帧轨迹。";
        }
        catch (Exception ex) { message = $"游戏载入 REP 失败：{ex.Message}"; }
        finally { nativeTransportBusy = false; playbackControlGate.Release(); UpdateTransport(); }
        Render();
    }
    private async void CaptureFullReplay() => await CaptureFullReplayAsync();

    private void CancelFullCapture()
    {
        if (!fullCaptureBusy) return;
        fullCaptureCancelRequested = true;
        message = "正在取消完整采集并暂停游戏回放…";
        Render();
    }

    private async Task CaptureFullReplayAsync()
    {
        if (shutdownInProgress || shutdownReady || ReplayControlInFlight || fullCaptureBusy || branchBusy || escapeBusy ||
            branchOriginal is not null || probe is null || session?.Header is null)
            return;
        fullCaptureBusy = true;
        fullCaptureCancelRequested = false;
        dismissedImportProgress = false;
        dismissedNoticeMessage = null;
        hiddenNoticeMessage = null;
        SetImportProgress(0, session.Header.InputFrames);
        message = "正在快速计算完整 REP；跳过游戏画面绘制，逐帧记录引擎状态。";
        Render();
        await playbackControlGate.WaitAsync();
        try
        {
            if (shutdownInProgress || shutdownReady) return;
            await LoadCurrentRepIntoGameAsync();
            if (fullCaptureCancelRequested) throw new OperationCanceledException();
            probeStatus = await probe.SendAsync(ProbeCommand.Capture);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            var lastProgress = -1;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (fullCaptureCancelRequested) throw new OperationCanceledException();
                await Task.Delay(100);
                probeStatus = probe.Read();
                var progress = Math.Max(0, probeStatus.Ordinal) / ReplayTiming.FramesPerSecond;
                if (progress > lastProgress && probeStatus.Status == 1)
                {
                    lastProgress = progress;
                    SetImportProgress(probeStatus.Ordinal, session.Header.InputFrames);
                }
                if (fullCaptureCancelRequested) throw new OperationCanceledException();
                if (probeStatus.Status == 3) break;
                if (probeStatus.Status < 0)
                    throw new IOException($"游戏探针状态 {probeStatus.Status}。");
            }
            if (probeStatus?.Status != 3)
                throw new TimeoutException("三分钟内未到达回放终局。");
            if (fullCaptureCancelRequested) throw new OperationCanceledException();
            await RefreshSessionFromProbe();
            if (session is null || session.Frames.Count < 100 ||
                session.Frames[^1].MatchState != 5)
                throw new InvalidDataException("终局轨迹不足，未更新分析。");
            var verified = System.IO.Path.Combine(root,
                "verified_trace_" + session.Header!.Hash + ".csv");
            var nativeTrace = System.IO.Path.Combine(root,
                "trace_" + session.Header.Hash + ".csv");
            var staged = verified + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(nativeTrace, staged);
                File.Move(staged, verified, overwrite: true);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
            session.TraceOrigin = "已验证的完整实机轨迹";
            var savedIndex = samples.FindIndex(x => string.Equals(
                System.IO.Path.GetFullPath(x.Source), System.IO.Path.GetFullPath(session.Source),
                StringComparison.OrdinalIgnoreCase));
            if (savedIndex >= 0) samples[savedIndex] = session;
            message = $"本次游戏已完整采集 {session.Frames.Count:N0} 帧；" +
                "压制、逃压候选和统计已按实机轨迹刷新并保存。";
            try
            {
                probeStatus = await probe.SendAsync(ProbeCommand.Seek, target: 0);
                probeStatus = await probe.SendAsync(ProbeCommand.Speed,
                    target: (int)Math.Round(playbackSpeed * 100));
                cursor = 0;
            }
            catch (Exception ex)
            {
                message += $" 回放返回起点失败：{ex.Message}";
            }
            if (!message.Contains("失败", StringComparison.Ordinal))
                hiddenNoticeMessage = message;
        }
        catch (OperationCanceledException)
        {
            try
            {
                probeStatus = await probe!.SendAsync(ProbeCommand.Pause);
                message = "完整采集已取消；未保存这次未完成的轨迹。";
            }
            catch (Exception ex)
            {
                message = $"完整采集已取消，但暂停游戏回放失败：{ex.Message}";
            }
        }
        catch (Exception ex)
        {
            message = $"完整采集失败：{ex.Message}";
            try
            {
                if (probe is not null && probe.Read().Status is 1 or 2)
                    probeStatus = await probe.SendAsync(ProbeCommand.Pause);
            }
            catch (Exception pauseError) { message += $" 暂停回放失败：{pauseError.Message}"; }
        }
        finally
        {
            playbackControlGate.Release();
            fullCaptureBusy = false;
            fullCaptureCancelRequested = false;
            importProgressText = null;
            Render();
        }
    }

    public async Task<(int FrameCount, int FinalOrdinal, string Message)> SmokeFullCaptureAsync()
    {
        await CaptureFullReplayAsync();
        if (message?.StartsWith("完整采集失败：", StringComparison.Ordinal) == true)
            throw new InvalidDataException(message);
        return (session?.Frames.Count ?? 0, probeStatus?.Ordinal ?? -1, message ?? "");
    }
    public async Task<(int Ordinal, string Message)> SmokeCancelFullCaptureAsync()
    {
        var capture = CaptureFullReplayAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (probe?.Read().Ordinal < 300 && DateTimeOffset.UtcNow < deadline)
            {
                if (!fullCaptureBusy)
                    throw new InvalidOperationException("完整采集在取消测试前已经结束。");
                await Task.Delay(100);
            }
            if (probe?.Read().Ordinal < 300)
                throw new TimeoutException("采集未能推进到测试取消帧。");
            CancelFullCapture();
            await capture;
            if (message?.StartsWith("完整采集已取消；", StringComparison.Ordinal) != true ||
                probeStatus?.Status != 3 || probeStatus.Ordinal < 300)
                throw new InvalidDataException("完整采集未正常暂停：" + message);
            var ordinal = probeStatus.Ordinal;
            await Task.Delay(250);
            if (probe?.Read().Ordinal != ordinal)
                throw new InvalidDataException("取消后游戏回放仍在推进。");
            return (ordinal, message);
        }
        finally
        {
            if (fullCaptureBusy)
            {
                CancelFullCapture();
                await capture;
            }
        }
    }
    private async Task LoadCurrentRepIntoGameAsync()
    {
        if (probe is null || session?.Header is null ||
            !string.Equals(System.IO.Path.GetExtension(session.Source), ".rep",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前需要一份有效 REP 才能载入游戏。");
        PausePlayback();
        ClearNativeSnapshots();
        branchOriginal = null;
        escapeResults.Clear();
        selectedEscapeResult = null;
        var trace = System.IO.Path.Combine(root, "trace_" + session.Header.Hash + ".csv");
        nativeReplaySource = null;
        nativePlaybackReleased = true;
        probeStatus = await probe.SendAsync(ProbeCommand.Load,
            replayPath: session.Source, tracePath: trace);
        nativeReplaySource = session.Source;
        nativePlaybackReleased = false;
        probeStatus = await probe.SendAsync(ProbeCommand.Seek, 0);
        probeStatus = await probe.SendAsync(ProbeCommand.Speed, (int)Math.Round(playbackSpeed * 100));
        AdoptPlaybackPosition(probeStatus);
    }
    private void SetGameSpeed(double rate) => _ = ChangePlaybackSpeedAsync(rate);
    private int sessionRefreshVersion;
    private async Task RefreshSessionFromProbe()
    {
        if (session?.Header is null) return;
        var originalSession = session;
        var originalProbe = probe;
        var refreshVersion = ++sessionRefreshVersion;
        var source = originalSession.Source;
        var nativeTrace = System.IO.Path.Combine(root,
            "trace_" + originalSession.Header.Hash + ".csv");
        var updated = await Task.Run(() => SessionLoader.Load(source, root, nativeTrace,
            preloadedHeader: originalSession.Header));
        if (refreshVersion != sessionRefreshVersion ||
            !ReferenceEquals(session, originalSession) || !ReferenceEquals(probe, originalProbe) ||
            shutdownInProgress || shutdownReady) return;
        if (!fullCaptureBusy && session.TraceOrigin == "已验证的完整实机轨迹" &&
            updated.Frames.Count < session.Frames.Count)
        {
            cursor = Math.Clamp(probeStatus?.Ordinal ?? cursor, 0, session.Frames.Count - 1);
            return;
        }
        session = updated;
        cursor = Math.Clamp(probeStatus?.Ordinal ?? cursor, 0, Math.Max(0, updated.Frames.Count - 1));
        var index = samples.FindIndex(x => string.Equals(System.IO.Path.GetFullPath(x.Source),
            System.IO.Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase));
        if (index >= 0) samples[index] = updated;
    }
    private void RefreshProbeStatus()
    {
        if (probe is null) return;
        try
        {
            probeStatus = probe.Read();
            if (NativeSessionMatches && !PlaybackOperationBlocked && !nativeTransportBusy && pendingSeekTarget is null &&
                probeStatus.Status is 2 or 3 && session?.Frames.Count > 0)
            {
                var observed = Math.Clamp(probeStatus.Ordinal, 0, session.Frames.Count - 1);
                if (replaySlider?.IsMouseCaptureWithin != true && cursor != observed)
                {
                    cursor = observed;
                    refreshReplayFrame?.Invoke();
                }
            }
            gameSurface?.Reconcile();
            if (gameSurface?.LayoutError is { } layoutError)
            {
                var notice = $"游戏画面暂时脱离，正在自动恢复：{layoutError}";
                if (message != notice)
                {
                    message = notice;
                    if (page == 0) RefreshReplayPageInPlace();
                }
            }
            if (probeStatusLabel is not null) probeStatusLabel.Text = ProbeStatusText();
            UpdateTransport();
        }
        catch (Exception ex)
        {
            probeTimer.Stop();
            message = $"探针状态读取失败：{ex.Message}";
            Render();
        }
    }
    private string ProbeStatusText()
    {
        if (probeStatus is null) return "等待探针状态。";
        var state = probeStatus.Status switch {
            0 => "已释放", 1 => "载入或定位中", 2 => "播放中", 3 => "已暂停",
            _ => $"错误 {probeStatus.Status}"
        };
        return $"{state} · 轨迹帧 {probeStatus.Ordinal:N0} · 引擎帧 {probeStatus.EngineFrame:N0} · 命令 {probeStatus.Acknowledged}/{probeStatus.Sequence}";
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownReady) return;
        e.Cancel = true;
        if (shutdownInProgress) return;
        shutdownInProgress = true;
        try
        {
            gameFocusTimer.Stop(); gameKeyboardFocus?.Dispose(); gameKeyboardFocus = null;
            playbackTimer.Stop(); liveTimer.Stop(); probeTimer.Stop();
            pressureEscapeCancellation?.Cancel();
            fullCaptureCancelRequested = true;
            if (pressureEscapeTask is { IsCompleted: false }) await pressureEscapeTask;
            if (sessionLoadTask is { IsCompleted: false }) await sessionLoadTask;
            await playbackControlGate.WaitAsync();
            playbackControlGate.Release();
            var games = new Dictionary<int, string>();
            if (detectedGame is not null)
                games[detectedGame.Pid] = detectedGame.Exe;
            if (launchedGamePid > 0 && launchedGameExe is not null)
                games[launchedGamePid] = launchedGameExe;
            if (managedGamePid > 0 && managedGameExe is not null)
                games[managedGamePid] = managedGameExe;
            if (probe is not null && !games.ContainsKey(probe.Pid))
            {
                var connected = await Task.Run(() => GameDiscovery.FindProcesses()
                    .FirstOrDefault(game => game.Pid == probe.Pid));
                if (connected is not null) games[connected.Pid] = connected.Exe;
            }
            foreach (var (pid, exe) in games)
                await GameDiscovery.CloseVerifiedGameAsync(pid, exe);
            probe?.Dispose();
            probe = null;
            liveReader?.Dispose();
            liveReader = null;
            ClearNativeSnapshots();
            for (var i = 0; gameSurface is not null && !gameSurface.Detach() && i < 100; i++)
                await Task.Delay(50);
            if (gameSurface is not null && !gameSurface.Detach())
            {
                throw new TimeoutException("游戏窗口宿主未能在游戏退出后完成收尾。");
            }
            shutdownReady = true;
            Close();
        }
        catch (Exception ex)
        {
            message = $"关闭游戏失败，复盘程序保持开启：{ex.Message}";
            Render();
        }
        finally { shutdownInProgress = false; }
    }
    private Task Load(string path)
    {
        if (sessionLoadTask is { IsCompleted: false }) return Task.CompletedTask;
        return sessionLoadTask = LoadSessionAsync(path);
    }
    private async Task LoadSessionAsync(string path)
    {
        if (shutdownInProgress || shutdownReady || probeConnecting || ReplayControlInFlight || branchBusy || escapeBusy || fullCaptureBusy || sessionLoading)
        {
            message = "当前回放任务正在执行，完成后再切换样本。";
            Render();
            return;
        }
        sessionLoading = true;
        PausePlayback();
        ClearNativeSnapshots();
        dismissedImportProgress = false;
        dismissedNoticeMessage = null;
        hiddenNoticeMessage = null;
        importProgressText = null;
        message = "正在读取和分析轨迹…";
        Render();
        try
        {
            ReplayHeader? header = null;
            if (string.Equals(System.IO.Path.GetExtension(path), ".rep",
                    StringComparison.OrdinalIgnoreCase))
            {
                header = await Task.Run(() => ReplayHeader.Read(path));
                SetImportProgress(0, header.InputFrames);
            }
            var result = await Task.Run(() => SessionLoader.Load(path, root,
                preloadedHeader: header));
            if (result.Header is not null)
                SetImportProgress(result.Frames.Count, result.Header.InputFrames);
            if (shutdownInProgress || shutdownReady) return;
            if (probe?.Read().Status == 2) await probe.SendAsync(ProbeCommand.Pause);
            ResetReplayContext();
            session = result;
            if (statsRound >= 0 && !result.Stats.Any(x => x.Round == statsRound)) statsRound = -1;
            cursor = 0;
            var existingIndex = samples.FindIndex(x => string.Equals(System.IO.Path.GetFullPath(x.Source),
                System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0) samples[existingIndex] = result;
            else samples.Add(result);
            message = result.Frames.Count == 0
                ? "REP 头已读取，但没有对应的已采集轨迹。实际对局统计需要连接游戏引擎。"
                : $"已分析 {result.Frames.Count:N0} 帧（{result.TraceOrigin}）；基础统计、连段、压制和事件由新版 C# 代码计算。" +
                  (NeedsCompleteCapture(result) ? " 当前轨迹未到终局。" : "");
            if (result.Header is not null &&
                string.Equals(System.IO.Path.GetExtension(result.Source), ".rep",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (probe is null) await ConnectGameAsync(false);
                if (shutdownInProgress || shutdownReady) return;
                if (probe is not null)
                {
                    if (NeedsCompleteCapture(result)) await CaptureFullReplayAsync();
                    else
                    {
                        await LoadCurrentRepIntoGameAsync();
                        message += " 已自动载入游戏。";
                    }
                }
                else if (NeedsCompleteCapture(result))
                    message = "REP 已导入；当前没有完整轨迹或游戏连接。启动游戏后点“连接游戏”，将自动采集并计算。";
            }
            else if (probe is not null)
                message += " CSV 轨迹仅供离线分析，未切换游戏回放。";
        }
        catch (Exception ex) { message = session is not null &&
            string.Equals(System.IO.Path.GetFullPath(session.Source),
                System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                ? $"REP 已导入，但自动载入游戏失败：{ex.Message}"
                : $"导入失败：{ex.Message}"; }
        finally
        {
            sessionLoading = false;
            importProgressText = null;
            if (session?.Frames.Count > 0 && !NeedsCompleteCapture(session) &&
                message?.StartsWith("已分析", StringComparison.Ordinal) == true)
                hiddenNoticeMessage = message;
        }
        Render();
    }

    public void Smoke(ReplaySession test, ReplaySession? second = null)
    {
        var area = SystemParameters.WorkArea;
        if (Width > area.Width + 1 || Height > area.Height + 1 ||
            Left < area.Left - 1 || Top < area.Top - 1 ||
            Left + Width > area.Right + 1 || Top + Height > area.Bottom + 1)
            throw new InvalidOperationException("主窗口初始尺寸超出屏幕可用区域。");
        session = test;
        samples.Add(test);
        if (second is not null) samples.Add(second);
        for (var i = 0; i < Pages.Length; i++)
        {
            page = i;
            Render();
            if (i is 0 or 1)
            {
                SetImportProgress(123, 456);
                if (topNotice?.Visibility != Visibility.Visible ||
                    topNoticeText?.Text != "已导入 123 / REP 总共 456 帧")
                    throw new InvalidOperationException("导入帧数提示未显示在页面顶部。");
                if (i == 0 && topNotice?.Child is DockPanel noticeRow)
                {
                    noticeRow.Children.OfType<Button>().Single().RaiseEvent(
                        new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    if (topNotice.Visibility != Visibility.Collapsed)
                        throw new InvalidOperationException("顶部提示的关闭按钮未生效。");
                    dismissedImportProgress = false;
                }
                importProgressText = null;
                hiddenNoticeMessage = message;
                UpdateTopNotice();
                if (topNotice?.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("导入完成后顶部提示未自动隐藏。");
            }
            if (i == 3) SmokeCurveViewport();
        }
        page = 7;
        var calculatedTips = interpretedTips;
        if (calculatedTips is null)
            throw new InvalidOperationException("REP 解读没有生成提示缓存。");
        foreach (var side in new[] { 0, 1, -1 })
        {
            interpretSide = side;
            Render();
            if (!ReferenceEquals(calculatedTips, interpretedTips))
                throw new InvalidOperationException("切换解读角色时重复计算了整场回放。");
        }
        statsSide = 0;
        statsRound = test.Stats.FirstOrDefault()?.Round ?? -1;
        foreach (var category in new[] { "全表", "时间、资源与输入", "命中与伤害",
            "攻防判定", "防御", "主动压制", "受身与逃压", "移动与招式", "招式使用" })
        {
            statsCategory = category;
            page = 1;
            Render();
        }
        comboShowDashes = false; page = 4; Render();
        comboShowDashes = true; Render();
        minPressureMoves = 3;
        page = 5; Render();
        statsCategory = "受身与逃压";
        page = 1; Render();
        page = 6; Render();
        escapeOnlySuccess = true; Render();
        escapeOnlySuccess = false;
        foreach (var category in new[] { "全表", "时间与输入", "伤害与连段", "防守与压制", "资源与行动" })
        {
            multiCategory = category;
            page = 2;
            Render();
        }
        detectedGame = new GameInstance(-1, (nint)123, "", "界面宿主检查");
        page = 0;
        Render();
        gameSurface = new GameSurfaceHost(detectedGame, _ => { });
        var originalHost = gameSurface;
        message = "检查回放页刷新";
        Render();
        if (originalHost is null || !ReferenceEquals(originalHost, gameSurface))
            throw new InvalidOperationException("回放页刷新意外重建了游戏窗口宿主。");
        session = null;
        Render();
        session = test;
        Render();
        if (!ReferenceEquals(originalHost, gameSurface))
            throw new InvalidOperationException("更换回放数据意外重建了游戏窗口宿主。");
        detectedGame = null;
        Render();
    }

    public void SmokeAttachForShutdown(GameInstance game) => AttachGame(game);

    public async Task SmokeTableWheelAsync(ReplaySession test)
    {
        session = test;
        statsCategory = "全表";
        statsSide = -1;
        statsRound = -1;
        page = 1;
        Render();
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (pageScroller is null || pageScroller.ScrollableHeight <= 0)
            throw new InvalidOperationException("统计页没有形成页面级滚动范围。");
        pageScroller.ScrollToVerticalOffset(200);
        UpdateLayout();
        static DataGrid? FindTable(DependencyObject parent)
        {
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return null;
            if (parent is DataGrid table) return table;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var found = FindTable(VisualTreeHelper.GetChild(parent, i));
                if (found is not null) return found;
            }
            return null;
        }
        var grid = FindTable(content) ??
            throw new InvalidOperationException("统计页表格未显示。");
        var before = pageScroller.VerticalOffset;
        grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,
            Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        UpdateLayout();
        if (pageScroller.VerticalOffset <= before)
            throw new InvalidOperationException("在表格上滚轮没有推动整页滚动。");
        var initialRows = grid.Items.Count;
        var tableBottom = grid.TranslatePoint(new Point(0, grid.ActualHeight), pageScroller).Y;
        pageScroller.ScrollToVerticalOffset(pageScroller.VerticalOffset +
            tableBottom - pageScroller.ViewportHeight + 80);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (grid.Tag is int totalRows && totalRows > initialRows &&
            grid.Items.Count <= initialRows)
            throw new InvalidOperationException($"页面下滑后表格没有继续加载行：" +
                $"行={initialRows}/{totalRows}，页偏移={pageScroller.VerticalOffset:0}，" +
                $"表底={grid.TranslatePoint(new Point(0, grid.ActualHeight), pageScroller).Y:0}，" +
                $"已加载={grid.IsLoaded}，可见={grid.IsVisible}，" +
                $"表高={grid.ActualHeight:0}，视口={pageScroller.ViewportHeight:0}。");
    }

    public async Task SmokeTimelineClickAsync(ReplaySession test)
    {
        session = test;
        page = 4;
        Render();
        UpdateLayout();
        static DataGrid? FindTable(DependencyObject parent)
        {
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return null;
            if (parent is DataGrid found) return found;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var nested = FindTable(VisualTreeHelper.GetChild(parent, i));
                if (nested is not null) return nested;
            }
            return null;
        }
        var grid = FindTable(content) ??
            throw new InvalidOperationException("连段时间表未显示。");
        if (grid.CanUserSortColumns || grid.CanUserResizeColumns ||
            grid.CanUserReorderColumns || grid.CanUserResizeRows)
            throw new InvalidOperationException("表格仍允许排序或调整尺寸。");
        if (grid.Items.Count == 0)
            throw new InvalidOperationException("连段时间表没有可验证的数据。");
        grid.ScrollIntoView(grid.Items[0]);
        UpdateLayout();
        var ordinary = grid.Columns[1].GetCellContent(grid.Items[0]) ??
            throw new InvalidOperationException("普通表格单元格未生成。");
        ordinary.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
            Environment.TickCount, MouseButton.Left) {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
        if (page != 4 || grid.SelectedIndex >= 0)
            throw new InvalidOperationException("普通表格单元格仍可交互。");
        var time = grid.Columns[0].GetCellContent(grid.Items[0]) ??
            throw new InvalidOperationException("时间单元格未生成。");
        time.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
            Environment.TickCount, MouseButton.Left) {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (page != 0 || cursor != test.Combos.Where(x => x.Side == 0)
                .OrderBy(x => x.Start).First().Start)
            throw new InvalidOperationException("单击连段时间未跳转到对应帧。");
    }

    public async Task<(string? Source, int Status, int Ordinal, int FrameCount,
        string? Message)> SmokeAutoImportAsync(GameInstance game, string path)
    {
        detectedGame = game;
        probe = ProbeClient.TryOpen(game.Pid)
            ?? throw new InvalidOperationException("游戏尚未加载新版探针。");
        probeStatus = probe.Read();
        probeTimer.Start();
        await Load(path);
        return (session?.Source, probeStatus?.Status ?? -1,
            probeStatus?.Ordinal ?? -1, session?.Frames.Count ?? 0, message);
    }

    public async Task<(string? Source, int Status, int Ordinal, int FrameCount,
        string? Message)> SmokeImportAnotherAsync(string path)
    {
        if (probe is null) throw new InvalidOperationException("游戏探针尚未连接。");
        await Load(path);
        return (session?.Source, probeStatus?.Status ?? -1,
            probeStatus?.Ordinal ?? -1, session?.Frames.Count ?? 0, message);
    }

    public async Task<(int Anchor, int Defender, string Move, string Result,
        string Reason, int MatchedFrames, int End)> SmokeEscapeMoveAsync(string move,
        int candidateIndex = 0)
    {
        if (probe is null || session is null)
            throw new InvalidOperationException("未连接游戏或导入回放。");
        var original = session;
        var pressure = PressureSelection.Select(original, minPressureMoves)
            .Skip(candidateIndex).FirstOrDefault()
            ?? throw new InvalidOperationException("没有符合压制总表设置的压制。");
        var job = new PressureEscapeJob(pressure, move);
        var plans = PressureEscapeSimulation.Plans(original, job);
        if (plans.Count == 0) throw new InvalidOperationException("缺少可验证的输入时机。");
        var runner = new PressureEscapeRunner(original, new EscapeSimulationGameEngine(probe, original, root));
        try
        {
            await runner.StartAsync(plans.Max(p => p.Anchor + p.Inputs.Length));
            var result = await runner.RunAsync(job, () => false);
            StoreEscapeResult(result);
            return (result.Anchor, result.Defender, move, result.Verdict.Result,
                result.Verdict.Reason, result.MatchedInputFrames, result.End);
        }
        finally { await runner.RestoreAsync(0); probeStatus = probe.Read(); }
    }

    public (bool Attached, long HostHandle) SmokeConnectResident(GameInstance game, ReplaySession test)
    {
        session = test;
        detectedGame = game;
        probe = ProbeClient.TryOpen(game.Pid)
            ?? throw new InvalidOperationException("游戏尚未加载新版探针。");
        probeStatus = probe.Read();
        page = 0;
        Render();
        UpdateLayout();
        return (gameSurface?.IsAttached == true, gameSurface?.HostHandle ?? 0);
    }

    public async Task<(int Saved, int Restored, bool SlotVisible, string Message)> SmokeNativeSnapshotAsync()
    {
        if (probe is null || session?.Header is null)
            throw new InvalidOperationException("界面未连接游戏回放。");
        var trace = System.IO.Path.Combine(root, "trace_" + session.Header.Hash + ".csv");
        ClearNativeSnapshots();
        probeStatus = await probe.SendAsync(ProbeCommand.Load,
            replayPath: session.Source, tracePath: trace);
        probeStatus = await probe.SendAsync(ProbeCommand.Seek, target: 400);
        nativeReplaySource = session.Source;
        nativePlaybackReleased = false;
        await RefreshSessionFromProbe();
        cursor = 400;
        page = 0;
        Render();
        await SaveNativeSnapshotAsync(400);
        if (!nativeSnapshotSlots.Contains(400))
            throw new InvalidOperationException("界面保存操作没有登记第 400 帧。");
        probeStatus = await probe.SendAsync(ProbeCommand.Seek, target: 500);
        await RefreshSessionFromProbe();
        cursor = 500;
        Render();
        var slotVisible = nativeSnapshotSlots.Contains(400) &&
            nativeSnapshotPid == probe.Pid &&
            string.Equals(nativeSnapshotSource, session?.Source,
                StringComparison.OrdinalIgnoreCase);
        await RestoreNativeSnapshotAsync(400);
        if (probeStatus?.Ordinal != 400 || cursor != 400)
            throw new InvalidOperationException("界面恢复操作没有回到第 400 帧。");
        return (nativeSnapshotSlots.Count, probeStatus.Ordinal, slotVisible, message ?? "");
    }

    public (bool Attached, long HostHandle) SmokeNavigate(int index)
    {
        NavigateToPage(index);
        UpdateLayout();
        return (gameSurface?.IsAttached == true, gameSurface?.HostHandle ?? 0);
    }

    public (long FirstStatsMs, long FirstMultiMs, long FirstCurvesMs,
        long CachedStatsMs, long CachedMultiMs, long CachedCurvesMs,
        bool ReusedStats, bool ReusedMulti, bool ReusedCurves,
        bool InvalidatedAfterFilter) SmokeNavigationCache(ReplaySession test)
    {
        session = test;
        samples.Clear();
        samples.Add(test);
        page = 0;
        Render();
        long Visit(int target)
        {
            var watch = Stopwatch.StartNew();
            NavigateToPage(target);
            UpdateLayout();
            return watch.ElapsedMilliseconds;
        }
        UIElement LastChild() => content.Children.Cast<UIElement>().Last(element => element.Visibility != Visibility.Collapsed);
        var stats = Visit(1);
        var statsBody = LastChild();
        var multi = Visit(2);
        var multiBody = LastChild();
        var curves = Visit(3);
        var curvesBody = LastChild();
        var cachedStats = Visit(1);
        var reusedStats = ReferenceEquals(statsBody, LastChild());
        var cachedMulti = Visit(2);
        var reusedMulti = ReferenceEquals(multiBody, LastChild());
        var cachedCurves = Visit(3);
        var reusedCurves = ReferenceEquals(curvesBody, LastChild());
        // Ordinary playback renders and persistence must preserve other pages.
        NavigateToPage(0);
        Render();
        InvalidateSettingsViews();
        NavigateToPage(1);
        if (!ReferenceEquals(statsBody, LastChild()))
            throw new InvalidOperationException("回放页刷新或保存播放设置清空了统计页缓存。");
        NavigateToPage(1);
        statsCategory = "防御";
        Render();
        var invalidated = !ReferenceEquals(statsBody, LastChild());
        shutdownReady = true; // This diagnostic has no game session to close.
        return (stats, multi, curves, cachedStats, cachedMulti, cachedCurves,
            reusedStats, reusedMulti, reusedCurves, invalidated);
    }

    public void ExportPreview(ReplaySession test, string path, int pageIndex = 0,
        string? previewSkin = null, double scrollOffset = 0)
    {
        session = test;
        page = Math.Clamp(pageIndex, 0, Pages.Length - 1);
        if (previewSkin is not null) ApplySkin(previewSkin, false);
        else Render();
        var visual = (FrameworkElement)Content;
        visual.Measure(new Size(1440, 900));
        visual.Arrange(new Rect(0, 0, 1440, 900));
        visual.UpdateLayout();
        // Finish coalesced curve drawing before taking the diagnostic image.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        visual.UpdateLayout();
        if (scrollOffset > 0)
        {
            pageScroller?.ScrollToVerticalOffset(scrollOffset);
            visual.UpdateLayout();
        }
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
