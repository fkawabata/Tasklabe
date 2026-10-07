namespace Tasklabe.Core.Domain;

/// <summary>GitHub Project 上でアプリが用いる名称と記号（要件定義書 6.2 節、技術設計書 3.2.1 節）。</summary>
public static class ProjectConventions
{
    public const string ProjectMarker = "[tasklabe]";
    public const string InboxMarker = "[tasklabe:inbox]";
    public const string InboxTitle = "Inbox";
    public const string DefaultPersonalRepositoryName = "tasklabe-personal";

    public static class Fields
    {
        public const string Status = "Status";
        public const string Start = "Start";
        public const string Target = "Target";
        public const string ActualStart = "Actual Start";
        public const string ActualEnd = "Actual End";
        public const string Estimate = "Estimate";
        public const string Progress = "Progress";
        public const string Kind = "Kind";

        /// <summary>日程の扱い（後続を待たせない、など。要件 F-DEP-07）。</summary>
        public const string Schedule = "Schedule";

        /// <summary>作業するリポジトリ（owner/name をカンマで区切ったテキスト。要件 F-TSK-18）。</summary>
        public const string Repositories = "Repositories";

        /// <summary>作業するブランチ（owner/name:ブランチ名 を空白で区切ったテキスト。要件 F-TSK-19）。</summary>
        public const string Branches = "Branches";
    }

    public static class ScheduleOptions
    {
        /// <summary>後続の開始を待たせない。親の終わりの計算からも外す。</summary>
        public const string NonBlocking = "Non-blocking";
    }

    public static class KindOptions
    {
        public const string Issue = "Issue";
        public const string Task = "Task";
    }

    /// <summary>
    /// 区分の選択肢から種別を読み取る。未設定の場合は、チームプロジェクトでは課題、個人プロジェクトではタスクとする。
    /// ただし、日程や親が入っているものは既に計画へ組み込まれているとみなす。
    /// </summary>
    /// <param name="looksPlanned">予定日か親タスクが設定されているか。</param>
    public static TaskKind ParseKind(string? optionName, ProjectKind projectKind, bool looksPlanned = false) => optionName switch
    {
        KindOptions.Issue => TaskKind.Issue,
        KindOptions.Task => TaskKind.Task,
        _ => projectKind == ProjectKind.Team && !looksPlanned ? TaskKind.Issue : TaskKind.Task,
    };

    public static string KindOption(TaskKind kind) => kind switch
    {
        TaskKind.Issue => KindOptions.Issue,
        _ => KindOptions.Task,
    };

    /// <summary>
    /// 計画のタスクの親は、区分が未設定でも計画として扱う（階層が途切れないようにする）。
    /// 区分を明示的に「課題」としたタスクは変えない。
    /// </summary>
    /// <param name="explicitIssues">区分として「課題」が明示されていたタスクの ID。</param>
    public static IReadOnlyList<TaskItem> KeepPlanHierarchy(IReadOnlyList<TaskItem> tasks, IReadOnlySet<string> explicitIssues)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(explicitIssues);

        var byIssue = tasks.GroupBy(t => t.IssueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var planned = new HashSet<string>(tasks.Where(t => t.IsPlanned).Select(t => t.IssueId), StringComparer.Ordinal);

        // 計画のタスクから親をたどり、途中の課題を計画へ引き上げる
        foreach (var task in tasks.Where(t => t.IsPlanned).ToList())
        {
            var parentId = task.ParentIssueId;
            while (parentId is not null && byIssue.TryGetValue(parentId, out var parent)
                && !planned.Contains(parentId) && !explicitIssues.Contains(parentId))
            {
                planned.Add(parentId);
                parentId = parent.ParentIssueId;
            }
        }

        return [.. tasks.Select(t => t.IsPlanned || !planned.Contains(t.IssueId) ? t : t with { Kind = TaskKind.Task })];
    }

    /// <summary>アプリの管理対象の Project かどうかを説明欄から判定する。</summary>
    public static bool IsManaged(string? shortDescription) =>
        shortDescription is not null
        && (shortDescription.Contains(ProjectMarker, StringComparison.OrdinalIgnoreCase)
            || shortDescription.Contains(InboxMarker, StringComparison.OrdinalIgnoreCase));

