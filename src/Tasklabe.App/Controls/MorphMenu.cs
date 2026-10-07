using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Animation;
using Tasklabe.App.Services;
using Windows.Foundation;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>メニューの項目。</summary>
internal abstract record MenuEntry;

/// <summary>押すと操作を行う項目。</summary>
/// <param name="Glyph">先頭のアイコン（Segoe Fluent Icons の文字）。</param>
internal sealed record MenuCommand(string Text, string? Glyph, Func<Task> Invoke) : MenuEntry
{
    /// <summary>同じ操作のキー（UX 規約 UX-17）。行の右端に示す。</summary>
    public string? Accelerator { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>いまの値・入っている切り替え。行の末尾のチェックで示す。</summary>
    public bool IsChecked { get; init; }

    /// <summary>取り消せない操作（削除など）。文字と印を警告の色にする。</summary>
    public bool IsDestructive { get; init; }

    /// <summary>アイコンの色のリソース名（ステータスのカテゴリなど）。</summary>
    public string? GlyphBrushKey { get; init; }

    /// <summary>アイコンのフォント（Segoe Fluent Icons にない記号を使うとき）。</summary>
    public string? GlyphFontFamily { get; init; }

    public string? ToolTip { get; init; }
}

/// <summary>押すと、同じ面の中身が子の項目に入れ替わる項目（ステータスなど）。</summary>
internal sealed record MenuSubmenu(string Text, string? Glyph, IReadOnlyList<MenuEntry> Items) : MenuEntry
{
    public bool IsEnabled { get; init; } = true;

    public string? GlyphBrushKey { get; init; }
}

/// <summary>項目の区切り。</summary>
internal sealed record MenuSeparator : MenuEntry
{
    public static readonly MenuSeparator Instance = new();
}

/// <summary>押せない見出し（「3 件を選択中」など）。</summary>
internal sealed record MenuHeader(string Text) : MenuEntry;

/// <summary>
/// 起点（右クリックした位置、行、押したボタン）から面が育って開くメニュー（UI デザイン設計書 4.1.2 節、UX 規約 UX-20）。
/// 面・影・開き方・閉じ方は候補から選ぶピッカーと同じ <see cref="MorphPopup"/> とし、行は少しずつずれて現れる。
/// いまいる行は、一覧の下に敷いた印が滑って示す（ポインターでは滑り、キーではその場に移る）。
/// サブメニューは別の面を開かず、同じ面の中身を入れ替え、面が新しい大きさへ伸び縮みする。
/// </summary>
internal sealed class MorphMenu
{
    private const double MinWidth = 208;
    private const double MaxWidth = 400;

    /// <summary>入れ替わった中身が、進む向きからずれて現れる距離。</summary>
    private const float PageShift = 8;

    private static readonly TimeSpan MarkFade = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan RowStagger = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan PageIn = TimeSpan.FromMilliseconds(240);

    private readonly IReadOnlyList<MenuEntry> _items;
    private readonly Grid _content = new() { MinWidth = MinWidth, MaxWidth = MaxWidth, Padding = new Thickness(4) };
    private readonly Grid _page = new();
    private readonly StackPanel _rows = new();
    private readonly Border _mark;
    private readonly ScrollViewer _scroll;
    private readonly Brush _markBrush = ThemeResources.Brush("Menu.Highlight");
    private readonly Brush _dangerMarkBrush;

    /// <summary>いま並べている、フォーカスを受ける行。</summary>
    private readonly List<(Button Button, MenuEntry Entry)> _shown = [];

    /// <summary>開いているサブメニューまでの道筋（親の項目と、そのサブメニューの項目）。</summary>
    private readonly Stack<(IReadOnlyList<MenuEntry> Items, MenuSubmenu Submenu)> _path = [];

    private MorphPopup? _morph;
    private bool _markShown;
    private bool _live;

