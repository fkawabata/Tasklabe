namespace Tasklabe.App.Services;

/// <summary>
/// 工数の表示（要件 F-PRG-01）。入力は常に時間（h）で行い、表示だけを、表示中のプロジェクトの設定に従って時間と人日で切り替える。
/// </summary>
public static class EffortText
{
    /// <summary>工数を「12.5h」または「1.6 人日」の形で表す。</summary>
    public static string Of(double hours) => ProjectPreferences.Current.FormatEffort(hours);

    /// <summary>未設定のときに代わりの文字列を返す。</summary>
    public static string Of(double? hours, string fallback = "") => hours is { } h ? Of(h) : fallback;
}
