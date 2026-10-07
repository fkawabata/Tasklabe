using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>
/// 角を短い辺の半分で丸め、両端を半円にする（UI デザイン設計書 2.6 節の Pill）。
/// WinUI は大きさの半分を超える角の丸めを縦と横で別々に縮めるため、大きな固定値を入れると横長の要素は楕円になる。
/// そこで、大きさが変わるたびに実際の高さ（幅のほうが短ければ幅）から求める。
/// </summary>
public static class Pill
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(Pill), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>コードで作る要素に付ける。</summary>
    public static T Apply<T>(T element)
        where T : FrameworkElement
    {
        SetIsEnabled(element, true);
        return element;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if (e.NewValue is true)
        {
            element.SizeChanged += OnSizeChanged;
            Round(element, new Size(element.ActualWidth, element.ActualHeight));
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Round((FrameworkElement)sender, e.NewSize);

    private static void Round(FrameworkElement element, Size size)
    {
        var radius = new CornerRadius(Math.Max(0, Math.Min(size.Width, size.Height) / 2));
        switch (element)
        {
            case Control control:
                control.CornerRadius = radius;
                break;
            case Border border:
                border.CornerRadius = radius;
                break;
        }
    }
}
