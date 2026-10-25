using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    private Key exitTakeoverKey = Key.Escape;
    private string ExitTakeoverKeyLabel => ShortcutKeyLabel(exitTakeoverKey);

    private static bool IsExitTakeoverKey(Key key) => Enum.IsDefined(key) &&
        key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or
            Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) &&
        KeyInterop.VirtualKeyFromKey(key) is > 0 and < 256;

    private static string ShortcutKeyLabel(Key key) => key switch {
        Key.Escape => "Esc", Key.Return => "Enter", Key.Back => "Backspace",
        Key.Space => "Space", Key.Prior => "Page Up", Key.Next => "Page Down",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        _ => new KeyConverter().ConvertToString(key) ?? key.ToString()
    };

    private void SetExitTakeoverKey(Key key)
    {
        if (!IsExitTakeoverKey(key)) throw new ArgumentException("请选择一个有效的单键。");
        exitTakeoverKey = key;
        SaveSettings();
    }

    private void ShortcutSettingsPage()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
        row.Children.Add(Text("退出接管", 14, TextColor, true));
        var current = Text(ExitTakeoverKeyLabel, 14, Blue, true);
        current.MinWidth = 85;
        current.Margin = new Thickness(22, 0, 12, 0);
        row.Children.Add(current);
        row.Children.Add(Button("更改按键", EditExitTakeoverKey));
        row.Children.Add(Button("恢复 Esc", () => { SetExitTakeoverKey(Key.Escape); Render(); }));
        content.Children.Add(Card("快捷键", Vertical(row,
            Text("只在接管游戏时生效，按下后收回键盘，并恢复接管前 REP 的位置、保持暂停。支持单个按键，更改会自动保存。", 12, Muted),
            Text("接管期间仅保留这里设置的退出键，其余按键交给游戏及 MOD 处理；不会修改游戏键位。", 12, Muted))));
    }

    private void EditExitTakeoverKey()
    {
        var selected = exitTakeoverKey;
        var dialog = new Window { Owner = this, Title = "更改退出接管按键", Width = 460,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = Light, Foreground = TextColor, FontFamily = FontFamily };
        foreach (var token in AppTheme.Tokens) dialog.Resources[$"Theme.{token.Key}"] = palette[token.Key];
        foreach (var dictionary in Resources.MergedDictionaries) dialog.Resources.MergedDictionaries.Add(dictionary);
        dialog.SourceInitialized += (_, _) => UpdateWindowChrome(dialog);
        var capture = new TextBox { Text = ShortcutKeyLabel(selected), IsReadOnly = true,
            FontSize = 22, HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 10), ToolTip = "按下要用作退出接管的单个按键" };
        var error = Text("", 12, Red);
        dialog.PreviewKeyDown += (_, e) => {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.Modifiers != ModifierKeys.None || !IsExitTakeoverKey(key))
            {
                error.Text = "请选择单个按键，不使用 Ctrl、Alt、Shift 或 Win 组合。";
                return;
            }
            selected = key;
            capture.Text = ShortcutKeyLabel(selected);
            error.Text = "";
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("保存", () => dialog.DialogResult = true, true));
        buttons.Children.Add(Button("取消", () => dialog.DialogResult = false));
        var body = Vertical(Text("按下新的退出接管按键", 16, Strong, true), capture,
            Text("Esc 也可以设置为退出键；点击“取消”放弃修改。", 12, Muted), error, buttons);
        body.Margin = new Thickness(22);
        dialog.Content = body;
        dialog.Loaded += (_, _) => { capture.Focus(); Keyboard.Focus(capture); };
        if (dialog.ShowDialog() == true) { SetExitTakeoverKey(selected); Render(); }
    }
}
