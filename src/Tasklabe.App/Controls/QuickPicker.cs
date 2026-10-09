using System.Globalization;
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

/// <summary>ピッカーの選択肢。</summary>
/// <param name="Detail">右側に添える補足（期日の日付、キーなど）。</param>
/// <param name="Glyph">先頭のアイコン（ステータスのカテゴリなど）。</param>
/// <param name="IsSelected">いまの値。末尾のチェックと、開いたときのフォーカスに使う。</param>
/// <param name="GlyphBrushKey">アイコンの色のリソース名。</param>
/// <param name="IsMixed">複数のタスクで値が混在している（UX 規約 UX-07）。「—」の印で示す。</param>
/// <param name="Group">見出し。前の選択肢と違う見出しのときに、その上へ見出しを置く。</param>
/// <param name="OnlyWhenSearching">絞り込みの文字を入れたときだけ出す（コマンドパレットのタスクなど、数の多い候補）。</param>
/// <param name="Avatar">人の候補（担当者）のログイン名。名前の前に丸いアバターを置く。</param>
/// <param name="ToolTip">選択肢の意味（区分の説明など）。行には出さず、ツールチップで添える。</param>
/// <param name="Keywords">行には出さず、絞り込みにだけ使う言葉（タスクの別の形の番号など）。</param>
public sealed record PickerOption(
    string Label,
    string? Detail = null,
    string? Glyph = null,
    bool IsSelected = false,
    string? GlyphBrushKey = null,
    bool IsMixed = false,
    string? Group = null,
    bool OnlyWhenSearching = false,
    string? Avatar = null,
    string? ToolTip = null,
    string? Keywords = null);

/// <summary>ピッカーの結果。選択肢を選んだら <see cref="Index"/>、入力欄で決めたら <see cref="Text"/>。</summary>
public readonly record struct PickResult(int Index, string? Text);

/// <summary>複数を選ぶピッカーでの、選択肢ごとの状態。</summary>
public enum PickState
{
    Unchecked,
    Checked,

    /// <summary>複数のタスクで混在していて、利用者が触れていない（変えない）。</summary>
    Mixed,
}

/// <summary>
/// 候補から選ぶための共通のピッカー（UX 規約 UX-01〜05、UI デザイン設計書 4.1.2 節）。候補を選ぶ操作は、どの画面でもこれを使う。
/// 押したボタン・値のセル・カードの印（右クリックならその位置）から面が育って開き（Morph select）、上端に見出し、
/// その下に候補を並べる。いまいる候補は、一覧の下に敷いた印が滑って示す。1 つを選ぶと、選んだ候補の文字が
/// 押したボタンの値の位置へ戻りながら面が縮む。
/// 候補に 1〜9、0 の番号を振って数字キーで選び、↑↓・Home・End と Enter でも選べる。
/// 入力欄を持つものは Tab か文字キーで入力欄へ移る。候補が 10 を超えるものは絞り込みから始める。
/// 下端には、その中で使えるキーを示す。
/// </summary>
public sealed class QuickPicker
{
    /// <summary>これを超える候補は、入力欄での絞り込みから始める（UX-05）。</summary>
    private const int FilterThreshold = 10;

    private const double RowHeight = 32;
    private const double ListMaxHeight = 360;

    private static readonly TimeSpan MarkFade = TimeSpan.FromMilliseconds(120);

    /// <summary>押したボタンから開いたときの、いちばん狭い幅。</summary>
    private const double TriggerMinWidth = 240;

