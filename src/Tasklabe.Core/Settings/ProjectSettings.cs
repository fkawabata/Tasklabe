using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Settings;

/// <summary>工数の表示の単位（要件 F-PRG-01）。</summary>
public enum EffortUnit
{
    Hours,
    Days,
}

/// <summary>プロジェクトの Status の選択肢の編集内容。Id が null のものは新しく足す（要件 F-SET-03）。</summary>
public sealed record StatusOptionEdit(string? Id, string Name, string Color, StatusCategory Category);

/// <summary>新しいプロジェクトの Status の選択肢のひな形。</summary>
public sealed record StatusTemplate(string Name, string Color, StatusCategory Category);

/// <summary>
/// プロジェクトで使う設定の値（要件 F-SET-02）。既定と上書きを重ねた結果。
/// </summary>
/// <param name="HoursPerDay">1 日の稼働時間。人日の換算と、担当者の負荷の上限に使う。</param>
/// <param name="NewTaskKind">新しく追加するタスクの区分の既定（チームのプロジェクトで使う）。</param>
public sealed record ResolvedSettings(EffortUnit EffortUnit, double HoursPerDay, WorkCalendarRules Calendar, TaskKind NewTaskKind)
{
    /// <summary>工数を、設定に従って「12.5h」または「1.6 人日」の形で表す。</summary>
    public string FormatEffort(double hours) => EffortUnit == EffortUnit.Days
        ? string.Create(CultureInfo.InvariantCulture, $"{hours / (HoursPerDay > 0 ? HoursPerDay : 8):0.#} 人日")
        : string.Create(CultureInfo.InvariantCulture, $"{hours:0.#}h");
}

/// <summary>
/// 個人またはチームのプロジェクトの既定（要件 F-SET-01）。この PC に保存し、プロジェクトの設定で上書きする。
/// </summary>
/// <param name="Statuses">新しいプロジェクトの Status の選択肢。</param>
public sealed record DefaultSettings(
    EffortUnit EffortUnit, double HoursPerDay, WorkCalendarRules Calendar, TaskKind NewTaskKind, IReadOnlyList<StatusTemplate> Statuses)
{
    public static IReadOnlyList<StatusTemplate> StandardStatuses { get; } =
        [.. ProjectConventions.DefaultStatuses.Select(s => new StatusTemplate(s.Name, s.Color, s.Category))];

    /// <summary>個人のプロジェクトの既定。区分は常にタスク。</summary>
    public static DefaultSettings Personal { get; } =
        new(EffortUnit.Hours, 8, WorkCalendarRules.Standard, TaskKind.Task, StandardStatuses);

    /// <summary>チームのプロジェクトの既定。新しいタスクは課題として入れる（要件 F-TSK-11）。</summary>
    public static DefaultSettings Team { get; } =
        new(EffortUnit.Hours, 8, WorkCalendarRules.Standard, TaskKind.Issue, StandardStatuses);

    public ResolvedSettings Resolved => new(EffortUnit, HoursPerDay, Calendar, NewTaskKind);

    public string ToJson()
    {
        var node = ProjectSettings.ToNode(new ProjectSettings
        {
            EffortUnit = EffortUnit,
            HoursPerDay = HoursPerDay,
            Calendar = Calendar,
            NewTaskKind = NewTaskKind,
        });
        var statuses = new JsonArray();
        foreach (var s in Statuses)
        {
            statuses.Add((JsonNode)new JsonObject { ["name"] = s.Name, ["color"] = s.Color, ["category"] = CategoryName(s.Category) });
        }

        node["statuses"] = statuses;
        return node.ToJsonString();
    }

    /// <summary>保存した形から読む。読めない項目は <paramref name="fallback"/> の値とする。</summary>
    public static DefaultSettings Parse(string? json, DefaultSettings fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (ProjectSettings.TryParseNode(json) is not { } node)
        {
            return fallback;
        }

        var values = ProjectSettings.FromNode(node);
        var statuses = new List<StatusTemplate>();
        if (node["statuses"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                if (item["name"]?.GetValue<string>() is { Length: > 0 } name && ParseCategory(item["category"]?.GetValue<string>(), name) is { } category)
                {
                    statuses.Add(new StatusTemplate(name.Trim(), item["color"]?.GetValue<string>() ?? "GRAY", category));
                }
            }
        }

        return new DefaultSettings(
            values.EffortUnit ?? fallback.EffortUnit,
            values.HoursPerDay ?? fallback.HoursPerDay,
            values.Calendar ?? fallback.Calendar,
            values.NewTaskKind ?? fallback.NewTaskKind,
            StatusTemplates.IsUsable(statuses) ? statuses : fallback.Statuses);
    }

    internal static string CategoryName(StatusCategory category) => category switch
    {
        StatusCategory.Backlog => "backlog",
        StatusCategory.InProgress => "inprogress",
        StatusCategory.Done => "done",
        StatusCategory.Pending => "pending",
        StatusCategory.Canceled => "canceled",
        _ => "todo",
    };

    /// <summary>保存したカテゴリの名前を読む。カテゴリが 4 つだったときの todo と doing は、ステータス名で Backlog と Pending を分ける。</summary>
    internal static StatusCategory? ParseCategory(string? category, string statusName) => category switch
    {
        "backlog" => StatusCategory.Backlog,
        "todo" => ProjectConventions.CategoryOfName(statusName) == StatusCategory.Backlog ? StatusCategory.Backlog : StatusCategory.Todo,
        "inprogress" => StatusCategory.InProgress,
        "doing" => ProjectConventions.CategoryOfName(statusName) == StatusCategory.Pending ? StatusCategory.Pending : StatusCategory.InProgress,
        "done" => StatusCategory.Done,
        "pending" => StatusCategory.Pending,
        "canceled" => StatusCategory.Canceled,
        _ => null,
    };
}

