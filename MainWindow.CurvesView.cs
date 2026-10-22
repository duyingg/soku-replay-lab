using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private sealed record CurveSegment(int Start, int End, int Row, string Label, Brush Color);
    private sealed record RoundMark(int Index, int Round);

    private ReplaySession? curveSession;
    private int curveWindowFrames;
    private int curveStartFrame;
    private bool curveSyncing;
    private TextBlock? curveRangeLabel;
    private readonly List<ScrollBar> curvePanBars = [];
    private readonly List<Action> curveDraws = [];
    private static int CurveCardHeight(int width) => (int)Math.Round(width * 33.0 / 21);

    private void CurvesPage()
    {
        if (session is null || session.Frames.Count == 0)
        {
            Empty("载入逐帧引擎轨迹后绘制曲线。"); return;
        }
        var active = session.Frames.Where(x => x.MatchState == 2).ToArray();
        var ordinals = active.Select(x => x.Ordinal).ToArray();
        if (ordinals.Length == 0)
        {
            Empty("轨迹没有可绘制的实际对战帧。"); return;
        }
        var plotted = session.Curves.Where(x =>
            Array.BinarySearch(ordinals, x.Ordinal) >= 0).ToArray();
        if (plotted.Length == 0)
        {
            Empty("轨迹没有可绘制的资源曲线采样。"); return;
        }
        var rounds = active.Select((frame, index) => new { frame.Round, Index = index })
            .Where(x => x.Index == 0 || active[x.Index - 1].Round != x.Round)
            .Select(x => new RoundMark(x.Index, x.Round)).ToArray();
        if (!ReferenceEquals(curveSession, session))
        {
            curveSession = session;
            curveWindowFrames = ordinals.Length;
            curveStartFrame = 0;
        }
        curveWindowFrames = Math.Clamp(curveWindowFrames, 1, ordinals.Length);
        curveStartFrame = Math.Clamp(curveStartFrame, 0,
            Math.Max(0, ordinals.Length - curveWindowFrames));
        curvePanBars.Clear();
        curveDraws.Clear();

        var totalSeconds = ordinals.Length * ReplayTiming.SecondsPerFrame;
        var minimumSeconds = Math.Min(10, totalSeconds);
        var range = new Slider {
            Minimum = minimumSeconds, Maximum = Math.Max(minimumSeconds, totalSeconds),
            Value = curveWindowFrames * ReplayTiming.SecondsPerFrame, TickFrequency = 1,
            IsSnapToTickEnabled = false, Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        curveRangeLabel = Text("", 12, Muted);
        curveRangeLabel.VerticalAlignment = VerticalAlignment.Center;
        var scale = new Grid();
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scale.Children.Add(Text("10 秒细节", 12, Muted));
        Grid.SetColumn(range, 1); scale.Children.Add(range);
        var full = Text("整场", 12, Muted);
        Grid.SetColumn(full, 2); scale.Children.Add(full);
        range.ValueChanged += (_, _) => {
            if (curveSyncing) return;
            curveWindowFrames = Math.Clamp((int)Math.Round(range.Value * ReplayTiming.FramesPerSecond), 1,
                ordinals.Length);
            curveStartFrame = Math.Clamp(curveStartFrame, 0,
                Math.Max(0, ordinals.Length - curveWindowFrames));
            SyncCurveViewport(ordinals.Length);
        };
        if (curveToolbarHost is not null)
        {
            curveToolbarHost.Content = Card("时间量程 · 拖动缩放全部曲线",
                Vertical(scale, curveRangeLabel));
            curveToolbarHost.Visibility = Visibility.Visible;
        }

        var legend = new WrapPanel();
        legend.Children.Add(Text("━ 1P  ", 12, Blue, true));
        legend.Children.Add(Text("━ 2P  ", 12, Red, true));
        legend.Children.Add(Text("拖动任一曲线下方的横条，五张曲线同步移动。点击曲线定位回放。", 12, Muted));
        content.Children.Add(legend);
        if (chartEnabled[2]) content.Children.Add(Card("卡槽变化与用卡时刻",
            CurvePanel(Plot(plotted.Select(x => (x.P1Cards / 5, x.P2Cards / 5)).ToArray(),
                plotted, ordinals, rounds, 5, session.CardEvents), ordinals.Length)));
        if (chartEnabled[0]) content.Children.Add(Card("血量变化",
            CurvePanel(Plot(plotted.Select(x => (x.P1Hp / 10000.0, x.P2Hp / 10000.0)).ToArray(),
                plotted, ordinals, rounds, 10000), ordinals.Length)));
        if (chartEnabled[1]) content.Children.Add(Card("灵力变化",
            CurvePanel(Plot(plotted.Select(x => (x.P1Spirit / 1000.0, x.P2Spirit / 1000.0)).ToArray(),
                plotted, ordinals, rounds, 1000), ordinals.Length)));

        var weather = new List<CurveSegment>();
        var weatherStart = 0;
        for (var index = 1; index <= active.Length; index++)
        {
            if (index < active.Length && active[index].Weather == active[index - 1].Weather &&
                active[index].Round == active[index - 1].Round) continue;
            var code = active[weatherStart].Weather;
            if (code != 21) weather.Add(new CurveSegment(weatherStart, index - 1, 0,
                WeatherName(code), WeatherColor(code)));
            weatherStart = index;
        }
        if (chartEnabled[3]) content.Children.Add(Card("天气变化",
            CurvePanel(TimelineChart(weather, ["天气"], ordinals, rounds, false), ordinals.Length)));

        var states = new List<CurveSegment>();
        for (var side = 0; side < 2; side++)
        {
            string Kind(Frame frame)
            {
                var fighter = frame.Side(side);
                return fighter.Action is 97 or 98 or 99 ? "倒地" :
                    fighter.Y > 0.1 ? "空中" : "地面";
            }
            var start = 0;
            for (var index = 1; index <= active.Length; index++)
            {
                if (index < active.Length && Kind(active[index]) == Kind(active[index - 1]) &&
                    active[index].Round == active[index - 1].Round) continue;
                var kind = Kind(active[start]);
                states.Add(new CurveSegment(start, index - 1, side, kind, StateColor(kind)));
                start = index;
            }
        }
        if (chartEnabled[4]) content.Children.Add(Card("角色状态 · 地面 / 空中 / 倒地",
            CurvePanel(TimelineChart(states, ["1P", "2P"], ordinals, rounds, true),
                ordinals.Length)));

        var openings = session.Combos.Where(combo =>
            !combo.Impacts.Any(i => session.Frames[i].Weather == 13)).Select(combo => {
            var action = ReplayAnalyzer.ComboOpeningAction(session.Frames, combo);
            var attacker = session.Frames[Math.Clamp(combo.Start, 0, session.Frames.Count - 1)]
                .Side(combo.Side);
            return (Defender: 1 - combo.Side, Move: action < 0 ? "起手未确认" :
                ActionCatalog.Group(action, attacker.Character));
        }).GroupBy(x => x).Select(x => (x.Key.Defender, x.Key.Move, Count: x.Count()))
            .OrderByDescending(x => x.Count).ToArray();
        content.Children.Add(Card("被攻击起手招式次数", OpeningBars(openings)));
        SyncCurveViewport(ordinals.Length);
    }

    private static int BattleIndex(int[] ordinals, int ordinal)
    {
        var index = Array.BinarySearch(ordinals, ordinal);
        return index >= 0 ? index : Math.Clamp(~index, 0, ordinals.Length - 1);
    }

    private static int LowerBound(int[] sorted, int value)
    {
        var left = 0;
        var right = sorted.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (sorted[middle] < value) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private (int Start, int End) CurveWindow(int count)
    {
        var start = Math.Clamp(curveStartFrame, 0, Math.Max(0, count - curveWindowFrames));
        return (start, Math.Min(count - 1, start + Math.Max(1, curveWindowFrames) - 1));
    }

    private double CurveX(int index, int count, double left, double width)
    {
        var (start, end) = CurveWindow(count);
        return left + (index - start) * width / Math.Max(1, end - start);
    }

    private void SyncCurveViewport(int count)
    {
        if (curveSyncing) return;
        curveSyncing = true;
        try
        {
            var maximum = Math.Max(0, count - curveWindowFrames);
            curveStartFrame = Math.Clamp(curveStartFrame, 0, maximum);
            foreach (var bar in curvePanBars)
            {
                bar.Maximum = maximum;
                bar.ViewportSize = curveWindowFrames;
                bar.LargeChange = Math.Max(ReplayTiming.FramesPerSecond, curveWindowFrames * .75);
                bar.Value = curveStartFrame;
                bar.IsEnabled = maximum > 0;
            }
            if (curveRangeLabel is not null)
            {
                var (start, end) = CurveWindow(count);
                curveRangeLabel.Text = $"当前量程 {Math.Max(0.1, (end - start + 1) * ReplayTiming.SecondsPerFrame):0.#} 秒 · " +
                    $"显示对战 {Time(start)}—{Time(end)} / {Time(count - 1)}";
            }
            foreach (var draw in curveDraws) draw();
        }
        finally { curveSyncing = false; }
    }

    private void SmokeCurveViewport()
    {
        var count = session?.Frames.Count(x => x.MatchState == 2) ?? 0;
        if (count < 1200 || curvePanBars.Count < 2) return;
        curveWindowFrames = 600;
        curveStartFrame = 0;
        SyncCurveViewport(count);
        curvePanBars[0].Value = 300;
        if (curveStartFrame != 300 || curvePanBars.Any(bar =>
            Math.Abs(bar.Value - 300) > .1))
            throw new InvalidOperationException("曲线横向滚动未同步。");
        curveWindowFrames = count;
        curveStartFrame = 0;
        SyncCurveViewport(count);
    }

    private UIElement CurvePanel(Canvas chart, int count)
    {
        var bar = new ScrollBar {
            Orientation = Orientation.Horizontal, Minimum = 0,
            Maximum = Math.Max(0, count - curveWindowFrames),
            ViewportSize = curveWindowFrames, Value = curveStartFrame,
            SmallChange = ReplayTiming.FramesPerSecond / 2.0, LargeChange = Math.Max(ReplayTiming.FramesPerSecond, curveWindowFrames * .75),
            Height = 17, Margin = new Thickness(48, 6, 20, 0)
        };
        bar.ValueChanged += (_, _) => {
            if (curveSyncing) return;
            curveStartFrame = (int)Math.Round(bar.Value);
            SyncCurveViewport(count);
        };
        curvePanBars.Add(bar);
        return Vertical(chart, bar);
    }

    private void RegisterCurve(Canvas canvas, Action draw)
    {
        curveDraws.Add(draw);
        canvas.SizeChanged += (_, _) => draw();
        canvas.Loaded += (_, _) => draw();
    }

    private void DrawCurveRounds(Canvas canvas, RoundMark[] rounds, int count,
        double left, double width, double top, double bottom)
    {
        var (start, end) = CurveWindow(count);
        foreach (var mark in rounds.Where(x => x.Index >= start && x.Index <= end))
        {
            var x = CurveX(mark.Index, count, left, width);
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = bottom,
                Stroke = palette["RoundMarker"], StrokeThickness = 1.4,
                StrokeDashArray = new DoubleCollection([5, 4]), Opacity = .9 });
            var title = Text($"第 {mark.Round + 1} 本", 10, palette["RoundMarker"], true);
            Canvas.SetLeft(title, Math.Min(left + width - 43, x + 3));
            Canvas.SetTop(title, Math.Max(0, top - 16));
            canvas.Children.Add(title);
        }
    }

    private void DrawCurveTimeAxis(Canvas canvas, int count, double left,
        double width, double bottom)
    {
        var (start, end) = CurveWindow(count);
        for (var tick = 0; tick <= 4; tick++)
        {
            var x = left + width * tick / 4;
            var position = start + (int)Math.Round((end - start) * tick / 4.0);
            var text = Text(Time(position), 10, Muted);
            Canvas.SetLeft(text, x - (tick == 4 ? 42 : 8));
            Canvas.SetTop(text, bottom + 5);
            canvas.Children.Add(text);
        }
    }

    private Canvas Plot((double P1, double P2)[] values, Curve[] source,
        int[] ordinals, RoundMark[] rounds, int maximum,
        IReadOnlyList<CardEvent>? cards = null)
    {
        var battleIndices = source.Select(point => BattleIndex(ordinals, point.Ordinal)).ToArray();
        var cardUses = cards is null ? [] : cards.Where(card => card.Kind == CardKind.Use &&
            Array.BinarySearch(ordinals, card.Ordinal) >= 0)
            .OrderBy(card => card.Ordinal).ToArray();
        var iconWidth = curveCardWidth;
        var iconHeight = CurveCardHeight(iconWidth);
        var laneStride = iconHeight + 8;
        var canvas = new Canvas { Height = cards is null ? 222 : 218 + 4 * laneStride + 12,
            Background = Surface, Cursor = Cursors.Hand,
            ToolTip = "点击曲线可定位回放；下方横条调整所有曲线的位置" };
        const double left = 55, right = 20, top = 22, bottom = 188;
        void Draw()
        {
            canvas.Children.Clear();
            var width = Math.Max(180, canvas.ActualWidth - left - right);
            var (start, end) = CurveWindow(ordinals.Length);
            var horizontalScale = width / Math.Max(1, end - start);
            for (var tick = 0; tick <= 5; tick++)
            {
                var y = bottom - (bottom - top) * tick / 5.0;
                canvas.Children.Add(new Line { X1 = left, X2 = left + width,
                    Y1 = y, Y2 = y, Stroke = Border, StrokeThickness = 1 });
                if (tick == 0) continue;
                var label = Text((maximum * tick / 5).ToString(), 10, Muted);
                Canvas.SetLeft(label, 4); Canvas.SetTop(label, y - 8);
                canvas.Children.Add(label);
            }
            for (var tick = 0; tick <= 4; tick++)
            {
                var x = left + width * tick / 4;
                canvas.Children.Add(new Line { X1 = x, X2 = x,
                    Y1 = top, Y2 = bottom, Stroke = Border, StrokeThickness = 1,
                    Opacity = .55 });
            }
            void Series(bool playerOne, Brush color)
            {
                var line = new Polyline { Stroke = color, StrokeThickness = 2 };
                var first = LowerBound(battleIndices, start);
                var limit = LowerBound(battleIndices, end + 1);
                var count = limit - first;
                var step = Math.Max(1, count / 1200);
                for (var index = first; index < limit; index += step)
                {
                    var value = playerOne ? values[index].P1 : values[index].P2;
                    line.Points.Add(new Point(left + (battleIndices[index] - start) * horizontalScale,
                        bottom - Math.Clamp(value, 0, 1) * (bottom - top)));
                }
                if (count > 1 && (count - 1) % step != 0)
                {
                    var last = limit - 1;
                    var value = playerOne ? values[last].P1 : values[last].P2;
                    line.Points.Add(new Point(left + (battleIndices[last] - start) * horizontalScale,
                        bottom - Math.Clamp(value, 0, 1) * (bottom - top)));
                }
                canvas.Children.Add(line);
            }
            Series(true, Blue);
            Series(false, Red);
            DrawCurveRounds(canvas, rounds, ordinals.Length, left, width, top, bottom);
            DrawCurveTimeAxis(canvas, ordinals.Length, left, width, bottom);
            if (cards is null) return;
            List<List<double>> occupied = [[], [], [], []];
            var lastLane = 0;
            foreach (var card in cardUses)
            {
                var index = BattleIndex(ordinals, card.Ordinal);
                if (index < start || index > end) continue;
                var x = CurveX(index, ordinals.Length, left, width);
                var peak = bottom - Math.Clamp((card.SlotsBefore ?? card.Slots) / 5, 0, 1) *
                    (bottom - top);
                var iconX = x;
                var lane = 0;
                var placed = false;
                for (var distance = 0; distance < 20 && !placed; distance++)
                {
                    foreach (var direction in distance == 0 ? new[] { 0 } : new[] { -1, 1 })
                    {
                        var candidate = Math.Clamp(x + direction * distance *
                            Math.Max(16, iconWidth / 2),
                            left + iconWidth / 2.0, left + width - iconWidth / 2.0);
                        for (var row = 0; row < occupied.Count; row++)
                        {
                            if (occupied[row].Any(prior =>
                                Math.Abs(candidate - prior) < iconWidth + 5))
                                continue;
                            iconX = candidate;
                            lane = row;
                            placed = true;
                            break;
                        }
                        if (placed) break;
                    }
                }
                if (!placed)
                {
                    lane = occupied.Count;
                    occupied.Add([]);
                    iconX = Math.Clamp(x, left + iconWidth / 2.0,
                        left + width - iconWidth / 2.0);
                }
                occupied[lane].Add(iconX);
                lastLane = Math.Max(lastLane, lane);
                var laneTop = 218 + lane * laneStride;
                var color = card.Side == 0 ? Blue : Red;
                var hint = $"对战 {Time(index)} · 回放 {Time(card.Ordinal)} · " +
                    $"{card.Side + 1}P 使用 {CardTimeline.Name(card)}";
                canvas.Children.Add(new Line { X1 = x, X2 = iconX, Y1 = peak + 4,
                    Y2 = laneTop - 2, Stroke = color, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection([3, 3]), ToolTip = hint });
                var dot = new Ellipse { Width = 6, Height = 6, Fill = color, ToolTip = hint };
                Canvas.SetLeft(dot, x - 3); Canvas.SetTop(dot, peak - 3);
                canvas.Children.Add(dot);
                var art = CardArt.Get(card.Character, card.CardId);
                if (art is not null)
                {
                    var image = new Image { Source = art, Width = iconWidth,
                        Height = iconHeight, Stretch = Stretch.Uniform, ToolTip = hint };
                    Canvas.SetLeft(image, iconX - iconWidth / 2.0);
                    Canvas.SetTop(image, laneTop);
                    canvas.Children.Add(image);
                }
                else
                {
                    var fallback = new Border {
                        Width = iconWidth, Height = iconHeight,
                        BorderBrush = color, BorderThickness = new Thickness(1),
                        Child = Text(card.CardId.ToString(), 11, color, true),
                        ToolTip = hint
                    };
                    Canvas.SetLeft(fallback, iconX - iconWidth / 2.0);
                    Canvas.SetTop(fallback, laneTop);
                    canvas.Children.Add(fallback);
                }
            }
            canvas.Height = 218 + (lastLane + 1) * laneStride + 12;
        }
        RegisterCurve(canvas, Draw);
        canvas.MouseLeftButtonDown += (_, e) => {
            var width = Math.Max(1, canvas.ActualWidth - left - right);
            var fraction = Math.Clamp((e.GetPosition(canvas).X - left) / width, 0, 1);
            var (start, end) = CurveWindow(ordinals.Length);
            Jump(ordinals[start + (int)Math.Round((end - start) * fraction)]);
        };
        return canvas;
    }

    private Canvas TimelineChart(IReadOnlyList<CurveSegment> segments,
        string[] rows, int[] ordinals, RoundMark[] rounds, bool showStateLegend)
    {
        var canvas = new Canvas { Height = 44 + rows.Length * 44 +
                (showStateLegend ? 22 : 0), Background = Surface,
            Cursor = Cursors.Hand, ToolTip = "点击时间轴可定位回放" };
        void Draw()
        {
            canvas.Children.Clear();
            var width = Math.Max(180, canvas.ActualWidth - 54 - 16);
            const double left = 54;
            var (start, end) = CurveWindow(ordinals.Length);
            for (var row = 0; row < rows.Length; row++)
            {
                var top = 21 + row * 44;
                var title = Text(rows[row], 12, Muted, true);
                Canvas.SetLeft(title, 6); Canvas.SetTop(title, top + 7);
                canvas.Children.Add(title);
                var background = new Rectangle { Width = width, Height = 27,
                    Fill = palette.Alternating };
                Canvas.SetLeft(background, left); Canvas.SetTop(background, top);
                canvas.Children.Add(background);
            }
            foreach (var segment in segments)
            {
                if (segment.End < start || segment.Start > end) continue;
                var first = Math.Max(start, segment.Start);
                var last = Math.Min(end, segment.End);
                var x = CurveX(first, ordinals.Length, left, width);
                var x2 = CurveX(Math.Min(end, last + 1), ordinals.Length, left, width);
                var span = Math.Max(1, x2 - x);
                var top = 21 + segment.Row * 44;
                var rectangle = new Rectangle { Width = span, Height = 27,
                    Fill = segment.Color, ToolTip = $"{rows[segment.Row]} · {segment.Label} · " +
                    $"对战 {Time(segment.Start)}—{Time(segment.End)}" };
                Canvas.SetLeft(rectangle, x); Canvas.SetTop(rectangle, top);
                canvas.Children.Add(rectangle);
                if (span < 48) continue;
                var label = Text(segment.Label, 10, palette["ChartLabel"], true);
                label.MaxWidth = span - 5;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                Canvas.SetLeft(label, x + 4); Canvas.SetTop(label, top + 5);
                canvas.Children.Add(label);
            }
            DrawCurveRounds(canvas, rounds, ordinals.Length, left, width, 21,
                21 + rows.Length * 44 - 17);
            DrawCurveTimeAxis(canvas, ordinals.Length, left, width,
                21 + rows.Length * 44 - 9);
            if (!showStateLegend) return;
            var legendTop = 44 + rows.Length * 44;
            foreach (var (kind, position) in new[] { ("地面", 0), ("空中", 1), ("倒地", 2) })
            {
                var swatch = new Rectangle { Width = 11, Height = 11,
                    Fill = StateColor(kind) };
                Canvas.SetLeft(swatch, left + position * 68);
                Canvas.SetTop(swatch, legendTop);
                canvas.Children.Add(swatch);
                var label = Text(kind, 10, Muted);
                Canvas.SetLeft(label, left + position * 68 + 15);
                Canvas.SetTop(label, legendTop - 2);
                canvas.Children.Add(label);
            }
        }
        RegisterCurve(canvas, Draw);
        canvas.MouseLeftButtonDown += (_, e) => {
            var fraction = Math.Clamp((e.GetPosition(canvas).X - 54) /
                Math.Max(1, canvas.ActualWidth - 70), 0, 1);
            var (start, end) = CurveWindow(ordinals.Length);
            Jump(ordinals[start + (int)Math.Round((end - start) * fraction)]);
        };
        return canvas;
    }

    private static Brush StateColor(string state) => palette[state switch {
        "空中" => "StateAir", "倒地" => "StateDown", _ => "StateGround"
    }];

    private static Brush WeatherColor(int weather) => palette[$"Weather{(weather is >= 0 and <= 20 ? weather : 21)}"];
}
