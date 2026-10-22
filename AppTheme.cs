using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;

namespace SokuReplayLab;

public sealed record ColorToken(string Key, string Label, string Group, string Usage);

/// <summary>Semantic colors shared by existing controls, including parked replay panels.</summary>
public sealed class AppTheme
{
    private sealed class ColorValue : INotifyPropertyChanged
    {
        private Color color;
        public Color Color { get => color; set { color = value; PropertyChanged?.Invoke(this, new(nameof(Color))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public static IReadOnlyList<ColorToken> Tokens { get; } = [
        new("Canvas", "页面背景", "界面", "主窗口和页面底色"),
        new("Surface", "卡片与控件", "界面", "卡片、工具栏、输入框和下拉菜单背景"),
        new("Alternating", "次级背景", "界面", "表头、交替行、悬停和滑轨背景"),
        new("Border", "边框与分隔", "界面", "边框、表格线和输入框轮廓"),
        new("Text", "正文", "文字", "正文、表格和控件文字"),
        new("Strong", "标题", "文字", "页面标题和重要数值"),
        new("Muted", "辅助文字", "文字", "说明、坐标、时间和次要文字"),
        new("Accent", "强调与 1P", "交互与提示", "主按钮、选中项、链接和 1P 曲线"),
        new("OnAccent", "强调色上的文字", "交互与提示", "主按钮和选中项的文字"),
        new("Danger", "危险与 2P", "交互与提示", "受击、失败和 2P 曲线"),
        new("Warning", "警告", "交互与提示", "连接状态、异常行和台风提示"),
        new("Notice", "提示背景", "交互与提示", "顶部提示条背景"),
        new("NoticeText", "提示文字", "交互与提示", "顶部提示条文字"),
        new("Navy", "侧栏与面板标题", "侧栏与回放", "导航侧栏和可拖动面板标题背景"),
        new("SidebarText", "侧栏文字", "侧栏与回放", "侧栏标题及未选中导航文字"),
        new("SidebarMuted", "侧栏辅助文字", "侧栏与回放", "侧栏分组与说明"),
        new("SidebarAccent", "侧栏点缀", "侧栏与回放", "品牌文字和导航图标"),
        new("Panel", "画面区域背景", "侧栏与回放", "游戏画面占位区域背景"),
        new("PanelRaised", "手牌区域背景", "侧栏与回放", "画面两侧手牌区域背景"),
        new("PanelText", "画面区域文字", "侧栏与回放", "画面区域标题"),
        new("PanelMuted", "画面辅助文字", "侧栏与回放", "画面占位说明"),
        new("ChartLabel", "图段文字", "曲线", "天气及状态色块上的文字"),
        new("RoundMarker", "回合分界", "曲线", "曲线回合分界线与标题"),
        new("StateAir", "空中状态", "曲线", "空中状态色块"),
        new("StateDown", "倒地状态", "曲线", "倒地状态色块"),
        new("StateGround", "地面状态", "曲线", "地面状态色块"),
        .. Enumerable.Range(0, 22).Select(i => new ColorToken($"Weather{i}",
            new[] { "快晴", "雾雨", "昙天", "苍天", "冰雹", "花昙", "浓雾", "雪", "天気雨", "疏雨", "风雨", "晴岚", "川雾", "台风", "平静", "钻石尘", "黄砂", "烈日", "梅雨", "极光", "黄昏", "无天气／未知" }[i],
            "天气曲线", "对应天气色块"))
    ];
    private static readonly string[] BaseKeys = ["Navy", "Accent", "Canvas", "Surface", "Border", "Muted", "Text", "Strong", "Alternating", "Panel", "Danger", "Notice", "NoticeText"];
    private static readonly Dictionary<string, string[]> Presets = new()
    {
        ["经典"] = ["#132238", "#2765D7", "#F3F6FA", "#FFFFFF", "#DFE7F1", "#66778C", "#253852", "#14243B", "#F3F7FD", "#14243B", "#C74657", "#FFF7E7", "#876523"],
        ["深色"] = ["#0B1525", "#86ABFF", "#111A29", "#1B293B", "#40536D", "#ADBBD0", "#E5EDF7", "#F6F9FF", "#25374E", "#0E1A2B", "#FF9CA7", "#3B3020", "#FFE0A4"],
        ["海盐"] = ["#123C4B", "#08748A", "#EDF6F7", "#FBFEFF", "#C7DFE4", "#536F79", "#25434C", "#173841", "#E5F1F3", "#102E38", "#BC425D", "#FFF4DF", "#805C20"],
        ["竹青"] = ["#1D382C", "#267447", "#F0F5EF", "#FCFEFA", "#CDDCCD", "#5C725F", "#2D4534", "#1C3725", "#E7EFE4", "#172E24", "#BC4653", "#FBF2DC", "#785D24"],
        ["暖纸"] = ["#433329", "#92502C", "#F7F1E7", "#FFFBF4", "#E2D5C2", "#7B6958", "#4F3D2E", "#35281E", "#F0E7D8", "#30261E", "#B6454E", "#F9EAC7", "#795421"],
        ["暮紫"] = ["#211A32", "#C5ADFF", "#1A1723", "#282333", "#50445F", "#B8ABC8", "#EEE7F7", "#FCF6FF", "#352E43", "#17131F", "#FF9AAE", "#403225", "#FFE1AE"]
    };
    public static IEnumerable<string> PresetNames => Presets.Keys;
    public static bool IsPreset(string? name) => name is not null && Presets.ContainsKey(name);
    public static bool TryColor(string? text, out Color color)
    {
        color = default;
        if (text is null || text.Length != 7 || text[0] != '#' ||
            !text.Skip(1).All(Uri.IsHexDigit)) return false;
        color = (Color)ColorConverter.ConvertFromString(text);
        return true;
    }
    public static Dictionary<string, string> ColorsFor(string name)
    {
        if (!IsPreset(name)) name = "经典";
        var colors = BaseKeys.Zip(Presets[name]).ToDictionary(x => x.First, x => x.Second);
        var dark = name is "深色" or "暮紫";
        foreach (var (key, value) in new Dictionary<string, string> {
            ["OnAccent"] = dark ? "#172033" : "#FFFFFF", ["Warning"] = dark ? "#F4C37F" : "#97601B",
            ["SidebarText"] = "#F2F6FC", ["SidebarMuted"] = "#B4C6D8", ["SidebarAccent"] = "#A5CEFF",
            ["PanelRaised"] = colors["Navy"], ["PanelText"] = "#F2F6FC", ["PanelMuted"] = "#B4C6D8",
            ["ChartLabel"] = "#111827", ["RoundMarker"] = dark ? "#DCA870" : "#90572D",
            ["StateAir"] = "#85ABED", ["StateDown"] = "#E38A8A", ["StateGround"] = "#92BE9F"
        }) colors[key] = value;
        string[] weather = ["#E4B66D", "#91A9DD", "#B6A1D3", "#8DBCE4", "#78B4C1", "#CF9DCE", "#9AABBB", "#9DBAC9", "#A9B8D1", "#D3AD76", "#8CBE96", "#BCA2D3", "#92B4C5", "#E28A92", "#9BCCAB", "#A5B9C5", "#CAA66A", "#E7AB78", "#91B5D7", "#C3A2D5", "#ABA0CF", "#A1B3C7"];
        for (var i = 0; i < weather.Length; i++) colors[$"Weather{i}"] = weather[i];
        return colors;
    }
    private readonly Dictionary<string, (ColorValue Value, SolidColorBrush Brush)> colors = [];
    public AppTheme()
    {
        foreach (var token in Tokens)
        {
            var value = new ColorValue();
            var brush = new SolidColorBrush();
            // Binding keeps shared brushes mutable when WPF seals table styles/templates.
            BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(ColorValue.Color)) { Source = value });
            colors[token.Key] = (value, brush);
        }
        Apply("经典", null);
    }
    public Brush this[string key] => colors[key].Brush;
    public string Hex(string key) => $"#{colors[key].Value.Color.R:X2}{colors[key].Value.Color.G:X2}{colors[key].Value.Color.B:X2}";
    public void Apply(string preset, IReadOnlyDictionary<string, string>? overrides)
    {
        var resolved = ColorsFor(preset);
        foreach (var token in Tokens)
        {
            var text = overrides?.GetValueOrDefault(token.Key);
            if (!TryColor(text, out var color)) TryColor(resolved[token.Key], out color);
            colors[token.Key].Value.Color = color;
        }
    }
    public Brush Navy => this["Navy"];
    public Brush Accent => this["Accent"];
    public Brush Canvas => this["Canvas"];
    public Brush Surface => this["Surface"];
    public Brush Border => this["Border"];
    public Brush Muted => this["Muted"];
    public Brush Text => this["Text"];
    public Brush Strong => this["Strong"];
    public Brush Alternating => this["Alternating"];
    public Brush Panel => this["Panel"];
    public Brush Danger => this["Danger"];
    public Brush Notice => this["Notice"];
    public Brush NoticeText => this["NoticeText"];
}