    private readonly Grid _content = new();
    private readonly StackPanel _root = new() { Spacing = 4, Padding = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel _items = new() { Spacing = 0 };
    private readonly Border _mark;
    private readonly ScrollViewer _scroll;
    private readonly Canvas _flyLayer = new() { IsHitTestVisible = false };
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _footer = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string _title;

    /// <summary>下端の区切り線ごと隠せるよう、案内の入れ物を持つ。</summary>
    private readonly Border _footerHost;
    private readonly IReadOnlyList<PickerOption> _options;
    private readonly bool _multiple;

    /// <summary>人の候補（アバターを置く）を含む。</summary>
    private readonly bool _hasAvatars;
    private readonly bool _filterable;
    private readonly double _width;
    private readonly Func<string, string?>? _validate;
    private readonly PickState[] _states;
    private readonly PickState[] _initialStates;
    private readonly List<(int Index, Button Button)> _shown = [];

    /// <summary>番号（1〜9、0）を振れる候補の数（UX-01）。</summary>
    private const int Numbered = 10;

    /// <summary>
    /// 入力欄から始めるか（UX-05）。番号を振りきれないほど候補があるときだけ絞り込みから始め、
    /// 番号で選べる数なら候補から始めて数字キーで選ばせる（文字を打てば入力欄へ移る。UX-04）。
    /// </summary>
    private bool StartsInInput => _input is not null && _filterable && _options.Count > Numbered;
    private readonly TextBox? _input;
    private readonly List<UIElement> _faces = [];
    private TaskCompletionSource<PickResult?>? _result;
    private MorphPopup? _morph;
    private FontIcon? _chevron;
    private TextBlock? _lidValue;

    /// <summary>上端の欄の要素と、ボタンの中で同じ役目の要素。開くと閉じるときに、ボタンとのあいだを運ぶ。</summary>
    private readonly List<(FrameworkElement Lid, FrameworkElement Source)> _lidCarries = [];

    /// <summary>上端の欄のうち、ボタンにないもの（小さな起点の値の名前）。中身と一緒に現れる。</summary>
    private readonly List<UIElement> _lidFaces = [];

    /// <summary>上端の欄の、小さな起点の見た目の写し。値を選ぶと、選んだ候補のアイコンと入れ替わる。</summary>
    private FrameworkElement? _lidFace;
    private bool _markShown;
    private bool _grown;

    /// <param name="multiple">複数を選ぶ（数字キーと Space で選ぶ・外すを切り替え、Enter で決める。UX-03）。</param>
    /// <param name="input">入力欄の案内文。null なら入力欄を置かない（候補が多いときは絞り込みの入力欄を置く）。</param>
    /// <param name="filterable">入力欄を候補の絞り込みに使う。false なら入力欄は値の入力（工数など）に使う。</param>
    /// <param name="validate">入力欄の値を確かめる。問題があれば利用者に示す文を返す（UX-27）。</param>
    /// <param name="width">幅。押したボタンのほうが広ければ、ボタンの幅に合わせる。</param>
    /// <param name="note">候補の下に添える補足（候補にない人の招待の方法など）。</param>
    /// <param name="search">必ず絞り込みから始める（コマンドパレット）。</param>
    public QuickPicker(string title, IReadOnlyList<PickerOption> options, bool multiple = false,
        string? input = null, bool filterable = false, Func<string, string?>? validate = null, double width = 320,
        string? note = null, bool search = false)
    {
        _title = title;
        _options = options;
        _multiple = multiple;
        _validate = validate;
        _width = width;

        // 候補が多いときは、入力欄を絞り込みに使う
        if (input is null && (search || options.Count > FilterThreshold))
        {
            input = "文字を入力して絞り込む";
            filterable = true;
        }

        _filterable = filterable;
        _hasAvatars = options.Any(o => o.Avatar is not null);
        _states = [.. options.Select(o => o.IsMixed ? PickState.Mixed : o.IsSelected ? PickState.Checked : PickState.Unchecked)];
        _initialStates = [.. _states];

        var caption = AppResources.Style("Text.Caption");
        var root = _root;

        if (input is not null)
        {
            _input = new TextBox { PlaceholderText = input, Margin = new Thickness(4, 0, 4, 0) };
            AutomationProperties.SetName(_input, input);
            _input.TextChanged += (_, _) =>
            {
                _error.Visibility = Visibility.Collapsed;
                if (_filterable)
                {
                    BuildItems();
                }
            };
            root.Children.Add(_input);
            _faces.Add(_input);
        }

        _error.Style = caption;
        _error.Foreground = ThemeResources.Brush("Viz.Delay");
        root.Children.Add(_error);
        _faces.Add(_error);

        // 候補。いまいる行の下に印を敷き、移ると印が滑る
        _mark = new Border
        {
            Height = RowHeight,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = AppResources.CornerRadius("Radius.Control"),
            Background = ThemeResources.Brush("Morph.Highlight"),
            IsHitTestVisible = false,
        };
        Motion.VisualOf(_mark).Opacity = 0;
        _scroll = new ScrollViewer { Content = new Grid { Children = { _mark, _items } }, MaxHeight = ListMaxHeight, Margin = new Thickness(4, 0, 4, 0) };
        root.Children.Add(_scroll);
        _faces.Add(_scroll);
        if (note is not null)
        {
            var noteText = new TextBlock { Text = note, Style = caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0) };
            root.Children.Add(noteText);
            _faces.Add(noteText);
        }

        // 使えるキーを下端に 1 行で示す（UX-17）
        _footer.Style = caption;
        _footerHost = new Border
        {
            BorderBrush = ThemeResources.Brush("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 6, 8, 2),
            Margin = new Thickness(4, 0, 4, 0),
            Child = _footer,
        };
        root.Children.Add(_footerHost);
        _faces.Add(_footerHost);

        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        _content.Children.Add(root);
        _content.Children.Add(_flyLayer);
        AutomationProperties.SetName(_content, title);
        BuildItems();
        UpdateFooter();
    }

    /// <summary>
    /// ピッカーを開き、閉じるまで待つ。何も選ばずに閉じたら null。
    /// 複数を選ぶときは、選ぶ・外すを変えていれば、どう閉じても決めたものとする（UX-03）。
    /// </summary>
    /// <param name="anchor">起点（押したボタン、値のセル、カード）。</param>
    /// <param name="position">右クリックした位置（anchor の中の座標）。</param>
    public async Task<PickResult?> ShowAsync(FrameworkElement anchor, Point? position = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        _result = new TaskCompletionSource<PickResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 押したボタンから開くときは、上端の欄をボタンと同じ見た目・同じ位置にし、ボタンがそのまま広がって見えるようにする。
        // 幅もボタンを基本にし、伸びるのは下（と、狭いボタンなら右）だけにする
        var trigger = position is null ? PickerTrigger.Of(anchor) : null;

        // 文字やアイコンでない起点の見た目（ステータスのアイコン、アバター）は、隠す前に写しを撮って上端の欄に置く
        ImageSource? face = null;
        if (trigger?.Icon is { } icon and not FontIcon and not TextBlock && icon.ActualWidth > 0)
        {
            face = await MorphPopup.TrySnapshotAsync(icon);
        }

        FrameworkElement lid = trigger is not null ? TriggerLid(trigger, face) : TitleLid();
        _root.Children.Insert(0, lid);
        _content.Width = trigger is not null ? Math.Max(anchor.ActualWidth, TriggerMinWidth) : _width;
        _morph = new MorphPopup(anchor, position, _content);
        _morph.Faces.AddRange(_faces);
        if (trigger is not null)
        {
            // 上端の欄のアイコン・項目名・値・矢印は、ボタンの中の同じものの位置から動き出す（面が横に広がっても、ボタンの文字がそのまま動いて見える）
            // ボタンにないもの（小さな起点の値の名前）は、中身と一緒に現れる
            foreach (var (part, source) in _lidCarries)
            {
                _morph.Carry(part, source);
            }

            _morph.Faces.AddRange(_lidFaces);
        }
        else
        {
            _morph.Faces.Add(lid);
        }

        _morph.OpensUpward += (_, _) => StackUpward();
        _morph.Dismissed += (_, _) => Dismiss();
        _morph.Grown += (_, _) =>
        {
            _grown = true;
            RotateChevron(180f, Motion.Glide);

            FocusInitial();
        };
        _morph.Open();
        return await _result.Task;
    }

    /// <summary>
    /// 押したボタンと同じ見た目の上端の欄。いまの値（アイコンと文字）と矢印を、ボタンと同じ位置に並べる。
    /// 位置はボタンの実際の並びから測り、ピッカーのボタンでも表のセルでも、文字と矢印がボタンの上にそのまま重なるようにする。
    /// 押すと閉じる（ボタンをもう一度押したのと同じ）。
    /// </summary>
    private Button TriggerLid(IPickerTrigger trigger, ImageSource? face)
    {
        var element = trigger.Element;

        // 値の前に並ぶもの（アイコン、項目名）の列、値（残りの幅）、矢印
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(row);

        // 値の前に並ぶものは、ボタンの中と同じ間隔で並べる
        var value = trigger.ValueText;
        var leading = new List<FrameworkElement>();
        if (trigger.Icon is { Visibility: Visibility.Visible } icon)
        {
            leading.Add(icon);
        }

        if (trigger.Prefix is { Visibility: Visibility.Visible } prefix)
        {
            leading.Add(prefix);
        }

        for (int i = 0; i < leading.Count; i++)
        {
            var source = leading[i];
            FrameworkElement? next = i + 1 < leading.Count ? leading[i + 1] : value;
            double gap = next is not null ? XOf(next, element) - XOf(source, element) - source.ActualWidth : 8;
            FrameworkElement copy = source switch
            {
                FontIcon glyph => new FontIcon
                {
                    Glyph = glyph.Glyph,
                    FontSize = glyph.FontSize,
                    FontFamily = glyph.FontFamily,
                    Foreground = glyph.Foreground,
                    Width = glyph.ActualWidth,
                },
                TextBlock text => TextLike(text),

                // 小さな起点の見た目（ステータスのアイコン、アバター）は、撮った写しをそのまま置く
                _ => _lidFace = new Image { Source = face, Width = source.ActualWidth, Height = source.ActualHeight, Stretch = Stretch.Fill },
            };
            copy.Margin = new Thickness(0, 0, gap, 0);
            copy.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(copy);
            _lidCarries.Add((copy, source));
        }

        // 値の文字。小さな起点には値の文字がないので、いまの値の名前を中身と一緒に現す
        if (value is not null)
        {
            _lidValue = TextLike(value);
            _lidCarries.Add((_lidValue, value));
        }
        else
        {
            _lidValue = new TextBlock { Text = CurrentLabel(), VerticalAlignment = VerticalAlignment.Center };
            _lidFaces.Add(_lidValue);
        }

        _lidValue.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_lidValue, 1);
        grid.Children.Add(_lidValue);

        // 矢印は、ボタンが「⌄」を持つときだけ置く（カードのアイコンやチップのように、矢印のない起点には足さない）
        var chevronSource = trigger.Chevron;
        if (chevronSource is not null)
        {
            _chevron = new FontIcon
            {
                Glyph = chevronSource.Glyph,
                FontSize = chevronSource.FontSize,
                Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _lidCarries.Add((_chevron, chevronSource));
            Grid.SetColumn(_chevron, 2);
            grid.Children.Add(_chevron);
        }

        // 先頭の左端と矢印の右端を、ボタンの中と同じ位置にする（矢印のないボタンでは、行と同じ余白を置く）。
        // 高さもボタンにそろえ、ボタンの中のアイコンや文字が上下に動かないようにする（行のスタイルの最小の高さ 32 は外す）
        double left = XOf(leading.Count > 0 ? leading[0] : value!, element);
        double right = chevronSource is not null ? element.ActualWidth - XOf(chevronSource, element) - chevronSource.ActualWidth : 12;
        var lid = new Button
        {
            Content = grid,
            Style = AppResources.Style("MorphSelect.Option"),
            Padding = new Thickness(left, 0, right, 0),
            Height = element.ActualHeight,
            MinHeight = 0,
            IsTabStop = false,
        };
        AutomationProperties.SetName(lid, _title + "を閉じる");
        lid.Click += (_, _) => Dismiss();
        return lid;

        static double XOf(FrameworkElement e, FrameworkElement root) => e.TransformToVisual(root).TransformPoint(default).X;
    }

    /// <summary>いまの値の名前（選んでいる候補。複数なら並べ、混在やなしは「—」）。</summary>
    private string CurrentLabel()
    {
        var selected = _options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
        return selected.Count == 0 || _options.Any(o => o.IsMixed) ? "—" : string.Join("、", selected);
    }

    /// <summary>
    /// ボタンの文字と同じスタイル（行の高さを含む）・大きさ・太さの文字。閉じて着地したときに入れ替わって見えないようにする。
    /// 余白は持たない（位置はボタンの文字の実際の位置から測っている）。
    /// </summary>
    private static TextBlock TextLike(TextBlock source) => new()
    {
        Style = source.Style,
        Margin = new Thickness(0),
        Text = source.Text,
        Foreground = source.Foreground,
        FontStyle = source.FontStyle,
        FontSize = source.FontSize,
        FontWeight = source.FontWeight,
        FontFamily = source.FontFamily,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// 上へ開くときの並び。上端の欄を下端（起点の位置）に置き、入力欄・候補・キーの案内をその上へ逆の順に積む。
    /// 候補の中の順は変えない。
    /// </summary>
    private void StackUpward()
    {
        var children = _root.Children.Reverse().ToList();
        _root.Children.Clear();
        foreach (var child in children)
        {
            _root.Children.Add(child);
        }

        _root.Padding = new Thickness(0, 4, 0, 0);
        _footerHost.BorderThickness = new Thickness(0, 0, 0, 1);
        _footerHost.Padding = new Thickness(8, 2, 8, 6);
    }

    /// <summary>起点がボタンでないとき（セル、カード、行）の上端の欄。何を選ぶのかを項目名で示す。</summary>
    private Grid TitleLid()
    {
        var lid = new Grid { Height = RowHeight, Padding = new Thickness(12, 0, 12, 0) };
        lid.Children.Add(new TextBlock
        {
            Text = _title,
            Style = AppResources.Style("Text.Caption"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return lid;
    }

    /// <summary>複数を選ぶピッカーで、決めたときの選択肢ごとの状態。</summary>
    public IReadOnlyList<PickState> States => _states;

    /// <summary>複数を選ぶピッカーで、選んだ選択肢の番号。</summary>
    public IReadOnlyList<int> Checked => [.. Enumerable.Range(0, _states.Length).Where(i => _states[i] == PickState.Checked)];

    // ---------------------------------------------------------------- 一覧

    private void BuildItems()
    {
        _items.Children.Clear();
        _shown.Clear();
        var query = _filterable ? _input?.Text.Trim() ?? "" : "";
        string? group = null;

        // 絞り込むときは、見出しの順を保ったまま、先頭から一致するもの（#番号など）を先に並べる
        var order = Enumerable.Range(0, _options.Count)
            .Where(i => query.Length > 0 ? Matches(_options[i], query) : !_options[i].OnlyWhenSearching)
            .OrderBy(i => GroupRank(_options[i].Group))
            .ThenBy(i => query.Length == 0 ? 0 : Rank(_options[i].Label, query))
            .ThenBy(i => i)
            .ToList();
        foreach (var i in order)
        {
            var option = _options[i];

            if (option.Group is { } g && g != group)
            {
                group = g;
                _items.Children.Add(new TextBlock
                {
                    Text = g,
                    Style = AppResources.Style("Text.Caption"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(8, _items.Children.Count == 0 ? 2 : 8, 8, 2),
                });
            }

            // 番号は絞り込んだ結果に振り直す（UX-05）
            var button = CreateItem(i, _shown.Count + 1);
            _shown.Add((i, button));
            _items.Children.Add(button);
        }

        if (_shown.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = query.Length > 0 ? "一致する候補はありません" : "選べる候補はありません",
                Style = AppResources.Style("Text.Caption"),
                Margin = new Thickness(8, 4, 8, 4),
            });
        }

        // 絞り込みで並びが変わったら、印は先頭の候補へ移る
        if (_grown)
        {
            _items.UpdateLayout();
            PlaceMark(_shown.FirstOrDefault().Button);
        }
    }

    private int GroupRank(string? group)
    {
        for (int i = 0; i < _options.Count; i++)
        {
            if (_options[i].Group == group)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static int Rank(string label, string query) =>
        label.StartsWith(query + " ", StringComparison.OrdinalIgnoreCase) || label.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
        : label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
        : 2;

    private static bool Matches(PickerOption option, string query) =>
        option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (option.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || (option.Group?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || (option.Keywords?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private Button CreateItem(int index, int number)
    {
        var option = _options[index];
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

        // 人の候補には、名前の前にカードと同じアバターを置く。行の子の並び（番号、印、名前）は変えない
        int shift = 0;
        if (_hasAvatars)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            shift = 1;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });

        // 番号（1〜9、10 番目は 0。UX-01）
        grid.Children.Add(new TextBlock
        {
            Text = number <= Numbered ? (number % 10).ToString(CultureInfo.InvariantCulture) : "",
            Style = AppResources.Style("Text.Caption"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 先頭は、複数を選ぶときは選ぶ・外すの四角、それ以外は候補のアイコン
        var mark = new FontIcon { FontSize = option.Glyph is null ? 12 : 14, Glyph = MarkGlyph(index) };
        if (option.GlyphBrushKey is { } brushKey)
        {
            mark.Foreground = ThemeResources.Brush(brushKey);
        }

        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        var label = new TextBlock { Text = option.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (_multiple && _states[index] == PickState.Checked)
        {
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }

        Grid.SetColumn(label, 2 + shift);
        grid.Children.Add(label);

        if (option.Detail is { Length: > 0 } detail)
        {
            var d = new TextBlock { Text = detail, Style = AppResources.Style("Text.Caption"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(d, 3 + shift);
            grid.Children.Add(d);
        }

        // 1 つを選ぶときは、いまの値を末尾のチェックで、混在を「—」で示す
        if (!_multiple && (option.IsSelected || option.IsMixed))
        {
            var check = option.IsMixed
                ? (FrameworkElement)new TextBlock { Text = "—", Style = AppResources.Style("Text.Caption"), VerticalAlignment = VerticalAlignment.Center }
                : new FontIcon { Glyph = "\uE73E", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Brush("Brand.Accent") };
            Grid.SetColumn(check, 4 + shift);
            grid.Children.Add(check);
        }

        if (option.Avatar is { } login)
        {
            var avatar = Avatar.Create(login);
            Grid.SetColumn(avatar, 2);
            grid.Children.Add(avatar);
        }

        var button = new Button
        {
            Content = grid,
            Style = AppResources.Style("MorphSelect.Option"),
            Tag = index,
        };
        AutomationProperties.SetName(button, AccessibleName(index));
        if (option.ToolTip is { Length: > 0 } tip)
        {
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetHelpText(button, tip);
        }

        button.Click += (_, _) => Activate(index);
        button.GotFocus += (_, _) => PlaceMark(button);
        button.PointerEntered += (_, _) => PlaceMark(button);
        return button;
    }

    private string MarkGlyph(int index)
    {
        if (_multiple)
        {
            return _states[index] switch
            {
                PickState.Checked => "\uE73A",   // チェックの入った四角
                PickState.Mixed => "\uE73C",     // 一部だけの四角
                _ => "\uE739",                   // 空の四角
            };
        }

        return _options[index].Glyph ?? "";
    }

    private string AccessibleName(int index)
    {
        var option = _options[index];
        var state = _multiple
            ? _states[index] switch { PickState.Checked => "、選択中", PickState.Mixed => "、一部のタスクで選択中", _ => "" }
            : option.IsSelected ? "、いまの値" : option.IsMixed ? "、一部のタスクの値" : "";
        return option.Label + (option.Detail is { Length: > 0 } d ? $"、{d}" : "") + state;
    }

    /// <summary>いまいる行の下へ印を滑らせる。初めて出すときは、その場に現す。</summary>
    private void PlaceMark(Button? row)
    {
        var visual = Motion.VisualOf(_mark);
        if (row is null || row.ActualHeight <= 0)
        {
            Motion.EaseTo(visual, "Opacity", 0f, MarkFade);
            _markShown = false;
            return;
        }

        _mark.Height = row.ActualHeight;
        var offset = new Vector3(0, (float)row.TransformToVisual(_items).TransformPoint(default).Y, 0);
        Motion.SpringTo(visual, "Translation", offset, Motion.Glide, animate: _markShown);
        if (!_markShown)
        {
            Motion.EaseTo(visual, "Opacity", 1f, MarkFade, Motion.EnterEasing(visual.Compositor));
        }

        _markShown = true;
    }

    private void UpdateFooter()
    {
        // ヒントを出さない設定でも、選んでいる件数は残す
        if (!Hints.Shown)
        {
            _footer.Text = _multiple ? $"{Checked.Count} 件を選択中" : "";
            _footerHost.Visibility = _multiple ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _footerHost.Visibility = Visibility.Visible;
        _footer.Text = _multiple
            ? $"{Checked.Count} 件を選択中　Space で選ぶ・外す"
            : _filterable
                ? StartsInInput
                    ? "文字で絞り込み　↓ で候補へ　Enter で先頭を選ぶ　Esc で閉じる"
                    : "数字キーで選ぶ　文字で絞り込み　Esc で閉じる"
                : _input is not null
                    ? "数字キーか Enter で決定　Tab で入力　Esc で閉じる"
                    : "数字キーか Enter で決定　Esc で閉じる";
    }

    private void FocusInitial()
    {
        // 候補が多い絞り込みは入力から始め、それ以外はいまの値（なければ先頭）から始める
        if (StartsInInput && _input is { } input)
        {
            input.Focus(FocusState.Keyboard);
            PlaceMark(_shown.FirstOrDefault().Button);
            return;
        }

        var initial = _shown.FirstOrDefault(s => _options[s.Index].IsSelected || _options[s.Index].IsMixed).Button ?? _shown.FirstOrDefault().Button;
        if (initial is not null)
        {
            initial.Focus(FocusState.Keyboard);
            PlaceMark(initial);
        }
        else
        {
            _input?.Focus(FocusState.Keyboard);
        }
    }

    // ---------------------------------------------------------------- 選ぶ

    private void Activate(int index)
    {
        if (_multiple)
        {
            // 混在（触れていない）→ 選ぶ → 外す と切り替える
            _states[index] = _states[index] == PickState.Checked ? PickState.Unchecked : PickState.Checked;
            if (_shown.FirstOrDefault(s => s.Index == index).Button is { } button && button.Content is Grid g)
            {
                ((FontIcon)g.Children[1]).Glyph = MarkGlyph(index);
                ((TextBlock)g.Children[2]).FontWeight = _states[index] == PickState.Checked
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                AutomationProperties.SetName(button, AccessibleName(index));
            }

            UpdateFooter();
            return;
        }

        FlyToTrigger(index);
        Close(new PickResult(index, null));
    }

    /// <summary>
    /// 選んだ候補の文字を、押したボタンの値の位置へ運ぶ。一覧は先に消えるので、文字の写しを一覧の上に置いて動かす。
    /// 起点がピッカーのボタンでなければ運ばない。
    /// </summary>
    private void FlyToTrigger(int index)
    {
        if (_morph?.Trigger is not { ShowsChoice: true } || !Motion.IsEnabled
            || _shown.FirstOrDefault(s => s.Index == index).Button is not { Content: Grid row })
        {
            return;
        }

        if (_morph.Trigger is { ValueText: null } compact)
        {
            FlyIconToTrigger(index, row, compact);
            return;
        }

        var label = (TextBlock)row.Children[2];
        var at = label.TransformToVisual(_flyLayer).TransformPoint(default);
        var copy = new TextBlock { Text = label.Text };
        if (_morph.Trigger is { ValueText: { } valueText })
        {
            copy.Style = valueText.Style;
            copy.Margin = new Thickness(0);
            copy.FontSize = valueText.FontSize;
            copy.FontWeight = valueText.FontWeight;
            copy.FontFamily = valueText.FontFamily;
        }

        Canvas.SetLeft(copy, at.X);
        Canvas.SetTop(copy, at.Y);
        _flyLayer.Children.Add(copy);
        _flyLayer.UpdateLayout();
        if (_morph.OffsetToTriggerValue(copy) is { } offset)
        {
            if (_lidValue is not null)
            {
                _lidValue.Opacity = 0;
            }

            Motion.SpringTo(Motion.VisualOf(copy), "Translation", offset, _morph.ShrinkSpring);
        }
    }

    /// <summary>
    /// 小さな起点（ステータスのアイコン、アバター）へは、選んだ候補のアイコン（アバター）を運ぶ。
    /// 写しは起点の見た目と同じ大きさの箱の中央に置き、候補の行のアイコンの位置から動き出す。
    /// </summary>
    private void FlyIconToTrigger(int index, Grid row, IPickerTrigger trigger)
    {
        var option = _options[index];
        if (trigger.Icon is not { } face || _morph is null)
        {
            return;
        }

        FrameworkElement? mark = option.Avatar is not null ? row.Children.OfType<Border>().LastOrDefault()
            : option.Glyph is not null ? (FrameworkElement)row.Children[1]
            : null;
        if (mark is null)
        {
            return;
        }

        FrameworkElement inner = option.Avatar is { } login
            ? Avatar.Create(login)
            : new FontIcon { Glyph = option.Glyph!, FontSize = ((FontIcon)mark).FontSize, Foreground = ((FontIcon)mark).Foreground };
        inner.HorizontalAlignment = HorizontalAlignment.Center;
        inner.VerticalAlignment = VerticalAlignment.Center;
        var copy = new Grid { Width = face.ActualWidth, Height = face.ActualHeight, Children = { inner } };
        var at = mark.TransformToVisual(_flyLayer).TransformPoint(default);
        Canvas.SetLeft(copy, at.X + (mark.ActualWidth - face.ActualWidth) / 2);
        Canvas.SetTop(copy, at.Y + (mark.ActualHeight - face.ActualHeight) / 2);
        _flyLayer.Children.Add(copy);
        _flyLayer.UpdateLayout();
        if (_morph.OffsetToTriggerValue(copy) is { } offset)
        {
            if (_lidFace is not null)
            {
                _lidFace.Opacity = 0;
            }

            Motion.SpringTo(Motion.VisualOf(copy), "Translation", offset, _morph.ShrinkSpring);
        }
    }

    /// <summary>上端の欄の矢印を、矢印の中心で回す。</summary>
    private void RotateChevron(float angle, Spring spring)
    {
        if (_chevron is null)
        {
            return;
        }

        var chevron = Motion.VisualOf(_chevron);
        chevron.CenterPoint = new Vector3((float)_chevron.ActualWidth / 2, (float)_chevron.ActualHeight / 2, 0);
        Motion.SpringTo(chevron, "RotationAngleInDegrees", angle, spring);
    }

    private void Close(PickResult? result)
    {
        if (_morph is null || _morph.IsClosing)
        {
            return;
        }

        _result?.TrySetResult(result);
        RotateChevron(0f, _morph.ShrinkSpring);

        _morph.Close(keyboard: !LastInput.WasPointer);
    }

    /// <summary>
    /// 何も選ばずに閉じる（Esc、外側や上端の欄を押す）。
    /// 複数を選ぶときは、閉じ方によらず、変えた内容を反映する（UX-03）。
    /// </summary>
    private void Dismiss()
    {
        bool changed = _multiple && !_states.SequenceEqual(_initialStates);
        Close(changed ? new PickResult(-1, null) : null);
    }

    private void SubmitInput()
    {
        if (_input is null)
        {
            return;
        }

        if (_filterable)
        {
            // 絞り込みの先頭を選ぶ
            if (_shown.Count > 0)
            {
                Activate(_shown[0].Index);
            }

            return;
        }

        // 全角の数字や記号も読めるよう、半角に直してから確かめる（UX-30）
        var text = _input.Text.Trim().Normalize(System.Text.NormalizationForm.FormKC);
        if (_validate?.Invoke(text) is { } problem)
        {
            _error.Text = "⚠ " + problem;
            _error.Visibility = Visibility.Visible;
            return;
        }

        Close(new PickResult(-1, text));
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var current = _content.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        bool inInput = _input is not null && ReferenceEquals(current, _input);
        int focused = _shown.FindIndex(s => ReferenceEquals(current, s.Button));
        bool modified = KeyInput.IsDown(VirtualKey.Control) || KeyInput.IsDown(VirtualKey.Menu);

        if (!inInput && !modified && KeyInput.Digit(e.Key) is { } n && n <= Math.Min(_shown.Count, Numbered))
        {
            e.Handled = true;
            if (_multiple)
            {
                _shown[n - 1].Button.Focus(FocusState.Keyboard);
            }

            Activate(_shown[n - 1].Index);
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                MoveFocus(inInput ? 0 : focused + 1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                if (focused <= 0 && _input is not null)
                {
                    _input.Focus(FocusState.Keyboard);
                }
                else
                {
                    MoveFocus(focused - 1);
                }

                break;
            case VirtualKey.Home when !inInput:
                e.Handled = true;
                MoveFocus(0);
                break;
            case VirtualKey.End when !inInput:
                e.Handled = true;
                MoveFocus(_shown.Count - 1);
                break;
            case VirtualKey.Enter when inInput:
                e.Handled = true;
                SubmitInput();
                break;
            case VirtualKey.Enter when _multiple:
                // 複数を選ぶときの Enter は閉じて反映する（ボタンの押下にしない）
                e.Handled = true;
                Close(new PickResult(-1, null));
                break;
            default:
                // 候補にいるときに文字を打ち始めたら、入力欄へ移ってそのまま入力させる（UX-04）
                if (!inInput && !modified && _input is not null && IsTextKey(e.Key))
                {
                    _input.Focus(FocusState.Keyboard);
                    _input.SelectionStart = _input.Text.Length;
                }

                break;
        }
    }

    private static bool IsTextKey(VirtualKey key) =>
        key is >= VirtualKey.A and <= VirtualKey.Z
            || (int)key is 187 or 188 or 189 or 190 or 191 or 219 or 220 or 221 or 222 or 226
            || (key is >= VirtualKey.Number0 and <= VirtualKey.Number9 && KeyInput.IsDown(VirtualKey.Shift));

    private void MoveFocus(int index)
    {
        if (_shown.Count == 0)
        {
            return;
        }

        _shown[(index % _shown.Count + _shown.Count) % _shown.Count].Button.Focus(FocusState.Keyboard);
    }
}
