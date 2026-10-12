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
    public void ShowUndoable(string message, Func<Task> undo) => ShowToast(message, Texts.Get("Common_Undo"), undo);

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
        TaskKeys.Update(projects, await _services.Store.GetTasksAsync());
        if (projects.Any(p => p.Kind != ProjectKind.Inbox && ProjectKey.SavedKey(p) is null))
        {
            _ = FixMissingKeysAsync();
        }

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

    private bool _fixingKeys;

    /// <summary>キーを保存していないプロジェクトに、提案したキーを保存して固定する（要件 F-TSK-15）。固定できたら読み直す。</summary>
    private async Task FixMissingKeysAsync()
    {
        if (_fixingKeys)
        {
            return;
        }

        _fixingKeys = true;
        try
        {
            if (await Task.Run(() => _services.Workspace.FixMissingKeysAsync()))
            {
                await ReloadAsync();
            }
        }
        catch (Exception ex) when (ex is Tasklabe.GitHub.GitHubException or HttpRequestException or OperationCanceledException)
        {
            // 固定できなくても、提案したキーで番号は表示できる
        }
        finally
        {
            _fixingKeys = false;
        }
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
            var taskTitle = (await _services.Store.GetTaskAsync(entry.ItemId))?.Title ?? Texts.Get("Failure_DeletedTask");
            lines.Add(entry.Kind switch
            {
                OutboxKind.Create => Texts.Format("Failure_Create", taskTitle, entry.LastError),
                OutboxKind.Delete => Texts.Format("Failure_Delete", taskTitle, entry.LastError),
                OutboxKind.Move => Texts.Format("Failure_Move", taskTitle, entry.LastError),
                _ => entry.Field is { } field
                    ? Texts.Format("Failure_Field", taskTitle, FieldLabels.Of(field), entry.LastError)
                    : Texts.Format("Failure_Change", taskTitle, entry.LastError),
            });
        }

        if (failed.Count > lines.Count)
        {
            lines.Add(Texts.Format("Delete_More", failed.Count - lines.Count));
        }

        var title = Texts.Count("Failure_Title", failed.Count);
        _failureSections =
        [
            new Controls.ChipSection(Controls.ChipSeverity.Critical, title,
                Texts.Get("Failure_Message"),
                lines,
                [
                    new Controls.ChipAction(Texts.Get("Failure_Discard"), () => _ = DiscardFailedAsync()),
                    new Controls.ChipAction(Texts.Get("Failure_Retry"), () => _ = RetryFailedAsync(), Primary: true),
                ]),
        ];
        UpdateSyncProblems();
        if (announce)
        {
            ShowToast(title, "", InfoBarSeverity.Error, Texts.Get("Common_Details"), RequestSyncDetails);
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
            ? Texts.Format("Conflict_Overwritten", conflict.TaskTitle, FieldLabels.Of(conflict.Field))
            : Texts.Format("Conflict_OverwrittenValues", conflict.TaskTitle, FieldLabels.Of(conflict.Field), Display(conflict.RemoteValue), Display(conflict.LocalValue));
        ShowToast(Texts.Get("Conflict_OverwrittenTitle"), message, InfoBarSeverity.Warning);
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
            ShowToast(redo ? Texts.Get("Undo_NothingToRedo") : Texts.Get("Undo_NothingToUndo"));
            return;
        }

        // 操作の結果は下端に知らせ、反対の操作をその場で選べるようにする（UX 規約 UX-26）
        ShowToast(
            redo ? Texts.Format("Undo_Redone", outcome.Description) : Texts.Format("Undo_Undone", outcome.Description),
            redo ? Texts.Get("Common_Undo") : Texts.Get("Common_Redo"),
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
        var title = Texts.Format("Org_FetchFailed", org.DisplayName);
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
            ShowToast(title, "", InfoBarSeverity.Warning, Texts.Get("Common_Details"), RequestSyncDetails);
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
            { Failed: > 0 } => ("\uE7BA", Texts.Format("Sync_Failed", status.Failed)),
            { State: SyncState.Syncing } => ("\uE895", Texts.Get("Sync_Syncing")),
            { State: SyncState.Offline, Pending: > 0 } => ("\uF384", Texts.Format("Sync_OfflinePending", status.Pending)),
            { State: SyncState.Offline } => ("\uF384", Texts.Get("Sync_Offline")),
            { State: SyncState.Error } => ("\uE7BA", Texts.Get("Sync_Error")),
            { Pending: > 0 } => ("\uE895", Texts.Format("Sync_Pending", status.Pending)),
            // 一部のプロジェクトを取り込めなかった: 同期は続いているのでエラーにはせず、理由はツールチップで示す
            { State: SyncState.Idle, Message: not null, LastSyncedAt: { } partial } => ("\uE7BA", Texts.Format("Sync_SyncedPartial", partial.ToLocalTime())),
            { LastSyncedAt: { } at } => ("\uE73E", Texts.Format("Sync_Synced", at.ToLocalTime())),
            _ => ("\uE895", Texts.Get("Sync_NotYet")),
        };
        SyncStatusDetail = status.Message ?? Controls.KeyHints.Tip("app.sync");
    }

    private static string Display(string? value) => string.IsNullOrEmpty(value) ? Texts.Get("Conflict_Empty") : value.Length > 30 ? value[..30] + "…" : value;
}
