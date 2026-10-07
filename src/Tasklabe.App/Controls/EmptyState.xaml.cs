using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tasklabe.App.Controls;

/// <summary>空の状態。アイコン、見出し、説明、次の操作のボタンを縦に並べる。読み込み中は輪の表示に替える。</summary>
public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    /// <summary>ボタンが押された。</summary>
    public event EventHandler? ActionInvoked;

    public string Glyph
    {
        get => Icon.Glyph;
        set => Icon.Glyph = value;
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Message
    {
        get => MessageText.Text;
        set
        {
            MessageText.Text = value;
            MessageText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>ボタンの文言。空ならボタンを出さない。</summary>
    public string ActionText
    {
        get => ActionButton.Content as string ?? "";
        set
        {
            ActionButton.Content = value;
            UpdateAction();
        }
    }

    /// <summary>ボタンのツールチップ（ショートカットキーを添える）。</summary>
    public string? ActionToolTip
    {
        get => ToolTipService.GetToolTip(ActionButton) as string;
        set => ToolTipService.SetToolTip(ActionButton, value);
    }

    /// <summary>読み込み中か。読み込み中はアイコンとボタンを隠して輪を回す。</summary>
    public bool IsLoading
    {
        get => Ring.IsActive;
        set
        {
            Ring.IsActive = value;
            Ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Icon.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            UpdateAction();
        }
    }

    private void UpdateAction() =>
        ActionButton.Visibility = !IsLoading && ActionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionInvoked?.Invoke(this, EventArgs.Empty);
}
