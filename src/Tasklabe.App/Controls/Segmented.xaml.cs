using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Tasklabe.Animation;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// 少数の選択肢を横に並べ、押すだけで切り替える（ガントの日・週・月）。いまの値がいつも見え、1 回で選べる。
/// 選んでいるものは溝の上に浮いた印で示し、選び直すと印がばねの動きで滑る。もう一度押しても外れない。
/// 項目の幅はいちばん広いものにそろえ、印の大きさが変わらないようにする。
/// </summary>
public sealed partial class Segmented : UserControl
{
    private readonly List<ToggleButton> _buttons = [];
    private readonly Visual _selectionVisual;
    private bool _placed;

    public Segmented(string name, IReadOnlyList<string> labels, int selected = 0)
    {
        ArgumentNullException.ThrowIfNull(labels);
        InitializeComponent();

        var style = AppResources.Style("Segmented.Item");
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            var button = new ToggleButton { Content = labels[i], Style = style };
            AutomationProperties.SetName(button, $"{name}: {labels[i]}");
            button.Click += (_, _) =>
            {
                SelectedIndex = index;
                SelectionChanged?.Invoke(this, index);
            };
            _buttons.Add(button);
            Items.Children.Add(button);
        }

        _selectionVisual = Motion.VisualOf(Selection);
        Items.Loaded += (_, _) => EqualizeWidths();
        Items.SizeChanged += (_, _) => PlaceSelection(animate: false);

        AutomationProperties.SetName(this, name);
        SelectedIndex = selected;
    }

    public event EventHandler<int>? SelectionChanged;

    public int SelectedIndex
    {
        get;
        set
        {
            bool moved = field != value;
            field = value;
            for (int i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].IsChecked = i == value;
            }

            // 同じ値を設定し直されたとき（押したあとに呼び出し側が表示を合わせるなど）は、滑っている途中の印を止めない
            if (moved || !_placed)
            {
                PlaceSelection(animate: _placed);
            }
        }
    }

    private void EqualizeWidths()
    {
        double widest = 0;
        foreach (var button in _buttons)
        {
            button.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, button.DesiredSize.Width);
        }

        foreach (var button in _buttons)
        {
            button.MinWidth = widest;
        }
    }

    /// <summary>選択の印を、選んでいる項目の下へ置く。</summary>
    private void PlaceSelection(bool animate)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _buttons.Count)
        {
            Selection.Opacity = 0;
            return;
        }

        var button = _buttons[SelectedIndex];
        if (button.ActualWidth <= 0)
        {
            return;
        }

        var margin = Selection.Margin;
        Selection.Width = button.ActualWidth - margin.Left - margin.Right;
        Selection.Opacity = 1;
        var offset = new Vector3((float)button.TransformToVisual(Items).TransformPoint(default).X, 0, 0);
        Motion.SpringTo(_selectionVisual, "Translation", offset, Motion.Morph, animate: animate);
        _placed = true;
    }
}
