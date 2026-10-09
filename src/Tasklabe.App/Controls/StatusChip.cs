using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>状態の重さ。色とアイコンの既定を決める。</summary>
public enum ChipSeverity
{
    /// <summary>注意（日程の食い違い、権限の不足など）。</summary>
    Caution,

    /// <summary>失敗（送信できなかった変更など）。</summary>
    Critical,
}

/// <summary>詳細の中に置く操作。Primary は強調したボタンにする。</summary>
public sealed record ChipAction(string Label, Action Run, bool Primary = false);

/// <summary>詳細の 1 区切り（見出し、説明、項目、操作）。</summary>
public sealed record ChipSection(ChipSeverity Severity, string Title, string Message, IReadOnlyList<string> Details, IReadOnlyList<ChipAction> Actions);

/// <summary>
/// 続いている状態の知らせ（UX 規約 UX-31）。見出しの行に小さな丸いチップとして置き、押すと詳細と操作を開く。
/// 内容の上に帯を差し込まないため、出ても消えても画面の高さは変わらない。
/// </summary>
public sealed class StatusChip : Button
{
    private readonly FontIcon _icon = new() { FontSize = 12 };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout _flyout = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private IReadOnlyList<ChipSection> _sections = [];

    public StatusChip()
    {
        // 色の付いた背景の上でも読めるよう、文字は本文の色にする（キャプションの淡い色では、コントラストが足りない）
        _text.Style = AppResources.Style("Text.Caption");
        _text.Foreground = ThemeResources.Brush("TextFillColorPrimaryBrush");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(_icon);
        content.Children.Add(_text);
        Content = content;
        Pill.SetIsEnabled(this, true);
        Padding = new Thickness(10, 4, 10, 4);
        MinHeight = 0;
        BorderThickness = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        Click += (_, _) => OpenDetails();
    }

    /// <summary>チップを出す。text が null なら、アイコンだけの小さな形にする（見送った状態など）。</summary>
    public void Show(string glyph, string? text, string name, params ChipSection[] sections)
    {
        _sections = sections;
        var severity = sections.Length > 0 && sections.Any(s => s.Severity == ChipSeverity.Critical) ? ChipSeverity.Critical : ChipSeverity.Caution;
        _icon.Glyph = glyph;
        _icon.Foreground = ThemeResources.Brush(severity == ChipSeverity.Critical ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
        _text.Text = text ?? "";
        _text.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Background = ThemeResources.Brush(severity == ChipSeverity.Critical ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorCautionBackgroundBrush");
        AutomationProperties.SetName(this, $"{name}。押すと詳しく表示します");
        ToolTipService.SetToolTip(this, name);
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        _flyout.Hide();
        Visibility = Visibility.Collapsed;
    }

    public bool IsShown => Visibility == Visibility.Visible;

    /// <summary>詳細を開く（チップを押したとき、コマンドパレットから）。</summary>
    public void OpenDetails()
    {
        if (_sections.Count == 0)
        {
            return;
        }

        _flyout.Content = Details(_sections, _flyout.Hide);
        _flyout.ShowAt(this);
    }

    /// <summary>詳細の中身。タイトルバーの同期の状態など、チップ以外から開く詳細にも使う。</summary>
    /// <param name="close">操作を選んだときに、詳細を閉じる。</param>
    public static FrameworkElement Details(IReadOnlyList<ChipSection> sections, Action close)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var caption = AppResources.Style("Text.Caption");
        var root = new StackPanel { Width = 360, Spacing = 16 };
        foreach (var section in sections)
        {
            var panel = new StackPanel { Spacing = 8 };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new FontIcon
            {
                Glyph = section.Severity == ChipSeverity.Critical ? "\uEA39" : "\uE7BA",
                FontSize = 14,
                Foreground = ThemeResources.Brush(section.Severity == ChipSeverity.Critical ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var title = new TextBlock { Text = section.Title, Style = AppResources.Style("Text.BodyStrong"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            heading.Children.Add(title);
            panel.Children.Add(heading);
            panel.Children.Add(new TextBlock { Text = section.Message, TextWrapping = TextWrapping.Wrap });
            foreach (var detail in section.Details)
            {
                panel.Children.Add(new TextBlock { Text = "・" + detail, Style = caption, TextWrapping = TextWrapping.Wrap });
            }

            if (section.Actions.Count > 0)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
                foreach (var action in section.Actions)
                {
                    var button = new Button { Content = action.Label };
                    if (action.Primary)
                    {
                        button.Style = AppResources.Style("AccentButtonStyle");
                    }

                    button.Click += (_, _) =>
                    {
                        close();
                        action.Run();
                    };
                    actions.Children.Add(button);
                }

                panel.Children.Add(actions);
            }

            root.Children.Add(panel);
        }

        return root;
    }
}