/// <summary>Status の選択肢の並びの決まり。</summary>
public static class StatusTemplates
{
    /// <summary>使える並びか（名前が空でなく重ならず、Backlog か Todo と、Done の選択肢が 1 つ以上ある）。</summary>
    public static bool IsUsable(IReadOnlyList<StatusTemplate> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return Problem(statuses) is null;
    }

    /// <summary>使えない理由。使えるなら null。</summary>
    public static string? Problem(IReadOnlyList<StatusTemplate> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Any(s => string.IsNullOrWhiteSpace(s.Name)))
        {
            return "名前が空のステータスがあります。";
        }

        if (statuses.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            return "同じ名前のステータスがあります。";
        }

        if (!statuses.Any(s => StatusCategories.IsNotStarted(s.Category)))
        {
            return "カテゴリが Backlog か Todo のステータスが 1 つ以上必要です。";
        }

        return statuses.Any(s => s.Category == StatusCategory.Done) ? null : "カテゴリが Done のステータスが 1 つ以上必要です。";
    }
}

/// <summary>
/// プロジェクトごとの上書き（要件 F-SET-02）。null の項目は既定に従う。
/// GitHub の Project の README に埋め込んで、メンバーで共有する（<see cref="ProjectReadme"/>）。
/// </summary>
public sealed record ProjectSettings
{
    public EffortUnit? EffortUnit { get; init; }

    public double? HoursPerDay { get; init; }

    public WorkCalendarRules? Calendar { get; init; }

    public TaskKind? NewTaskKind { get; init; }

    /// <summary>タスクの番号のキー（要件 F-TSK-14）。既定を持たない、プロジェクトだけの値。null ならまだ決めていない。</summary>
    public string? Key { get; init; }

    /// <summary>
    /// 計画のタスクを階層番号（例: TLB_1_2）で呼ぶときの、あらかじめ用意する段の数（1〜<see cref="ProjectKey.MaxOutlineLevels"/>）。
    /// null なら Issue の番号の通し番号（例: TLB-123）で呼ぶ。キーと同じく既定を持たない、プロジェクトだけの値。
    /// </summary>
    public int? OutlineLevels { get; init; }

    public static ProjectSettings None { get; } = new();

    public bool IsEmpty => EffortUnit is null && HoursPerDay is null && Calendar is null && NewTaskKind is null && Key is null && OutlineLevels is null;

    /// <summary>既定に重ねた値。</summary>
    public ResolvedSettings Over(DefaultSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return new ResolvedSettings(
            EffortUnit ?? defaults.EffortUnit,
            HoursPerDay is > 0 and <= 24 ? HoursPerDay.Value : defaults.HoursPerDay,
            Calendar ?? defaults.Calendar,
            NewTaskKind ?? defaults.NewTaskKind);
    }

