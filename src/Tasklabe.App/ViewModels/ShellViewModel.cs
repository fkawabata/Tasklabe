using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Abstractions;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Data;

namespace Tasklabe.App.ViewModels;

public sealed record ProjectNavItem(string Id, string Name, bool IsTeam)
{
    public static ProjectNavItem From(Project p) => new(p.Id, p.Title, p.IsTeam);
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;

    public ShellViewModel(AppServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;

        var sync = services.Sync;
        sync.StatusChanged += (_, status) => _dispatcher.TryEnqueue(() => ApplyStatus(status));
        sync.DataChanged += (_, _) => _dispatcher.TryEnqueue(() => _ = ReloadAsync());
        sync.ConflictDetected += (_, c) => _dispatcher.TryEnqueue(() => ShowConflict(c));
        sync.SendFailed += (_, _) => _dispatcher.TryEnqueue(() => _ = RefreshFailuresAsync(announce: true));
        sync.OrganizationProblemDetected += (_, org) => _dispatcher.TryEnqueue(() => ShowOrganizationProblem(org));
        services.Edits.Changed += (_, task) => _dispatcher.TryEnqueue(() => OnLocalEdit(task));
        services.Store.ItemIdReplaced += (_, ids) => _dispatcher.TryEnqueue(() => OnItemIdReplaced(ids.OldItemId, ids.NewItemId));

        ApplyStatus(sync.Status);
    }

    /// <summary>キャッシュの内容が変わり、画面の再読み込みが必要になった。</summary>
    public event EventHandler? DataChanged;

    /// <summary>ナビゲーションに表示するプロジェクト（Inbox を除く）。</summary>
    public ObservableCollection<ProjectNavItem> Projects { get; } = [];

    // ---------------------------------------------------------------- 同期状態（要件 F-SYNC-05）

    [ObservableProperty]
    public partial string SyncStatusText { get; set; } = "";

    [ObservableProperty]
    public partial string SyncStatusGlyph { get; set; } = "";

