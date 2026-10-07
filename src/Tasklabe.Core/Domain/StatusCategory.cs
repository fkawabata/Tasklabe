namespace Tasklabe.Core.Domain;

/// <summary>
/// ステータスのカテゴリ（要件 F-STS-01）。ステータス名はプロジェクトごとに自由だが、集計とプロジェクトをまたぐ表示はこのカテゴリで行う。
/// カテゴリどうしはまとめず、それぞれを独立に扱う。値はキャッシュに保存するため、既存の値を変えない。
/// </summary>
public enum StatusCategory
{
    /// <summary>まだ取りかかる予定のないもの。</summary>
    Backlog = 4,

    /// <summary>取りかかる予定のもの。</summary>
    Todo = 0,

    /// <summary>取りかかっているもの。</summary>
    InProgress = 1,

    /// <summary>完了。Issue を Close する。</summary>
    Done = 2,

    /// <summary>取りかかった後で止めているもの。</summary>
    Pending = 5,

    /// <summary>中止。Issue を「対応しない」として Close し、進捗・工数の集計から外す。</summary>
    Canceled = 3,
}

/// <summary>ステータスのカテゴリの並びと、カテゴリどうしの関係。</summary>
public static class StatusCategories
{
    /// <summary>表示の順（Backlog → Todo → In Progress → Done → Pending → Canceled）。</summary>
    public static IReadOnlyList<StatusCategory> All { get; } =
    [
        StatusCategory.Backlog,
        StatusCategory.Todo,
        StatusCategory.InProgress,
        StatusCategory.Done,
        StatusCategory.Pending,
        StatusCategory.Canceled,
    ];

    /// <summary>表示の順での位置。</summary>
    public static int Rank(StatusCategory category) => category switch
    {
        StatusCategory.Backlog => 0,
        StatusCategory.Todo => 1,
        StatusCategory.InProgress => 2,
        StatusCategory.Done => 3,
        StatusCategory.Pending => 4,
        _ => 5,
    };

    /// <summary>まだ取りかかっていないカテゴリか。</summary>
    public static bool IsNotStarted(StatusCategory category) =>
        category is StatusCategory.Backlog or StatusCategory.Todo;

    /// <summary>
    /// そのカテゴリのステータスがないプロジェクトで、代わりに使うカテゴリの順。Done と Canceled には代わりを置かない。
    /// </summary>
    public static IReadOnlyList<StatusCategory> Substitutes(StatusCategory category) => category switch
    {
        StatusCategory.Backlog => [StatusCategory.Todo],
        StatusCategory.Todo => [StatusCategory.Backlog],
        StatusCategory.InProgress => [StatusCategory.Pending, StatusCategory.Todo],
        StatusCategory.Pending => [StatusCategory.InProgress],
        _ => [],
    };
}