    /// <summary>すべての項目を持つ上書き（新しいチームのプロジェクトに、作った人の既定を書き込むときに使う）。</summary>
    public static ProjectSettings From(ResolvedSettings resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return new ProjectSettings
        {
            EffortUnit = resolved.EffortUnit,
            HoursPerDay = resolved.HoursPerDay,
            Calendar = resolved.Calendar,
            NewTaskKind = resolved.NewTaskKind,
        };
    }

    public string ToJson() => ToNode(this).ToJsonString();

    public static ProjectSettings Parse(string? json) => TryParseNode(json) is { } node ? FromNode(node) : None;

    internal static JsonObject ToNode(ProjectSettings s)
    {
        var node = new JsonObject();
        if (s.EffortUnit is { } unit)
        {
            node["effortUnit"] = unit == Settings.EffortUnit.Days ? "days" : "hours";
        }

        if (s.HoursPerDay is { } hours)
        {
            node["hoursPerDay"] = hours;
        }

        if (s.Calendar is { } calendar)
        {
            node["calendar"] = calendar.ToString();
        }

        if (s.NewTaskKind is { } kind)
        {
            node["newTaskKind"] = kind == TaskKind.Issue ? "issue" : "task";
        }

        if (s.Key is { } key)
        {
            node["key"] = key;
        }

        if (s.OutlineLevels is { } levels)
        {
            node["outlineLevels"] = levels;
        }

        return node;
    }

    internal static JsonObject? TryParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ProjectSettings FromNode(JsonObject node)
    {
        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        double? hours = node["hoursPerDay"] is JsonValue h && h.TryGetValue<double>(out var d) && d is > 0 and <= 24 ? d : null;
        return new ProjectSettings
        {
            EffortUnit = Text(node["effortUnit"]) switch { "days" => Settings.EffortUnit.Days, "hours" => Settings.EffortUnit.Hours, _ => null },
            HoursPerDay = hours,
            Calendar = Text(node["calendar"]) is { Length: > 0 } calendar ? WorkCalendarRules.Parse(calendar) : null,
            NewTaskKind = Text(node["newTaskKind"]) switch { "issue" => TaskKind.Issue, "task" => TaskKind.Task, _ => null },
            Key = ProjectKey.Normalize(Text(node["key"])) is var key && ProjectKey.IsValid(key) ? key : null,
            OutlineLevels = node["outlineLevels"] is JsonValue o && o.TryGetValue<int>(out var levels) && levels is >= 1 and <= ProjectKey.MaxOutlineLevels
                ? levels
                : null,
        };
    }
}

/// <summary>
/// Project の README に、プロジェクトの設定を目に見えない注記として埋め込む（技術設計書 3.2.3 節）。
/// 利用者が書いた README の本文には手を触れない。
/// </summary>
public static class ProjectReadme
{
    private const string Start = "<!-- tasklabe:settings ";
    private const string End = " -->";

    public static ProjectSettings Read(string? readme)
    {
        if (readme is null || Find(readme) is not { } range)
        {
            return ProjectSettings.None;
        }

        return ProjectSettings.Parse(readme[(range.Start + Start.Length)..(range.End - End.Length)]);
    }

    /// <summary>
    /// README の設定の注記を置き換えた本文。設定が空なら注記を取り除く。
    /// ただし本文が空になるときは、中身が空の注記を残す（GitHub は空の README への更新を受け付けないため）。
    /// </summary>
    public static string Write(string? readme, ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var body = readme ?? "";
        if (Find(body) is { } range)
        {
            body = (body[..range.Start] + body[range.End..]).TrimEnd('\r', '\n', ' ');
        }

        if (settings.IsEmpty)
        {
            return body.Length > 0 ? body : Start + "{}" + End;
        }

        var note = Start + settings.ToJson() + End;
        return body.Length == 0 ? note : body.TrimEnd() + "\n\n" + note;
    }

    private static (int Start, int End)? Find(string readme)
    {
        int start = readme.IndexOf(Start, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int end = readme.IndexOf(End, start + Start.Length, StringComparison.Ordinal);
        return end < 0 ? null : (start, end + End.Length);
    }
}
