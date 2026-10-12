using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;
using Windows.UI.Text;
using Tasklabe.App.Services;

namespace Tasklabe.App.ViewModels;

/// <summary>WBS の列。</summary>
public enum WbsColumn
{
    Title,
    Assignee,
    Status,
    Start,
    Target,
    Estimate,
    Progress,
}

/// <summary>WBS の 1 行（UI デザイン設計書 3.4.4 節）。子を持つ行の工数と進捗率は子から積み上げた値を表示する。</summary>
public sealed partial class WbsRowViewModel(TaskNode node, bool isExpanded, DateOnly today) : ObservableObject
{
    /// <summary>
    /// 行のタスク。表示の変わらない更新（同期で届いた同じ内容など）では、行を作り直さずにここだけを差し替える（<see cref="LooksSame"/>）。
    /// </summary>
    public TaskNode Node { get; private set; } = node;

    public TaskItem Task => Node.Task;

    public string ItemId => Task.ItemId;

    /// <summary>選択中のセルの列（行が選ばれていないときは -1）。</summary>
    [ObservableProperty]
    public partial int ActiveColumn { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    public partial bool IsExpanded { get; set; } = isExpanded;

    public bool HasChildren => Node.HasChildren;

    public Thickness Indent => new(Node.Depth * 20, 0, 0, 0);

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string Title => Task.Title;

    public FontWeight TitleWeight => HasChildren ? FontWeights.Bold : FontWeights.Normal;

    public bool IsLocal => Task.IsLocal;

    /// <summary>タイトルの前に出す番号（設定で出さないとき、送信待ちのときは空。要件 F-SET-06）。</summary>
    public string NumberText => App.Current.Services.CurrentSettings.ShowNumberInPlan ? TaskKeys.Of(Task) : "";

    public bool HasNumber => NumberText.Length > 0;

    // ---------------------------------------------------------------- 状態

    /// <summary>完了・中止したタスクは目立たなくする。</summary>
    public bool IsMuted => Task.IsDone;

    public bool IsCanceled => !HasChildren && Task.IsCanceled;

    /// <summary>中止したタスクは、カンバンのカードと同じく取り消し線で示す。</summary>
    public TextDecorations TitleDecorations => IsCanceled ? TextDecorations.Strikethrough : TextDecorations.None;

    public bool IsDoing => !Task.IsDone && (Task.Category == StatusCategory.InProgress || (HasChildren && Progress > 0));

    public bool IsDone => HasChildren ? Progress >= 100 && Node.Summary.EstimateHours > 0 || Task.IsCompleted : Task.IsCompleted;

    public double Progress => HasChildren ? Node.Summary.ProgressPercent : Task.EffectiveProgress;

    /// <summary>状態アイコンで示すカテゴリ（親タスクは子から求めた状態）。</summary>
    public StatusCategory IconCategory => IsCanceled ? StatusCategory.Canceled : IsDone ? StatusCategory.Done : IsDoing ? StatusCategory.InProgress
        : HasChildren ? StatusCategory.Todo : Task.Category;

    public string StatusText => Task.StatusName ?? (Task.IsDone ? StatusVisuals.Name(Task.Category) : "—");

    public string StatusTip => HasChildren
        ? Texts.Format("Wbs_StatusFromChildren", StatusText)
        : Controls.KeyHints.Tip("task.status", Texts.Format("PickerButton_Name", Texts.Get("Field_Status"), StatusText));

    /// <summary>状態アイコンのツールチップ。押して変える要素のため、キー（S。ステータスのセルから開く）は添えない。</summary>
    public string StatusIconTip => Texts.Format("PickerButton_Name", Texts.Get("Field_Status"), StatusText);

    // ---------------------------------------------------------------- 値

    public string AssigneeText => string.Join(", ", Task.Assignees.Select(People.Display));

    public string StartText => Format(Task.Start);

    public string TargetText => Format(Task.Target);

    public bool IsOverdue => Task.IsOverdueOn(today);

    /// <summary>子の予定が親の枠（予定終了日）を超えた稼働日数（PlanFrame）。超えていなければ 0。</summary>
    public int FrameOverrunDays => PlanFrame.OverrunDays(Node);

    public bool HasFrameOverrun => FrameOverrunDays > 0;

    /// <summary>終了のセルに添える枠超えの印（「▲2日」）。</summary>
    public string FrameOverrunText => HasFrameOverrun ? Texts.Format("Status_DaysBehind", FrameOverrunDays) : "";

    public string FrameOverrunTip => PlanFrame.OverrunEnd(Node) is { } end && Task.Target is { } frame
        ? Texts.Format("Wbs_FrameOverrunTip", DateText.Short(end, today), DateText.Short(frame, today))
        : "";

    /// <summary>子の予定が親の枠の始まり（予定開始日）より前に始まる稼働日数（PlanFrame）。はみ出していなければ 0。</summary>
    public int FrameEarlyDays => PlanFrame.EarlyDays(Node);

    public bool HasFrameEarly => FrameEarlyDays > 0;

    /// <summary>開始のセルに添える枠超えの印（「▲2日」）。</summary>
    public string FrameEarlyText => HasFrameEarly ? Texts.Format("Status_DaysBehind", FrameEarlyDays) : "";

    public string FrameEarlyTip => PlanFrame.EarlyStart(Node) is { } start && Task.Start is { } frame
        ? Texts.Format("Wbs_FrameEarlyTip", DateText.Short(start, today), DateText.Short(frame, today))
        : "";

    /// <summary>子を持つ行は積み上げた工数（読み取り専用）。</summary>
    public string EstimateText => HasChildren
        ? (Node.Summary.EstimateHours > 0 ? EffortText.Of(Node.Summary.EstimateHours) : "—")
        : EffortText.Of(Task.EstimateHours, "—");

    public string ProgressText => $"{Progress:0}%";

    /// <summary>ステータス・工数・進捗率は、子を持つ行では編集できない（子から決まる値のため）。</summary>
    public bool IsReadOnly(WbsColumn column) => HasChildren && column is WbsColumn.Status or WbsColumn.Estimate or WbsColumn.Progress;

    /// <summary>ステータス・工数・進捗率のセルと状態アイコンを押せる（子を持つ行では押せず、押すと行を選ぶだけ）。</summary>
    public bool CanEditOwnValues => !HasChildren;

    /// <summary>読み上げの名前。タイトルのあとに、値を言語の区切りでつないで並べる。</summary>
    public string AutomationName
    {
        get
        {
            List<string> parts =
            [
                Title,
                Texts.Format("Wbs_Level", Node.Depth + 1),
                StatusText,
                Texts.Format("Card_ProgressText", ProgressText),
                Texts.Format("Card_Estimate", EstimateText),
            ];
            if (StartText.Length > 0)
            {
                parts.Add(Texts.Format("Wbs_StartName", StartText));
            }

            if (TargetText.Length > 0)
            {
                parts.Add(Texts.Format("Wbs_FinishName", TargetText));
            }

            if (HasFrameEarly)
            {
                parts.Add(Texts.Count("Wbs_FrameEarlyName", FrameEarlyDays));
            }

            if (HasFrameOverrun)
            {
                parts.Add(Texts.Count("Wbs_FrameOverrunName", FrameOverrunDays));
            }

            if (HasChildren)
            {
                parts.Add(IsExpanded ? Texts.Get("Wbs_Expanded") : Texts.Get("Wbs_Collapsed"));
            }

            return string.Join(Texts.Get("Common_ListSeparator"), parts);
        }
    }

    /// <summary>
    /// 同じ見た目の行か。同じなら行を作り直さず、タスクだけを差し替える（作り直すと行の部品が作り直され、表が点滅する）。
    /// </summary>
    public bool LooksSame(WbsRowViewModel other) => Look.Equals(other.Look);

    /// <summary>行に表示する値の組。</summary>
    private object Look => (ItemId, IsExpanded, Node.Depth, HasChildren, NumberText, Title, IsLocal, IsMuted, IconCategory, Progress,
        StatusText, AssigneeText, StartText, TargetText, IsOverdue, EstimateText, FrameOverrunText, FrameEarlyText);

    /// <summary>見た目の変わらないタスクに差し替える。</summary>
    public void Adopt(WbsRowViewModel other) => Node = other.Node;

    private string Format(DateOnly? d) => d is { } v ? DateText.Short(v, today) : "";
}
