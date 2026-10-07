using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// 左右の境目をドラッグして幅を変えるつまみ（UI デザイン設計書 3.1 節）。見た目は Windows Community Toolkit の Sizer に合わせる。
/// ポインターを重ねたときだけ、中央に縦長のつまみ（4 × 24）を出して帯を淡く塗り、押すと少し濃く塗る。ダブルクリックで既定に戻す。
/// </summary>
public sealed partial class PaneSizer : Grid
{
    private readonly Rectangle _thumb = new()
    {
        Width = 4,
        Height = 24,
        RadiusX = 2,
        RadiusY = 2,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Opacity = 0,
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(83) },
    };

    private double _startX;
    private bool _dragging;
    private bool _hovering;

    public PaneSizer()
    {
        Width = 12;
        CornerRadius = AppResources.CornerRadius("Radius.Control");
        BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(83) };
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        ToolTipService.SetToolTip(this, "ドラッグで幅を変える。ダブルクリックで元の幅に戻す");
        Children.Add(_thumb);
        UpdateVisual();
        ActualThemeChanged += (_, _) => UpdateVisual();

        // 隠れたあとに重ねた形のまま残らないようにする（縮めて閉じたときなど）
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            _hovering = false;
            UpdateVisual();
        });

        PointerEntered += (_, _) =>
        {
            _hovering = true;
            UpdateVisual();
        };
        PointerExited += (_, _) =>
        {
            _hovering = false;
            UpdateVisual();
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => EndDrag(null);
        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ResetRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>ドラッグで動いた量（押した位置からの左右の差）。</summary>
    public event EventHandler<double>? Resizing;

    /// <summary>ドラッグを終えた。</summary>
    public event EventHandler? Resized;

    /// <summary>ダブルクリックで既定に戻すよう求められた。</summary>
    public event EventHandler? ResetRequested;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _startX = e.GetCurrentPoint(null).Position.X;
        _dragging = CapturePointer(e.Pointer);
        UpdateVisual();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Resizing?.Invoke(this, e.GetCurrentPoint(null).Position.X - _startX);
            e.Handled = true;
        }
    }

    private void EndDrag(PointerRoutedEventArgs? e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (e is not null)
        {
            ReleasePointerCapture(e.Pointer);
        }

        UpdateVisual();
        Resized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toolkit の Sizer と同じブラシ（通常は透明、重ねると Tertiary、押すと Quarternary）。つまみは重ねたときだけ出す。</summary>
    private void UpdateVisual()
    {
        Background = ThemeResources.Brush(_dragging ? "ControlAltFillColorQuarternaryBrush"
            : _hovering ? "ControlAltFillColorTertiaryBrush"
            : "ControlAltFillColorTransparentBrush");
        _thumb.Fill = ThemeResources.Brush("ControlStrongFillColorDefaultBrush");
        _thumb.Opacity = _dragging || _hovering ? 1 : 0;
    }
}