    [ObservableProperty]
    public partial string SyncStatusDetail { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    // ---------------------------------------------------------------- 詳細ペイン

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial TaskItem? SelectedTask { get; set; }

    /// <summary>詳細パネルで作成中のタスク（UX 規約 UX-09）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen), nameof(IsCreating))]
    public partial Controls.TaskDraft? Draft { get; set; }

    public bool IsDetailOpen => SelectedTask is not null || Draft is not null;

    /// <summary>詳細パネルでタスクを作成中か。</summary>
    public bool IsCreating => Draft is not null;

    /// <summary>
    /// 詳細パネルでタスクを開く（null なら閉じる）。開いたまま中身を替えるときに、いったん閉じたとみなされない
    /// （<see cref="IsDetailOpen"/> が途中で false にならない）よう、新しい中身を先に置く。
    /// </summary>
    public void SelectTask(TaskItem? task)
    {
        SelectedTask = task;
        Draft = null;
    }

    /// <summary>詳細パネルでタスクを開き、サブタスクの入力欄にフォーカスを置く。</summary>
    public event EventHandler<TaskItem>? SubtaskRequested;

    public void RequestSubtask(TaskItem task)
    {
        SelectTask(task);
        SubtaskRequested?.Invoke(this, task);
    }

    /// <summary>詳細パネルで、下書きからタスクの作成を続ける。</summary>
    public void StartCreating(Controls.TaskDraft draft)
    {
        Draft = draft;
        SelectedTask = null;
    }

    // ---------------------------------------------------------------- 同期の問題（UX 規約 UX-31）

    /// <summary>
    /// 続いている同期の問題（送信できない変更、取得できない Organization）。
    /// 画面に帯を差し込まず、タイトルバーの同期の状態をチップの形にして示し、押すと詳細と操作を開く。
    /// </summary>
    public IReadOnlyList<Controls.ChipSection> SyncProblems { get; private set; } = [];

    /// <summary>同期の問題があるか。あるときは、同期の状態を押すと同期ではなく詳細を開く。</summary>
    [ObservableProperty]
    public partial bool HasSyncProblems { get; set; }

    /// <summary>同期の問題の重さ（同期の状態のチップの色）。</summary>
    [ObservableProperty]
    public partial Controls.ChipSeverity SyncProblemSeverity { get; set; }

    /// <summary>同期の問題の詳細を開くよう求めた（下端の知らせの「詳しく見る」など）。</summary>
    public event EventHandler? SyncDetailsRequested;

    private IReadOnlyList<Controls.ChipSection> _failureSections = [];
    private readonly Dictionary<string, Controls.ChipSection> _organizationSections = [];

    private void UpdateSyncProblems()
    {
        SyncProblems = [.. _failureSections, .. _organizationSections.Values];
        SyncProblemSeverity = _failureSections.Count > 0 ? Controls.ChipSeverity.Critical : Controls.ChipSeverity.Caution;
        HasSyncProblems = SyncProblems.Count > 0;
        OnPropertyChanged(nameof(SyncProblems));
    }


    // ---------------------------------------------------------------- 下端の知らせ（UX 規約 UX-26）

    [ObservableProperty]
    public partial bool IsToastOpen { get; set; }

    [ObservableProperty]
    public partial string ToastTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ToastMessage { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity ToastSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string ToastActionText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasToastAction { get; set; }

    private Func<Task>? _toastAction;
    private Debouncer? _toastTimer;

    /// <summary>
    /// 画面の外へ影響が及ぶ変更（別のプロジェクトへ移す、計画に移す、一括の変更など）を、下端に「元に戻す」を添えて知らせる。
    /// </summary>
    public void ShowUndoable(string message, Func<Task> undo) => ShowToast(message, "元に戻す", undo);

    /// <summary>下端に短いあいだだけ知らせる。操作を添えるときは、その操作のボタンを置く。</summary>
    public void ShowToast(string message, string? actionText = null, Func<Task>? action = null) =>
        ShowToast("", message, InfoBarSeverity.Informational, actionText, action);

    private void ShowToast(string title, string message, InfoBarSeverity severity, string? actionText = null, Func<Task>? action = null)
    {
        ToastTitle = title;
        ToastSeverity = severity;
        ToastMessage = message;
        ToastActionText = actionText ?? "";
        HasToastAction = action is not null;
        _toastAction = action;
        IsToastOpen = true;
        _toastTimer ??= new Debouncer(_dispatcher, TimeSpan.FromSeconds(8));
        _toastTimer.Run(() => IsToastOpen = false);
        if (_toastHeld)
        {
            _toastTimer.Cancel();
        }
    }

    private bool _toastHeld;

    /// <summary>
    /// 知らせにポインターかフォーカスがあるあいだは、時間で閉じない（UX 規約 UX-26）。読んでいる途中や押そうとしているあいだに
    /// 消えないようにするためである。離れたら、そこから改めて時間を数える。
    /// </summary>
    public void HoldToast(bool held)
    {
        _toastHeld = held;
        if (held)
        {
            _toastTimer?.Cancel();
        }
        else if (IsToastOpen)
        {
            _toastTimer?.Run(() => IsToastOpen = false);
        }
    }

    [RelayCommand]
    private async Task RunToastActionAsync()
    {
        IsToastOpen = false;
        if (Interlocked.Exchange(ref _toastAction, null) is { } action)
        {
            await action();
        }
    }

    /// <summary>
    /// プロジェクトを別のプロジェクトの前か後ろへ移し、並びを覚える（要件 F-UI-NAV-02）。
    /// 一覧は Move の通知だけで変わるため、ナビゲーションは項目を作り直さずに動かせる。
    /// </summary>
    public void MoveProject(string moved, string target, bool after)
    {
        var ids = Projects.Select(p => p.Id).ToList();
        var order = Core.Settings.ProjectOrder.Move(ids, moved, target, after);
        if (order.SequenceEqual(ids))
        {
            return;
        }

        _services.CurrentSettings.ProjectOrder = [.. order];
        _services.SaveSettings();
        Projects.Move(ids.IndexOf(moved), order.ToList().IndexOf(moved));
    }

    public async Task ReloadAsync()
    {
        var projects = await _services.Store.GetProjectsAsync();
        TaskKeys.Update(projects);
        var items = Core.Settings.ProjectOrder.Apply(
            projects.Where(p => p.Kind != ProjectKind.Inbox && !p.Closed).Select(ProjectNavItem.From),
            i => i.Id,
            _services.CurrentSettings.ProjectOrder).ToList();

        if (!items.SequenceEqual(Projects))
        {
            Projects.Clear();
            foreach (var item in items)
            {
                Projects.Add(item);
            }
        }

        if (SelectedTask is { } selected)
        {
            SelectedTask = await _services.Store.GetTaskAsync(selected.ItemId);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>表示の設定が変わったときなど、開いている画面に描き直しを促す。</summary>
    public void NotifyDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>利用者が求めた同期。GitHub で変えたステータスの選択肢なども取り込めるよう、すべてを取り直す。</summary>
    [RelayCommand]
    private Task SyncNowAsync() => _services.Sync.SyncAsync(force: true);

    private int _failedCount;

    /// <summary>送信失敗の変更を送り直す（要件 F-SYNC-06）。</summary>
    private async Task RetryFailedAsync()
    {
        await _services.Edits.RetryFailedAsync();
        await RefreshFailuresAsync();
    }

    /// <summary>送信失敗の変更を捨て、影響するプロジェクトを取り直す（要件 F-SYNC-06）。</summary>
    private async Task DiscardFailedAsync()
    {
        await _services.Edits.DiscardFailedAsync();
        await RefreshFailuresAsync();
    }

    /// <summary>
    /// 送信できない変更を読み直し、同期の状態のチップに反映する。
    /// 新しく増えたときは、下端の知らせで伝え、そこから詳細を開けるようにする。
    /// </summary>
    public async Task RefreshFailuresAsync(bool announce = false)
    {
        var failed = await _services.Store.GetFailedAsync();
        if (failed.Count == 0)
        {
            _failureSections = [];
            UpdateSyncProblems();
            return;
        }

        // どのタスクのどの変更が、なぜ送れなかったかを並べる（多いときは先頭の 5 件）
        var lines = new List<string>();
        foreach (var entry in failed.Take(5))
        {
            var taskTitle = (await _services.Store.GetTaskAsync(entry.ItemId))?.Title ?? "（削除したタスク）";
            var what = entry.Kind switch
            {
                OutboxKind.Create => "作成",
                OutboxKind.Delete => "削除",
                OutboxKind.Move => "別のプロジェクトへの移動",
                _ => entry.Field is { } field ? FieldLabels.Of(field) + "の変更" : "変更",
            };
            lines.Add($"「{taskTitle}」の{what}: {entry.LastError}");
        }

        if (failed.Count > lines.Count)
        {
            lines.Add($"ほか {failed.Count - lines.Count} 件");
        }

        var title = $"{failed.Count} 件の変更を GitHub に送信できませんでした";
        _failureSections =
        [
            new Controls.ChipSection(Controls.ChipSeverity.Critical, title,
                "権限や招待が原因のときは、解決してから再送してください。不要なら、変更を破棄して GitHub の内容に戻せます。",
                lines,
                [
                    new Controls.ChipAction("変更を破棄", () => _ = DiscardFailedAsync()),
                    new Controls.ChipAction("再送する", () => _ = RetryFailedAsync(), Primary: true),
                ]),
        ];
        UpdateSyncProblems();
        if (announce)
        {
            ShowToast(title, "", InfoBarSeverity.Error, "詳しく見る", RequestSyncDetails);
        }
    }

    private Task RequestSyncDetails()
    {
        SyncDetailsRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>競合は起きたときに知らせるだけで続く状態ではないため、下端の知らせで伝える。</summary>
    private void ShowConflict(TaskConflict conflict)
    {
        AppLog.Info($"競合: {conflict.ItemId} {conflict.Field} GitHub={conflict.RemoteValue ?? "(null)"} この PC={conflict.LocalValue ?? "(null)"}");
        var message = conflict.Field is TaskField.Status or TaskField.Body or TaskField.Parent or TaskField.BlockedBy
            ? $"「{conflict.TaskTitle}」の{FieldLabels.Of(conflict.Field)}は GitHub 上で変更されていましたが、この PC での変更で上書きしました。"
            : $"「{conflict.TaskTitle}」の{FieldLabels.Of(conflict.Field)}は GitHub 上で「{Display(conflict.RemoteValue)}」に変更されていましたが、"
                + $"この PC での変更「{Display(conflict.LocalValue)}」で上書きしました。";
        ShowToast("他の変更を上書きしました", message, InfoBarSeverity.Warning);
    }

    /// <summary>
    /// 直前の操作を元に戻す（redo なら、元に戻した操作をやり直す）。要件 F-UNDO-01。
    /// 何を戻したかを知らせ、詳細ペインで開いているタスクなら表示を戻した値にする。
    /// </summary>
    public async Task UndoAsync(bool redo)
    {
        var outcome = redo ? await _services.Edits.RedoAsync() : await _services.Edits.UndoAsync();
        if (outcome is null)
        {
            ShowToast(redo ? "やり直せる操作はありません" : "元に戻せる操作はありません");
            return;
        }

        // 操作の結果は下端に知らせ、反対の操作をその場で選べるようにする（UX 規約 UX-26）
        ShowToast(
            (redo ? "やり直しました: " : "元に戻しました: ") + outcome.Description,
            redo ? "元に戻す" : "やり直す",
            () => UndoAsync(!redo));
    }

    /// <summary>操作できなかった理由などを、下端の知らせで伝える。</summary>
    public void ShowInfo(string title, string message) => ShowToast(title, message, InfoBarSeverity.Informational);

    /// <summary>
    /// Organization のプロジェクトを取得できない理由と対処を、同期の状態のチップに加える（要件 M3）。
    /// 初めて見つけたときは、下端の知らせでも伝える。
    /// </summary>
    private void ShowOrganizationProblem(Organization org)
    {
        var title = $"{org.DisplayName} のプロジェクトを取得できません";
        List<Controls.ChipAction> actions = [];
        if (Views.Dialogs.NewProjectDialog.ActionUrl(org) is { } url)
        {
            actions.Add(new Controls.ChipAction(Views.Dialogs.NewProjectDialog.ActionLabel(org), () => Browser.Open(url.ToString()), Primary: true));
        }

        bool known = _organizationSections.ContainsKey(org.Login);
        _organizationSections[org.Login] = new Controls.ChipSection(Controls.ChipSeverity.Caution, title,
            Views.Dialogs.NewProjectDialog.ProblemAdvice(org), [], actions);
        UpdateSyncProblems();
        if (!known)
        {
            ShowToast(title, "", InfoBarSeverity.Warning, "詳しく見る", RequestSyncDetails);
        }
    }

    private void OnLocalEdit(TaskItem? task)
    {
        if (task is not null && SelectedTask?.ItemId == task.ItemId)
        {
            SelectedTask = task;
        }

        _ = ReloadAsync();
    }

    private void OnItemIdReplaced(string oldItemId, string newItemId)
    {
        if (SelectedTask?.ItemId == oldItemId)
        {
            _ = SelectByIdAsync(newItemId);
        }
    }

    private async Task SelectByIdAsync(string itemId) => SelectedTask = await _services.Store.GetTaskAsync(itemId);

    private void ApplyStatus(SyncStatus status)
    {
        IsSyncing = status.State == SyncState.Syncing;
        // 送信できない変更が解消された（再送できた、別の PC で直したなど）ら、チップからも外す
        if (_failedCount > 0 && status.Failed == 0)
        {
            _ = RefreshFailuresAsync();
        }

        _failedCount = status.Failed;
        (SyncStatusGlyph, SyncStatusText) = status switch
        {
            { Failed: > 0 } => ("\uE7BA", $"送信失敗 {status.Failed} 件"),
            { State: SyncState.Syncing } => ("\uE895", "同期中"),
            { State: SyncState.Offline, Pending: > 0 } => ("\uF384", $"オフライン・送信待ち {status.Pending} 件"),
            { State: SyncState.Offline } => ("\uF384", "オフライン"),
            { State: SyncState.Error } => ("\uE7BA", "同期できません"),
            { Pending: > 0 } => ("\uE895", $"送信待ち {status.Pending} 件"),
            // 一部のプロジェクトを取り込めなかった: 同期は続いているのでエラーにはせず、理由はツールチップで示す
            { State: SyncState.Idle, Message: not null, LastSyncedAt: { } partial } => ("\uE7BA", $"同期済み {partial.ToLocalTime():HH:mm}（一部取得できず）"),
            { LastSyncedAt: { } at } => ("\uE73E", $"同期済み {at.ToLocalTime():HH:mm}"),
            _ => ("\uE895", "同期前"),
        };
        SyncStatusDetail = status.Message ?? Controls.KeyHints.Tip("app.sync", "今すぐ同期");
    }

    private static string Display(string? value) => string.IsNullOrEmpty(value) ? "（なし）" : value.Length > 30 ? value[..30] + "…" : value;
}
