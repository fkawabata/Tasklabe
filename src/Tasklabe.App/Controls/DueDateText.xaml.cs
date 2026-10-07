using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Tasklabe.App.Controls;

/// <summary>期日の表示。期日を過ぎていれば警告のアイコンと Viz.Delay の色で示す。</summary>
public sealed partial class DueDateText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DueDateText), new PropertyMetadata("", (d, _) => ((DueDateText)d).Update()));

    public static readonly DependencyProperty IsOverdueProperty = DependencyProperty.Register(
        nameof(IsOverdue), typeof(bool), typeof(DueDateText), new PropertyMetadata(false, (d, _) => ((DueDateText)d).Update()));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(DueDateText), new PropertyMetadata(null, (d, e) => ((DueDateText)d).Label.Style = (Style)e.NewValue));

    public DueDateText()
    {
        InitializeComponent();
        Update();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsOverdue
    {
        get => (bool)GetValue(IsOverdueProperty);
        set => SetValue(IsOverdueProperty, value);
    }

    /// <summary>文字のスタイル（表のセルは Text.Body、カードや一覧は Text.Caption）。</summary>
    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    private void Update()
    {
        Label.Text = Text;
        bool overdue = IsOverdue && !string.IsNullOrEmpty(Text);
        VisualStateManager.GoToState(this, overdue ? "Overdue" : "Normal", false);
        AutomationProperties.SetName(this, overdue ? $"{Text}（期日超過）" : Text);
        ToolTipService.SetToolTip(this, overdue ? "予定終了日を過ぎています" : null);
    }
}
