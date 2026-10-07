namespace Tasklabe.App.Services;

/// <summary>
/// 画面ごとの表示の状態を覚える（UX 規約 UX-23）。
/// ビュー・並び順・絞り込み・折りたたみ・表示の単位はこの PC の設定に保存し、アプリを開き直しても保つ。
/// スクロールの位置や選択は、アプリを開いているあいだだけ覚える。どちらもチームとは共有しない。
/// </summary>
public static class ViewState
{
    private static readonly Dictionary<string, string> Session = new(StringComparer.Ordinal);

    /// <summary>保存した値。なければ null。</summary>
    public static string? Get(string key) => App.Current.Services.CurrentSettings.ViewStates.GetValueOrDefault(key);

    /// <summary>値を保存する。null なら既定に戻す（保存から消す）。</summary>
    public static void Set(string key, string? value)
    {
        var states = App.Current.Services.CurrentSettings.ViewStates;
        if (value is null ? !states.Remove(key) : states.GetValueOrDefault(key) == value)
        {
            return;
        }

        if (value is not null)
        {
            states[key] = value;
        }

        App.Current.Services.SaveSettings();
    }

    /// <summary>キーの種類で始まる保存をすべて消す（「既定に戻す」）。</summary>
    public static void Reset(string screen)
    {
        var states = App.Current.Services.CurrentSettings.ViewStates;
        var keys = states.Keys.Where(k => k.EndsWith(":" + screen, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
        {
            states.Remove(key);
        }

        if (keys.Count > 0)
        {
            App.Current.Services.SaveSettings();
        }
    }

    /// <summary>アプリを開いているあいだだけ覚える値（選択など）。</summary>
    public static string? GetSession(string key) => Session.GetValueOrDefault(key);

    public static void SetSession(string key, string? value)
    {
        if (value is null)
        {
            Session.Remove(key);
        }
        else
        {
            Session[key] = value;
        }
    }

    /// <summary>集合を保存する（折りたたんだタスクの ID など）。</summary>
    public static HashSet<string> GetSet(string key) =>
        Get(key) is { Length: > 0 } value ? [.. value.Split('|', StringSplitOptions.RemoveEmptyEntries)] : [];

    public static void SetSet(string key, IEnumerable<string> values)
    {
        var joined = string.Join("|", values.Order(StringComparer.Ordinal));
        Set(key, joined.Length == 0 ? null : joined);
    }
}
