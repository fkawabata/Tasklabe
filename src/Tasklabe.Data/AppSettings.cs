using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasklabe.Data;

/// <summary>端末ごとの設定。%LOCALAPPDATA%\Tasklabe\settings.json に保存する。</summary>
public sealed class AppSettings
{
    /// <summary>Default / Light / Dark。</summary>
    public string Theme { get; set; } = "Default";

    public string? UserId { get; set; }

    public string? UserLogin { get; set; }

    public string? UserName { get; set; }

    public string? PersonalRepositoryId { get; set; }

    public string? PersonalRepositoryName { get; set; }

    /// <summary>個人のプロジェクトの既定（DefaultSettings の保存形。要件 F-SET-01）。null なら以前の設定から作る。</summary>
    public string? PersonalDefaults { get; set; }

    /// <summary>チームのプロジェクトの既定（DefaultSettings の保存形。要件 F-SET-01）。null なら以前の設定から作る。</summary>
    public string? TeamDefaults { get; set; }

    /// <summary>以前の工数の表示の設定。既定を保存していないときに、既定の初期値として読むだけに使う。</summary>
    public bool EffortInDays { get; set; }

    /// <summary>以前の 1 人日の時間。既定を保存していないときに、既定の初期値として読むだけに使う。</summary>
    public double HoursPerDay { get; set; } = 8;

    /// <summary>既定から変えたキーの割り当て（操作の Id → "N, Ctrl+N"。空文字は割り当てなし。要件 F-KEY-04）。</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    /// <summary>ナビゲーションを展開したときの幅（要件 F-UI-NAV-01）。</summary>
    public double NavPaneWidth { get; set; } = 240;

    /// <summary>ナビゲーションのプロジェクトの並び（プロジェクトの ID。要件 F-UI-NAV-02）。この PC の利用者だけのもの。</summary>
    public List<string> ProjectOrder { get; set; } = [];

    /// <summary>カンバンで、カードのない列も出すか（要件 F-UI-KB-03）。めったに変えないため、ビューごとではなくアプリで 1 つ持つ。</summary>
    public bool KanbanShowEmptyColumns { get; set; } = true;

    /// <summary>ダイアログ・ピッカー・詳細パネルなどの下端に、操作のヒント（使えるキー）を書いておくか（要件 F-SET-07）。</summary>
    public bool ShowHints { get; set; } = true;

    /// <summary>計画の表で、タスクの番号をタイトルの前に出すか（要件 F-SET-06）。カンバンはいつも出す。</summary>
    public bool ShowNumberInPlan { get; set; } = true;

    /// <summary>ガントチャートの行で、タスクの番号をタイトルの前に出すか（要件 F-SET-06）。</summary>
    public bool ShowNumberInGantt { get; set; } = true;

    /// <summary>課題の一覧で、タスクの番号をタイトルの前に出すか（要件 F-SET-06）。</summary>
    public bool ShowNumberInIssues { get; set; } = true;

    /// <summary>カンバンの表示の設定（ビューのキー → KanbanDisplay の保存形。要件 F-UI-KB-03）。</summary>
    public Dictionary<string, string> KanbanDisplays { get; set; } = [];

    /// <summary>
    /// 画面ごとの表示の状態（UX 規約 UX-23）。キーは「種類:画面」（例: "view:mytasks"、"filter:{プロジェクトの ID}"）。
    /// この PC の利用者だけのもので、チームとは共有しない。
    /// </summary>
    public Dictionary<string, string> ViewStates { get; set; } = [];

    /// <summary>
    /// 以前の稼働日の決め方（"personal" またはチームのプロジェクトの ID → WorkCalendarRules の保存形）。
    /// "personal" は個人の既定の初期値に、プロジェクトの ID のものは、そのプロジェクトに設定がないあいだの代わりに読むだけに使う。
    /// </summary>
    public Dictionary<string, string> WorkCalendars { get; set; } = [];
}

public sealed class SettingsStore(string path)
{
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 壊れた設定は既定値で置き換える
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        }

        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
