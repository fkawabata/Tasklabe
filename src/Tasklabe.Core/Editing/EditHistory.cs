using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Editing;

/// <summary>1 つのタスクへの変更のまとまり。</summary>
public sealed record TaskEdit(string ItemId, string Title, IReadOnlyList<TaskChange> Changes);

/// <summary>1 回の操作で行った変更（複数のタスクにまたがることがある）。元に戻す単位とする。</summary>
public sealed record EditStep(IReadOnlyList<TaskEdit> Edits)
{
    /// <summary>
    /// 「「設計」のステータスの変更」「3 件のタスクの変更」のような、操作の短い説明。
    /// 依存関係を張るのに合わせて後続の予定をずらした操作は、依存関係も戻ることが分かるよう
    /// 「「基本設計書」の先行タスクの変更と、3 件の予定の変更」とする。
    /// </summary>
    public string Description => Edits switch
    {
        [var one] when one.Changes.Any(c => c.Field == TaskField.BlockedBy) && one.Changes.Any(c => c.Field is TaskField.Start or TaskField.Target)
            => $"「{one.Title}」の先行タスクと予定の変更",
        [var one] => $"「{one.Title}」の{FieldLabels.Of(PrimaryField(one.Changes))}の変更",
        _ when Edits.FirstOrDefault(e => e.Changes.Any(c => c.Field == TaskField.BlockedBy)) is { } link
            => $"「{link.Title}」の先行タスクの変更と、{Edits.Count(e => e.Changes.Any(c => c.Field is TaskField.Start or TaskField.Target))} 件の予定の変更",
        _ => $"{Edits.Count} 件のタスクの変更",
    };

    /// <summary>
    /// 説明に使う項目。ステータスの変更に伴う開閉状態や実績日、構造の変更に伴う並び順は、主な変更ではないため後回しにする。
    /// </summary>
    private static TaskField PrimaryField(IReadOnlyList<TaskChange> changes) =>
        changes.Select(c => c.Field).FirstOrDefault(f => f is not (TaskField.State or TaskField.ActualStart or TaskField.ActualEnd or TaskField.SortOrder),
            changes[0].Field);
}

/// <summary>
/// 元に戻す・やり直すための編集の履歴（要件 F-UNDO-01）。
/// 変更は項目ごとの「変更前・変更後」の値で覚え、戻すときは、いまの値が変更後のままの項目だけを変更前へ戻す。
/// その後に GitHub や別の操作で変わった項目は、上書きしない。
/// </summary>
public sealed class EditHistory(int capacity = 100)
{
    private readonly List<EditStep> _undo = [];
    private readonly List<EditStep> _redo = [];
    private readonly Lock _lock = new();

    public bool CanUndo
    {
        get { lock (_lock) { return _undo.Count > 0; } }
    }

    public bool CanRedo
    {
        get { lock (_lock) { return _redo.Count > 0; } }
    }

    /// <summary>新しい操作を覚える。やり直しの履歴は捨てる。</summary>
    public void Record(EditStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var edits = step.Edits.Where(e => e.Changes.Count > 0).ToList();
        if (edits.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            Push(_undo, new EditStep(edits));
            _redo.Clear();
        }
    }

    /// <summary>元に戻す操作を取り出す。</summary>
    public EditStep? PopUndo()
    {
        lock (_lock)
        {
            return Pop(_undo);
        }
    }

    /// <summary>やり直す操作を取り出す。</summary>
    public EditStep? PopRedo()
    {
        lock (_lock)
        {
            return Pop(_redo);
        }
    }

    /// <summary>元に戻した操作を、やり直せるように覚える。</summary>
    public void PushRedo(EditStep step)
    {
        lock (_lock)
        {
            Push(_redo, step);
        }
    }

    /// <summary>やり直した操作を、再び元に戻せるように覚える（やり直しの履歴は残す）。</summary>
    public void PushUndo(EditStep step)
    {
        lock (_lock)
        {
            Push(_undo, step);
        }
    }

    /// <summary>GitHub に作成したタスクの ID が、仮の ID から置き換わった。</summary>
    public void ReplaceItemId(string oldItemId, string newItemId)
    {
        lock (_lock)
        {
            foreach (var list in new[] { _undo, _redo })
            {
                for (int i = 0; i < list.Count; i++)
                {
                    list[i] = new EditStep([.. list[i].Edits.Select(e => e.ItemId == oldItemId ? e with { ItemId = newItemId } : e)]);
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _undo.Clear();
            _redo.Clear();
        }
    }

    private void Push(List<EditStep> list, EditStep step)
    {
        list.Add(step);
        if (list.Count > capacity)
        {
            list.RemoveAt(0);
        }
    }

    private static EditStep? Pop(List<EditStep> list)
    {
        if (list.Count == 0)
        {
            return null;
        }

        var step = list[^1];
        list.RemoveAt(list.Count - 1);
        return step;
    }

    /// <summary>
    /// 変更を打ち消す変更。いまの値が変更後のままの項目だけを、最初の変更前の値へ戻す。
    /// 項目の順は元の変更と同じにする。ステータスを開閉状態より先に送らないと、GitHub の Project の自動化
    /// （Status が Done のまま Issue を開き直すと閉じ直す）に打ち消されるため。
    /// </summary>
    public static IReadOnlyList<TaskChange> Inverse(TaskItem current, IReadOnlyList<TaskChange> changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);

        var result = new List<TaskChange>();
        foreach (var group in changes.GroupBy(c => c.Field))
        {
            var original = group.First();
            var latest = group.Last();
            var now = TaskValues.Get(current, latest.Field);
            if (string.Equals(now, latest.NewValue, StringComparison.Ordinal) && !string.Equals(now, original.OldValue, StringComparison.Ordinal))
            {
                result.Add(new TaskChange(latest.Field, now, original.OldValue));
            }
        }

        return result;
    }
}

/// <summary>画面に出す項目の呼び名。</summary>
public static class FieldLabels
{
    public static string Of(TaskField field) => field switch
    {
        TaskField.Title => "タイトル",
        TaskField.Body => "説明",
        TaskField.Status => "ステータス",
        TaskField.Start => "予定開始日",
        TaskField.Target => "予定終了日",
        TaskField.ActualStart => "実績開始日",
        TaskField.ActualEnd => "実績終了日",
        TaskField.Estimate => "想定工数",
        TaskField.Progress => "進捗率",
        TaskField.Kind => "区分",
        TaskField.State => "状態",
        TaskField.Assignees => "担当者",
        TaskField.Parent => "親タスク",
        TaskField.SortOrder => "並び順",
        TaskField.BlockedBy => "先行タスク",
        TaskField.Schedule => "後続を待たせない設定",
        TaskField.Repositories => "リポジトリ",
        TaskField.Branches => "ブランチ",
        TaskField.Milestone => "マイルストーン",
        _ => "項目",
    };
}