    /// <param name="name">読み上げに使うメニューの名前。</param>
    public MorphMenu(string name, IReadOnlyList<MenuEntry> items)
    {
        _items = items;

        var danger = ((SolidColorBrush)ThemeResources.Brush("Viz.Delay")).Color;
        _dangerMarkBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, danger.R, danger.G, danger.B));
        _mark = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = AppResources.CornerRadius("Radius.Control"),
            Background = _markBrush,
            IsHitTestVisible = false,
        };
        Motion.VisualOf(_mark).Opacity = 0;
        _page.Children.Add(_mark);
        _page.Children.Add(_rows);
        _scroll = new ScrollViewer { Content = _page };
        _scroll.PointerExited += (_, _) => HideMark();
        _content.Children.Add(_scroll);
        _content.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        AutomationProperties.SetName(_content, name);
    }

    /// <summary>メニューを開く。</summary>
    /// <param name="anchor">起点（行、押したボタン）。閉じたあとのフォーカスの戻り先にも使う。</param>
    /// <param name="position">右クリックした位置（anchor の中の座標）。null なら anchor から開く（Shift + F10、ボタン）。</param>
    public void Show(FrameworkElement anchor, Point? position)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        _scroll.MaxHeight = Math.Max(160, anchor.XamlRoot.Size.Height - 24);
        BuildPage(_items, null);
        _morph = new MorphPopup(anchor, position, _content) { FaceStagger = RowStagger, Back = GoBack };
        _morph.Faces.AddRange(_rows.Children);
        _morph.Dismissed += (_, _) => Close(focusNow: false);
        _morph.Grown += (_, _) =>
        {
            _live = true;
            FocusInitial(null);
        };
        _morph.Open();
    }

    // ---------------------------------------------------------------- 並べる

    /// <summary>項目を並べ直す。サブメニューの中なら、先頭に戻る行を置く。</summary>
    private void BuildPage(IReadOnlyList<MenuEntry> items, MenuSubmenu? parent)
    {
        _rows.Children.Clear();
        _shown.Clear();

        if (parent is not null)
        {
            var back = CreateRow(parent.Text, "\uE76B", null, null, null, null, enabled: true, destructive: false, secondary: true);
            AutomationProperties.SetName(back, parent.Text + "、戻る");
            back.Click += (_, _) => GoBack();
            Add(back, parent);
            _rows.Children.Add(Separator());
        }

        foreach (var entry in items)
        {
            switch (entry)
            {
                case MenuSeparator:
                    _rows.Children.Add(Separator());
                    break;
                case MenuHeader header:
                    _rows.Children.Add(new TextBlock
                    {
                        Text = header.Text,
                        Style = AppResources.Style("Text.Caption"),
                        Margin = new Thickness(12, 6, 12, 6),
                    });
                    break;
                case MenuCommand command:
                {
                    var trailing = command.IsChecked
                        ? new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = ThemeResources.Brush("Brand.Accent") }
                        : null;
                    var row = CreateRow(command.Text, command.Glyph, command.GlyphBrushKey, command.GlyphFontFamily,
                        command.Accelerator, trailing, command.IsEnabled, command.IsDestructive, secondary: false);
                    AutomationProperties.SetName(row, command.Text + (command.IsChecked ? "、選択中" : ""));
                    if (command.ToolTip is { } tip)
                    {
                        ToolTipService.SetToolTip(row, tip);
                    }

                    row.Click += (_, _) => Invoke(command);
                    Add(row, command);
                    break;
                }

                case MenuSubmenu submenu:
                {
                    var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush") };
                    var row = CreateRow(submenu.Text, submenu.Glyph, submenu.GlyphBrushKey, null, null, chevron,
                        submenu.IsEnabled, destructive: false, secondary: false);
                    AutomationProperties.SetName(row, submenu.Text + "、サブメニュー");
                    row.Click += (_, _) => Open(submenu);
                    Add(row, submenu);
                    break;
                }
            }
        }

        void Add(Button row, MenuEntry entry)
        {
            _rows.Children.Add(row);
            if (row.IsEnabled)
            {
                _shown.Add((row, entry));
            }
        }
    }

    /// <summary>1 行。アイコン、名前、キー、末尾の印（チェック、サブメニューの矢印）を並べる。</summary>
    private Button CreateRow(string text, string? glyph, string? glyphBrushKey, string? glyphFontFamily,
        string? accelerator, FontIcon? trailing, bool enabled, bool destructive, bool secondary)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var danger = destructive && enabled ? ThemeResources.Brush("Viz.Delay") : null;
        var disabled = enabled ? null : ThemeResources.Brush("TextFillColorDisabledBrush");

        // アイコンのない行も、名前の位置をそろえるため幅を取る
        var icon = new FontIcon
        {
            Glyph = glyph ?? "",
            FontSize = 16,
            Width = 16,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = disabled ?? danger ?? ThemeResources.Brush(glyphBrushKey ?? "TextFillColorSecondaryBrush"),
        };
        if (glyphFontFamily is not null)
        {
            icon.FontFamily = new FontFamily(glyphFontFamily);
        }

        grid.Children.Add(icon);

        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if ((disabled ?? danger ?? (secondary ? ThemeResources.Brush("TextFillColorSecondaryBrush") : null)) is { } labelBrush)
        {
            label.Foreground = labelBrush;
        }

        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (accelerator is { Length: > 0 })
        {
            var key = new TextBlock
            {
                Text = accelerator,
                Style = AppResources.Style("Text.Caption"),
                Foreground = disabled ?? ThemeResources.Brush("TextFillColorTertiaryBrush"),
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(key, 2);
            grid.Children.Add(key);
        }

        if (trailing is not null)
        {
            trailing.Margin = new Thickness(12, 0, 0, 0);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 3);
            grid.Children.Add(trailing);
        }

        var row = new Button
        {
            Content = grid,
            Style = AppResources.Style("ContextMenu.Item"),
            IsEnabled = enabled,
            Tag = destructive,
        };
        if (accelerator is { Length: > 0 })
        {
            AutomationProperties.SetAcceleratorKey(row, accelerator);
        }

        row.GotFocus += (_, _) =>
        {
            // キーで移ったときは印をその場に移す。ポインターで移ったときは PointerEntered で滑らせる
            if (row.FocusState == FocusState.Keyboard)
            {
                PlaceMark(row, glide: false);
            }
        };
        row.PointerEntered += (_, _) =>
        {
            if (!_live)
            {
                return;
            }

            row.Focus(FocusState.Pointer);
            PlaceMark(row, glide: true);
        };
        return row;
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 4, 8, 4),
        Background = ThemeResources.Brush("DividerStrokeColorDefaultBrush"),
        IsHitTestVisible = false,
    };

    // ---------------------------------------------------------------- 印

    /// <summary>いまいる行の下へ印を移す。ポインターで移るときは滑らせ、初めて出すときとキーで移るときはその場に置く。</summary>
    private void PlaceMark(Button row, bool glide)
    {
        if (row.ActualHeight <= 0)
        {
            return;
        }

        var visual = Motion.VisualOf(_mark);
        _mark.Height = row.ActualHeight;
        _mark.Background = row.Tag is true ? _dangerMarkBrush : _markBrush;
        var offset = new Vector3(0, (float)row.TransformToVisual(_page).TransformPoint(default).Y, 0);
        Motion.SpringTo(visual, "Translation", offset, Motion.Snappy, animate: glide && _markShown);
        if (!_markShown)
        {
            Motion.EaseTo(visual, "Opacity", 1f, MarkFade, Motion.EnterEasing(visual.Compositor));
            _markShown = true;
        }
    }

    private void HideMark()
    {
        if (!_markShown)
        {
            return;
        }

        Motion.EaseTo(Motion.VisualOf(_mark), "Opacity", 0f, MarkFade);
        _markShown = false;
    }

    // ---------------------------------------------------------------- 操作

    /// <summary>
    /// 開いたとき・中身を入れ替えたときのフォーカス。キーで開いたときは印を付けて置き、
    /// ポインターで開いたときは印を出さずに置く（ポインターを動かすと印が現れる）。
    /// </summary>
    /// <param name="prefer">フォーカスを置きたい行の項目（戻ったときのサブメニューなど）。</param>
    private void FocusInitial(MenuEntry? prefer)
    {
        var row = _shown.FirstOrDefault(s => ReferenceEquals(s.Entry, prefer)).Button
            ?? _shown.FirstOrDefault(s => s.Entry is MenuCommand { IsChecked: true }).Button
            ?? _shown.FirstOrDefault(s => _path.Count == 0 || !ReferenceEquals(s.Entry, _path.Peek().Submenu)).Button
            ?? _shown.FirstOrDefault().Button;
        if (row is null)
        {
            return;
        }

        if (LastInput.WasPointer)
        {
            HideMark();
            row.Focus(FocusState.Programmatic);
        }
        else
        {
            row.Focus(FocusState.Keyboard);
        }
    }

    private async void Invoke(MenuCommand command)
    {
        if (!_live)
        {
            return;
        }

        // 選んだ操作がピッカーやダイアログを開くことがあるため、先に閉じ始めてフォーカスを開く前の場所へ戻す
        Close(focusNow: true);
        await command.Invoke();
    }

    private void Open(MenuSubmenu submenu)
    {
        if (!_live)
        {
            return;
        }

        var items = _path.Count == 0 ? _items : _path.Peek().Submenu.Items;
        _path.Push((items, submenu));
        SwapPage(submenu.Items, submenu, direction: 1, prefer: null);
    }

    /// <summary>サブメニューから親へ戻る。サブメニューの中でなければ false（Esc なら閉じる）。</summary>
    private bool GoBack()
    {
        if (!_live || _path.Count == 0)
        {
            return false;
        }

        var (items, submenu) = _path.Pop();
        SwapPage(items, _path.Count == 0 ? null : _path.Peek().Submenu, direction: -1, prefer: submenu);
        return true;
    }

    /// <summary>
    /// 同じ面の中身を入れ替える。幅は狭めず（面が横に揺れないように）、高さは新しい中身に合わせて面が伸び縮みする。
    /// 新しい中身は、進むなら右から、戻るなら左から少しずれて現れる。
    /// </summary>
    private void SwapPage(IReadOnlyList<MenuEntry> items, MenuSubmenu? parent, int direction, MenuEntry? prefer)
    {
        _content.MinWidth = Math.Max(_content.MinWidth, _content.ActualWidth);
        foreach (var old in _rows.Children)
        {
            _morph?.Faces.Remove(old);
        }

        HideMark();
        BuildPage(items, parent);
        _morph?.Faces.AddRange(_rows.Children);

        Motion.Reveal(_page, new Vector3(direction * PageShift, 0, 0), PageIn);

        _page.UpdateLayout();
        FocusInitial(prefer);
    }

    private void Close(bool focusNow)
    {
        if (_morph is null || _morph.IsClosing)
        {
            return;
        }

        _live = false;
        _morph.Close(keyboard: !LastInput.WasPointer, focusNow);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_live || _shown.Count == 0)
        {
            return;
        }

        var current = _content.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        int focused = _shown.FindIndex(s => ReferenceEquals(current, s.Button));
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                MoveFocus(focused + 1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                MoveFocus(focused < 0 ? _shown.Count - 1 : focused - 1);
                break;
            case VirtualKey.Home:
                e.Handled = true;
                MoveFocus(0);
                break;
            case VirtualKey.End:
                e.Handled = true;
                MoveFocus(_shown.Count - 1);
                break;
            case VirtualKey.Right when focused >= 0 && _shown[focused].Entry is MenuSubmenu submenu && _path.All(p => !ReferenceEquals(p.Submenu, submenu)):
                e.Handled = true;
                Open(submenu);
                break;
            case VirtualKey.Left when _path.Count > 0:
                e.Handled = true;
                GoBack();
                break;
        }
    }

    private void MoveFocus(int index) =>
        _shown[(index % _shown.Count + _shown.Count) % _shown.Count].Button.Focus(FocusState.Keyboard);
}
