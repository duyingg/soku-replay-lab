using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Dictionary<string, string>> customColors = [];

    private void ConfigureThemeResources()
    {
        foreach (var token in AppTheme.Tokens) Resources[$"Theme.{token.Key}"] = palette[token.Key];
        foreach (var (key, token) in new (ResourceKey, string)[] {
            (SystemColors.WindowBrushKey, "Surface"), (SystemColors.WindowTextBrushKey, "Text"),
            (SystemColors.ControlBrushKey, "Surface"), (SystemColors.ControlTextBrushKey, "Text"),
            (SystemColors.ControlLightBrushKey, "Alternating"), (SystemColors.ControlDarkBrushKey, "Border"),
            (SystemColors.HighlightBrushKey, "Accent"), (SystemColors.HighlightTextBrushKey, "OnAccent"),
            (SystemColors.InactiveSelectionHighlightBrushKey, "Alternating"),
            (SystemColors.InactiveSelectionHighlightTextBrushKey, "Text"),
            (SystemColors.GrayTextBrushKey, "Muted"), (SystemColors.InfoBrushKey, "Surface"),
            (SystemColors.InfoTextBrushKey, "Text"), (SystemColors.ScrollBarBrushKey, "Alternating")
        }) Resources[key] = palette[token];
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("SokuReplayLab.ThemeControls.xaml")
            ?? throw new InvalidDataException("缺少皮肤控件资源。");
        Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        Foreground = TextColor;
        SourceInitialized += (_, _) => UpdateWindowChrome(this);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void UpdateWindowChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var background = ((SolidColorBrush)Surface).Color;
        var foreground = ((SolidColorBrush)TextColor).Color;
        var dark = background.R * .299 + background.G * .587 + background.B * .114 < 128 ? 1 : 0;
        var caption = background.R | background.G << 8 | background.B << 16;
        var text = foreground.R | foreground.G << 8 | foreground.B << 16;
        // Unsupported attributes return an HRESULT on older Windows versions.
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
    }

    private void SetTokenColor(string key, string hex)
    {
        if (!AppTheme.TryColor(hex, out _) || !AppTheme.Tokens.Any(t => t.Key == key)) return;
        if (!customColors.TryGetValue(skin, out var overrides)) customColors[skin] = overrides = [];
        if (string.Equals(hex, AppTheme.ColorsFor(skin)[key], StringComparison.OrdinalIgnoreCase)) overrides.Remove(key);
        else overrides[key] = hex.ToUpperInvariant();
        if (overrides.Count == 0) customColors.Remove(skin);
        palette.Apply(skin, customColors.GetValueOrDefault(skin));
        UpdateWindowChrome(this);
        SaveSettings();
    }

    private void SkinSettingsPage()
    {
        var presets = new WrapPanel();
        foreach (var name in AppTheme.PresetNames)
        {
            var selected = name;
            var button = Button(name + (customColors.GetValueOrDefault(name)?.Count > 0 ? " · 自定义" : ""),
                () => ApplySkin(selected), skin == name);
            button.Margin = new Thickness(0, 0, 8, 8);
            presets.Children.Add(button);
        }
        content.Children.Add(Card("皮肤预设", Vertical(presets,
            Text("经典、海盐、竹青、暖纸为浅色；深色、暮紫为深色。每套预设单独保存自定义颜色。", 12, Muted))));
        var reset = Button("恢复当前预设颜色", () => {
            customColors.Remove(skin);
            ApplySkin(skin);
        });
        content.Children.Add(Card("用途 → 颜色", Vertical(
            Text($"当前：{skin}。点击色块选色，或输入 #RRGGBB 后点击应用；修改立即生效并自动保存。", 12, Muted),
            Text("令牌会同步影响所有使用该颜色的组件；“恢复”只还原这一项。", 12, Muted), reset)));
        foreach (var group in AppTheme.Tokens.GroupBy(t => t.Group))
        {
            var rows = new StackPanel();
            foreach (var token in group)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = Vertical(Text(token.Label, 13, TextColor, true),
                    Text($"{token.Key} · {token.Usage}", 11, Muted));
                row.Children.Add(label);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var input = new TextBox { Text = palette.Hex(token.Key), Width = 96, MaxLength = 7,
                    ToolTip = "颜色格式：#RRGGBB", VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0), FontFamily = new FontFamily("Consolas") };
                var status = Text("", 11, Red);
                var swatch = Button("", () => {
                    var picked = PickTokenColor(token.Label, palette.Hex(token.Key));
                    if (picked is null) return;
                    SetTokenColor(token.Key, picked);
                    input.Text = palette.Hex(token.Key);
                    status.Text = "";
                });
                swatch.Width = 38;
                swatch.Background = palette[token.Key];
                swatch.ToolTip = $"选择{token.Label}颜色";
                actions.Children.Add(swatch);
                actions.Children.Add(input);
                actions.Children.Add(Button("应用", () => {
                    if (!AppTheme.TryColor(input.Text.Trim(), out _))
                    {
                        status.Text = "请输入 #RRGGBB";
                        input.Focus();
                        return;
                    }
                    SetTokenColor(token.Key, input.Text.Trim());
                    input.Text = palette.Hex(token.Key);
                    status.Text = "";
                }));
                actions.Children.Add(Button("恢复", () => {
                    SetTokenColor(token.Key, AppTheme.ColorsFor(skin)[token.Key]);
                    input.Text = palette.Hex(token.Key);
                    status.Text = "";
                }));
                Grid.SetColumn(actions, 1); row.Children.Add(actions);
                rows.Children.Add(Vertical(row, status));
            }
            var expander = new Expander { Header = $"{group.Key} · {group.Count()} 项", Content = rows,
                IsExpanded = group.Key != "天气曲线" };
            content.Children.Add(Box(expander));
        }
    }

    private string? PickTokenColor(string label, string initial)
    {
        AppTheme.TryColor(initial, out var original);
        var preview = new SolidColorBrush(original);
        var dialog = new Window { Title = $"选色 · {label}", Owner = this, Width = 460,
            SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40),
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Light, Foreground = TextColor, FontFamily = FontFamily };
        // Owned windows do not inherit the owner's resource dictionary.
        foreach (var token in AppTheme.Tokens) dialog.Resources[$"Theme.{token.Key}"] = palette[token.Key];
        foreach (var dictionary in Resources.MergedDictionaries) dialog.Resources.MergedDictionaries.Add(dictionary);
        var body = new StackPanel { Margin = new Thickness(22) };
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.SourceInitialized += (_, _) => UpdateWindowChrome(dialog);
        body.Children.Add(new Border { Height = 65, Background = preview, BorderBrush = Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12) });
        var hex = new TextBox { Text = initial, MaxLength = 7, Margin = new Thickness(0, 0, 0, 10) };
        body.Children.Add(hex);
        var error = Text("", 12, Red);
        var syncing = false;
        var channels = new List<Slider>();
        foreach (var (name, value) in new[] { ("红 R", original.R), ("绿 G", original.G), ("蓝 B", original.B) })
        {
            var slider = new Slider { Minimum = 0, Maximum = 255, Value = value, TickFrequency = 1,
                IsSnapToTickEnabled = true, Width = 255, Margin = new Thickness(8, 0, 0, 0) };
            channels.Add(slider);
            var valueLabel = Text(value.ToString(), 12, Muted); valueLabel.Width = 30;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            line.Children.Add(Text(name, 12)); line.Children.Add(slider); line.Children.Add(valueLabel);
            body.Children.Add(line);
            slider.ValueChanged += (_, _) => {
                valueLabel.Text = ((int)slider.Value).ToString();
                if (syncing || channels.Count != 3) return;
                preview.Color = Color.FromRgb((byte)channels[0].Value, (byte)channels[1].Value, (byte)channels[2].Value);
                syncing = true;
                hex.Text = $"#{preview.Color.R:X2}{preview.Color.G:X2}{preview.Color.B:X2}";
                syncing = false;
                error.Text = "";
            };
        }
        hex.TextChanged += (_, _) => {
            if (syncing) return;
            if (!AppTheme.TryColor(hex.Text, out var color)) { error.Text = "请输入 #RRGGBB"; return; }
            syncing = true;
            preview.Color = color;
            channels[0].Value = color.R; channels[1].Value = color.G; channels[2].Value = color.B;
            syncing = false;
            error.Text = "";
        };
        body.Children.Add(error);
        string? result = null;
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("应用颜色", () => {
            if (!AppTheme.TryColor(hex.Text, out _)) { error.Text = "请输入 #RRGGBB"; return; }
            result = hex.Text.ToUpperInvariant(); dialog.DialogResult = true;
        }, true));
        buttons.Children.Add(Button("取消", () => dialog.DialogResult = false));
        body.Children.Add(buttons);
        dialog.ShowDialog();
        return result;
    }
}