    public static bool IsInbox(string? shortDescription) =>
        shortDescription?.Contains(InboxMarker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>説明からカテゴリの記号（以前の記号を含む）を取り除いた、利用者が書いた部分。</summary>
    public static string StripCategoryMarker(string? description)
    {
        var text = description ?? "";
        foreach (var marker in AllMarkers)
        {
            text = text.Replace(marker, "", StringComparison.OrdinalIgnoreCase);
        }

        return text.Trim();
    }

    public static string CategoryMarker(StatusCategory category) => category switch
    {
        StatusCategory.Backlog => "[tasklabe:backlog]",
        StatusCategory.InProgress => "[tasklabe:inprogress]",
        StatusCategory.Done => "[tasklabe:done]",
        StatusCategory.Pending => "[tasklabe:pending]",
        StatusCategory.Canceled => "[tasklabe:canceled]",
        _ => "[tasklabe:todo]",
    };

    /// <summary>カテゴリが 4 つだったときの進行中の記号。Pending も含んでいたため、名前で In Progress と Pending に分けて読む。</summary>
    private const string LegacyDoingMarker = "[tasklabe:doing]";

    private static IEnumerable<string> AllMarkers => StatusCategories.All.Select(CategoryMarker).Append(LegacyDoingMarker);

    /// <summary>
    /// 新しく作る Project の Status の選択肢（名前、色、カテゴリ。要件 F-STS-03）。GitHub 上で自由に足し引き・改名できる。
    /// </summary>
    public static IReadOnlyList<(string Name, string Color, StatusCategory Category)> DefaultStatuses { get; } =
    [
        ("Backlog", "GRAY", StatusCategory.Backlog),
        ("Todo", "BLUE", StatusCategory.Todo),
        ("In Progress", "YELLOW", StatusCategory.InProgress),
        ("Done", "GREEN", StatusCategory.Done),
        ("Pending", "ORANGE", StatusCategory.Pending),
        ("Canceled", "RED", StatusCategory.Canceled),
    ];

    /// <summary>
    /// そのカテゴリへ移すときに使うステータス（カテゴリの代表。要件 F-STS-02、08）。カテゴリの中で並びの最初の選択肢とする。
    /// そのカテゴリの選択肢がなければ、近いカテゴリ（<see cref="StatusCategories.Substitutes"/>）の代表を使う。
    /// </summary>
    public static StatusOption? DefaultStatus(IReadOnlyList<StatusOption> options, StatusCategory category)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var c in StatusCategories.Substitutes(category).Prepend(category))
        {
            if (options.FirstOrDefault(o => o.Category == c) is { } option)
            {
                return option;
            }
        }

        return null;
    }

    private static string Normalize(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// Status の選択肢の説明欄からカテゴリを読み取る（要件 F-STS-07）。記号がない場合は名称から推定する。
    /// カテゴリが 4 つだったときの記号は、名称で Backlog と Pending を分けて読む。
    /// </summary>
    public static StatusCategory ParseCategory(string? description, string name, out bool hasMarker)
    {
        hasMarker = true;
        if (description?.Contains(LegacyDoingMarker, StringComparison.OrdinalIgnoreCase) == true)
        {
            return IsPendingName(name) ? StatusCategory.Pending : StatusCategory.InProgress;
        }

        foreach (var category in StatusCategories.All)
        {
            if (description?.Contains(CategoryMarker(category), StringComparison.OrdinalIgnoreCase) == true)
            {
                return category == StatusCategory.Todo && IsBacklogName(name) ? StatusCategory.Backlog : category;
            }
        }

        hasMarker = false;
        return CategoryOfName(name);
    }

    /// <summary>名称から推定したカテゴリ。</summary>
    public static StatusCategory CategoryOfName(string name)
    {
        if (IsBacklogName(name))
        {
            return StatusCategory.Backlog;
        }

        if (IsPendingName(name))
        {
            return StatusCategory.Pending;
        }

        return Normalize(name) switch
        {
            "DONE" or "COMPLETED" or "完了" => StatusCategory.Done,
            "CANCELED" or "CANCELLED" or "WON'T DO" or "WONT DO" or "中止" or "キャンセル" => StatusCategory.Canceled,
            "IN PROGRESS" or "DOING" or "IN REVIEW" or "REVIEW" or "進行中" or "対応中" or "レビュー" or "レビュー中" => StatusCategory.InProgress,
            _ => StatusCategory.Todo,
        };
    }

    // Todo の記号の Backlog は、カテゴリが 4 つだったときに Todo へまとめていたもの
    private static bool IsBacklogName(string name) => Normalize(name) is "BACKLOG" or "ICEBOX" or "バックログ";

    private static bool IsPendingName(string name) => Normalize(name) is "PENDING" or "ON HOLD" or "BLOCKED" or "WAITING" or "保留" or "待ち";
}
