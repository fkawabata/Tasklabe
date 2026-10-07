using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Keyboard;

namespace Tasklabe.App.Controls;

/// <summary>
/// タスクを追加するときの入力（要件 F-UI-MY-06、UX 規約 UX-08、UX-09、UX-12、UI デザイン設計書 3.5.1 節）。
/// タイトルと説明を主役にし、そのほかの項目は値を載せた小さなボタンから、一覧やキーと同じピッカーで選ぶ。
/// 入力は下書き（<see cref="TaskDraft"/>）に持ち、詳細パネルへそのまま引き継げる。
/// </summary>
public sealed partial class NewTaskComposer : UserControl
{
    private readonly PickerButton _project = Pill("追加先");
    private readonly PickerButton _status = Pill("ステータス");
    private readonly PickerButton _assignee = Pill("担当者");
    private readonly PickerButton _due = Pill("期日");
    private readonly PickerButton _estimate = Pill("工数");
    private readonly PickerButton _kind = Pill("区分");

    public NewTaskComposer()
    {
        InitializeComponent();
        _project.HorizontalAlignment = HorizontalAlignment.Left;
        ProjectHost.Children.Add(_project);
        foreach (var pill in (PickerButton[])[_status, _assignee, _due, _estimate, _kind])
        {
            Pills.Children.Add(pill);
        }

        TitleBox.TextChanged += (_, _) =>
        {
            Draft.Title = TitleBox.Text;
            UpdateHint();
            CanCreateChanged?.Invoke(this, EventArgs.Empty);
        };
        BodyBox.TextChanged += (_, _) => Draft.Body = BodyBox.Text;

        _project.Pick = async b =>
        {
            if (await ValuePickers.ProjectAsync(b, null, "追加先", Draft.Project is { } p ? [p.Id] : [], excludeCurrent: false) is { } picked)
            {
                TaskDetailPane.SetDraftProject(Draft, picked.Value, keepValues: true);
                ShowValues();
            }
        };
        _status.Pick = async b =>
        {
            if (Draft.Project is { } project)
            {
                var current = project.StatusOptions.FirstOrDefault(o => o.Id == Draft.StatusOptionId);
                if (await ValuePickers.StatusAsync(b, null, [project], [(current?.Name, current?.Category ?? StatusCategory.Todo)]) is { } status)
                {
                    Draft.StatusOptionId = status.Value.In(project)?.Id;
                    ShowValues();
                }
            }
        };
        _assignee.Pick = async b =>
        {
            if (Draft.Project is { IsTeam: true } project
                && await ValuePickers.AssigneesAsync(b, null, [project.RepositoryNameWithOwner], [Draft.Assignees]) is { } choice)
            {
                var next = choice.ApplyTo(Draft.Assignees);
                Draft.Assignees.Clear();
                Draft.Assignees.AddRange(next);
                ShowValues();
            }
        };
        _due.Pick = async b =>
        {
            if (await ValuePickers.DateAsync(b, null, "期日（予定終了日）", [Draft.Target]) is { } picked)
            {
                Draft.Target = picked.Value;
                ShowValues();
            }
        };
        _estimate.Pick = async b =>
        {
            if (await ValuePickers.EstimateAsync(b, null, [Draft.EstimateHours]) is { } picked)
            {
                Draft.EstimateHours = picked.Value;
                ShowValues();
            }
        };
        _kind.Pick = async b =>
        {
            if (await ValuePickers.KindAsync(b, null, [Draft.Kind]) is { } picked)
            {
                Draft.Kind = picked.Value;
                ShowValues();
            }
        };

        // 詳細パネルへ移るときに、最後にいた項目へフォーカスを置けるよう覚える（UX-09）
        foreach (var (element, field) in ((Control, string)[])
            [(TitleBox, "title"), (BodyBox, "body"), (_project, "project"), (_status, "status"), (_assignee, "assign"),
             (_due, "due"), (_estimate, "estimate"), (_kind, "kind")])
        {
            element.GotFocus += (_, _) => Draft.FocusField = field;
        }

        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnComposerPreviewKeyDown), true);
        UpdateKeyHints();
    }

    /// <summary>タイトルの有無が変わった。ダイアログの「追加」の有効・無効に使う。</summary>
    public event EventHandler? CanCreateChanged;

    /// <summary>キー操作で追加を求められた（Ctrl + Enter など）。</summary>
    public event EventHandler? SubmitRequested;

    /// <summary>「詳細を入力…」（Ctrl + D）で、詳細パネルへ移ることを求められた。</summary>
    public event EventHandler? DetailRequested;

    /// <summary>入力中の下書き。</summary>
    public TaskDraft Draft { get; private set; } = new();

    public bool CanCreate => Draft.CanCreate;

    /// <summary>始めた場所の文脈から既定値を入れる（UX-12）。</summary>
    internal void Load(IReadOnlyList<Project> projects, NewTaskContext context)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(context);

        var project = projects.FirstOrDefault(p => p.Id == context.ProjectId)
            ?? projects.FirstOrDefault(p => p.Kind == ProjectKind.Inbox)
            ?? projects[0];
        var draft = new TaskDraft { Target = context.Due };
        TaskDetailPane.SetDraftProject(draft, project, keepValues: false);
        if (context.Status?.Invoke(project) is { } status)
        {
            draft.StatusOptionId = status.Id;
        }

        if (project.IsTeam)
        {
            draft.Kind = context.Kind ?? draft.Kind;
            if (context.Assignee is { Length: > 0 } assignee)
            {
                draft.Assignees.Add(assignee);
            }

            if (context.ParentIssueId is { } parent)
            {
                draft.ParentIssueId = parent;
                draft.Kind = TaskKind.Task;
            }
        }

        Draft = draft;
        ShowValues();
        UpdateHint();
    }

    public void FocusTitle() => TitleBox.Focus(FocusState.Programmatic);

    private static PickerButton Pill(string name)
    {
        var pill = new PickerButton(name)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 3, 8, 4),
            MinHeight = 0,
            FontSize = 12,
        };
        Controls.Pill.SetIsEnabled(pill, true);
        return pill;
    }

    /// <summary>下書きの値をボタンに示す。値がないものは項目名を薄く示す。</summary>
    private void ShowValues()
    {
        var draft = Draft;
        var project = draft.Project;
        bool team = project?.IsTeam == true;
        _project.SetValue(project is null ? null : ProjectDisplay.Name(project),
            project?.Kind switch { ProjectKind.Inbox => "", ProjectKind.Team => "", _ => "" });

        var status = project?.StatusOptions.FirstOrDefault(o => o.Id == draft.StatusOptionId);
        _status.SetValue(status?.Name, StatusVisuals.Glyph(status?.Category ?? StatusCategory.Todo), StatusVisuals.BrushKey(status?.Category ?? StatusCategory.Todo), "ステータス");
        _assignee.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
        _assignee.SetValue(draft.Assignees.Count switch
        {
            0 => null,
            1 => "@" + draft.Assignees[0],
            var n => $"@{draft.Assignees[0]} +{n - 1}",
        }, "", placeholder: "担当");
        _due.SetValue(draft.Target is { } due ? DateText.Short(due) : null, "", placeholder: "期日");
        _estimate.SetValue(EffortText.Of(draft.EstimateHours), "", placeholder: "工数");
        _kind.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
        _kind.SetValue(KindVisuals.Name(draft.Kind));
        KindVisuals.Apply(_kind.Icon, draft.Kind);
        _kind.Icon.Visibility = Visibility.Visible;
        CanCreateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- キーボード（UX 規約 UX-13、UX-17）

    /// <summary>各項目のツールチップに、いまのキーの割り当てを示す。</summary>
    private void UpdateKeyHints()
    {
        foreach (var (pill, id, name) in ((PickerButton, string, string)[])
            [(_project, "composer.project", "追加先"), (_status, "composer.status", "ステータス"), (_assignee, "composer.assign", "担当者"),
             (_due, "composer.due", "期日"), (_estimate, "composer.estimate", "工数"), (_kind, "composer.kind", "区分")])
        {
            ToolTipService.SetToolTip(pill, KeyHints.Tip(id, name));
        }

        ToolTipService.SetToolTip(DetailButton, KeyHints.Tip("composer.detail", "詳細パネルで入力を続ける"));
    }

    /// <summary>下端の案内。追加できないときは、その理由を示す（UX-27）。</summary>
    private void UpdateHint()
    {
        var keymap = App.Current.Services.Keymap;
        var submit = keymap.Display("composer.submit");
        var detail = keymap.Display("composer.detail");
        HintText.Text = (Draft.Title.Trim().Length == 0 ? "タスク名を入力すると追加できます　" : $"{submit} で追加　")
            + $"{detail} で詳細　Tab で項目を移動　Alt + S などで項目を選ぶ";
        HintText.Visibility = Hints.Shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDetailClick(object sender, RoutedEventArgs e) => DetailRequested?.Invoke(this, EventArgs.Empty);

    private async void OnComposerPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (KeyInput.From(e.Key) is not { } gesture
            || App.Current.Services.Keymap.Find(gesture, ShortcutScope.Composer) is not { } action)
        {
            return;
        }

        e.Handled = true;
        PickerButton? pill = action.Id switch
        {
            "composer.project" => _project,
            "composer.status" => _status,
            "composer.assign" => _assignee,
            "composer.due" => _due,
            "composer.estimate" => _estimate,
            "composer.kind" => _kind,
            _ => null,
        };

        switch (action.Id)
        {
            case "composer.submit":
                SubmitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "composer.detail":
                DetailRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (pill is { Visibility: Visibility.Visible, Pick: { } pick })
                {
                    pill.Focus(FocusState.Keyboard);
                    await pick(pill);
                }

                break;
        }
    }
}
