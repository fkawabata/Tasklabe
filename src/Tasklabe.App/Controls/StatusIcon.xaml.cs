using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.Core.Domain;

namespace Tasklabe.App.Controls;

/// <summary>
/// ステータスのカテゴリを形で示すアイコン（UI デザイン設計書 2.4 節）。
/// Backlog は横線の円、Todo は空の円、In Progress は進捗率の円（0 % は中心の点）、Done はチェックの円、
/// Pending は一時停止の円、Canceled はバツの円。
/// </summary>
public sealed partial class StatusIcon : UserControl
{
    public static readonly DependencyProperty CategoryProperty = DependencyProperty.Register(
        nameof(Category), typeof(StatusCategory), typeof(StatusIcon), new PropertyMetadata(StatusCategory.Todo, (d, _) => ((StatusIcon)d).Update()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(StatusIcon), new PropertyMetadata(0.0, (d, _) => ((StatusIcon)d).Update()));

    public StatusIcon()
    {
        InitializeComponent();
    }

    /// <summary>アイコンで示すカテゴリ（親タスクは子から求めた状態）。</summary>
    public StatusCategory Category
    {
        get => (StatusCategory)GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    /// <summary>進捗率（0〜100）。In Progress のときの円の塗りに使う。</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private void Update()
    {
        var category = Category;
        BacklogIcon.Visibility = category == StatusCategory.Backlog ? Visibility.Visible : Visibility.Collapsed;
        TodoIcon.Visibility = category == StatusCategory.Todo ? Visibility.Visible : Visibility.Collapsed;
        DoingIcon.Visibility = category == StatusCategory.InProgress ? Visibility.Visible : Visibility.Collapsed;
        PendingIcon.Visibility = category == StatusCategory.Pending ? Visibility.Visible : Visibility.Collapsed;
        DoneIcon.Visibility = category == StatusCategory.Done ? Visibility.Visible : Visibility.Collapsed;
        CanceledIcon.Visibility = category == StatusCategory.Canceled ? Visibility.Visible : Visibility.Collapsed;
        DoingIcon.Value = Progress;
    }
}
