using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// ピッカーを開くボタンの形をした起点（いまの値と「⌄」を並べたもの）。ここから開くピッカーは、上端の欄を起点と同じ見た目・
/// 同じ位置にして起点がそのまま広がったように見せ、選んだ値を起点の値の位置へ戻す（UI デザイン設計書 4.1.2 節）。
/// カードのステータスのアイコンや担当者のアバターのように、値の文字を持たない小さな起点（<see cref="ValueText"/> が null）は、
/// 起点の見た目が上端の欄の左にそのまま残り、右に値の名前と「^」が現れて、通常のボタンの形に広がる。
/// </summary>
internal interface IPickerTrigger
{
    FrameworkElement Element { get; }

    /// <summary>値の前のアイコン（小さな起点では、起点の見た目そのもの）。ないときは null。</summary>
    FrameworkElement? Icon { get; }

    /// <summary>値の前の項目名（並び順のボタンの「並び順:」など）。ないときは null。</summary>
    TextBlock? Prefix => null;

    /// <summary>値の文字。値の文字を持たない小さな起点では null。</summary>
    TextBlock? ValueText { get; }

    /// <summary>右端の「⌄」。持たない起点（カードのチップなど）では null。</summary>
    FontIcon? Chevron { get; }

    /// <summary>
    /// 選んだ候補がそのままボタンの値になる。false なら（絞り込みのように、選んだあとに続きを開くボタン）、選んだ候補をボタンへ運ばない。
    /// </summary>
    bool ShowsChoice => true;
}

/// <summary>
/// 起点がボタンの形をしているかを決める。<see cref="PickerButton"/> のほか、表のセルやカードの値のように
/// <see cref="IPickerTrigger"/> を持たない要素でも、XAML で <c>controls:PickerTrigger.IsTrigger="True"</c> を付けると、
/// 中の文字とアイコンからボタンの形として扱う（最初の文字を値、下向きの矢印のアイコンを「⌄」、ほかのアイコンを値の前のアイコンとする）。
/// 値の文字を持たない小さな起点（アイコンだけ、アバターだけ）には、さらに <c>controls:PickerTrigger.IsCompact="True"</c> を付ける。
/// </summary>
public static partial class PickerTrigger
{
    /// <summary>下向きの矢印（Segoe Fluent Icons の ChevronDown）。</summary>
    private const string ChevronGlyph = "\uE70D";

    public static readonly DependencyProperty IsTriggerProperty = DependencyProperty.RegisterAttached(
        "IsTrigger", typeof(bool), typeof(PickerTrigger), new PropertyMetadata(false));

    /// <summary>選んだ候補がそのままボタンの値になるか（<see cref="IPickerTrigger.ShowsChoice"/>）。既定は true。</summary>
    public static readonly DependencyProperty ShowsChoiceProperty = DependencyProperty.RegisterAttached(
        "ShowsChoice", typeof(bool), typeof(PickerTrigger), new PropertyMetadata(true));

    /// <summary>値の文字を持たない小さな起点（中身全体を値のアイコンとして扱う）。</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(PickerTrigger), new PropertyMetadata(false));

    /// <summary>
    /// ピッカーを起点の左下の角から右へ開く（既定は「⌄」の側を留めて左へ開く）。左に候補の列を持つピッカー（日付）を、
    /// 押したボタンの真下に候補が並ぶように開くときに付ける。右へ収まらなければ、既定と同じく右下の角から開く。
    /// </summary>
    public static readonly DependencyProperty OpensFromStartProperty = DependencyProperty.RegisterAttached(
        "OpensFromStart", typeof(bool), typeof(PickerTrigger), new PropertyMetadata(false));

    public static bool GetOpensFromStart(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(OpensFromStartProperty);
    }

    public static void SetOpensFromStart(FrameworkElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(OpensFromStartProperty, value);
    }

    public static bool GetIsTrigger(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsTriggerProperty);
    }

    public static void SetIsTrigger(FrameworkElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsTriggerProperty, value);
    }

    public static bool GetShowsChoice(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ShowsChoiceProperty);
    }

    public static void SetShowsChoice(FrameworkElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShowsChoiceProperty, value);
    }

    public static bool GetIsCompact(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsCompactProperty);
    }

    public static void SetIsCompact(FrameworkElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsCompactProperty, value);
    }

    /// <summary>
    /// 起点がボタンの形なら、その形を返す。そうでなければ（値のセル、行、右クリックの位置）null。
    /// 値の文字を隠している形（狭いときにアイコンだけにした並び順のボタン）は、ボタンの形として扱わない。
    /// </summary>
    internal static IPickerTrigger? Of(FrameworkElement anchor)
    {
        var trigger = anchor switch
        {
            IPickerTrigger own => own,
            _ when GetIsTrigger(anchor) => Cell.Of(anchor),
            _ => null,
        };
        return trigger switch
        {
            { ValueText.Visibility: Visibility.Visible } => trigger,
            { ValueText: null, Icon: not null } => trigger,
            _ => null,
        };
    }


    /// <summary>ボタンの形をした、IPickerTrigger を持たない要素（計画の表のステータスのセル、絞り込みのボタン、カードの値）。</summary>
    private sealed record Cell(FrameworkElement Element, FrameworkElement? Icon, TextBlock? ValueText, FontIcon? Chevron, bool ShowsChoice)
        : IPickerTrigger
    {
        public static Cell? Of(FrameworkElement element)
        {
            bool showsChoice = GetShowsChoice(element);
            if (GetIsCompact(element))
            {
                // 中身全体（ステータスのアイコン、アバター）を値のアイコンとして扱う
                var face = element is ContentControl { Content: FrameworkElement content } ? content : element;
                return new Cell(element, face, null, null, showsChoice);
            }

            var icons = VisualTree.Descendants<FontIcon>(element).Where(i => i.Visibility == Visibility.Visible).ToList();
            var chevron = icons.LastOrDefault(i => i.Glyph == ChevronGlyph);
            var icon = icons.FirstOrDefault(i => !ReferenceEquals(i, chevron));
            // アイコン（FontIcon）も中に字形の文字を持つため、アイコンの中の文字は値として拾わない
            var text = VisualTree.Descendants<TextBlock>(element).FirstOrDefault(t => !InsideIcon(t, element));
            return text is null ? null : new Cell(element, icon, text, chevron, showsChoice);
        }

        private static bool InsideIcon(DependencyObject child, DependencyObject root) =>
            VisualTree.AncestorsAndSelf(child).Skip(1).TakeWhile(e => !ReferenceEquals(e, root)).Any(e => e is IconElement);
    }
}
