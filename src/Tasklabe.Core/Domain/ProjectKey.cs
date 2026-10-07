using System.Text;

namespace Tasklabe.Core.Domain;

/// <summary>
/// タスクの番号のキー（要件 F-TSK-14）。番号は「キー-Issue 番号」（例: TLB-123）とし、区分は含めない。
/// 課題を計画へ移しても、計画から課題へ戻しても、同じ Issue のまま同じ番号で呼べる。
/// </summary>
public static class ProjectKey
{
    public const int MinLength = 2;
    public const int MaxLength = 10;

    /// <summary>キーの候補が作れないときのキー。</summary>
    public const string Fallback = "TASK";

    /// <summary>既定の個人プロジェクト（画面では「未分類」）のキー。</summary>
    public const string InboxKey = "MY";

    /// <summary>番号の表記（例: TLB-123）。</summary>
    public static string Format(string key, int number) => $"{key}-{number}";

    /// <summary>入力されたキーを、保存する形（前後の空白を除いた大文字）にする。</summary>
    public static string Normalize(string? text) => (text ?? "").Trim().ToUpperInvariant();

    /// <summary>キーとして使えるか（英大文字で始まり、英大文字と数字だけの 2〜10 文字）。</summary>
    public static bool IsValid(string? key) =>
        key is { Length: >= MinLength and <= MaxLength } && IsLetter(key[0]) && key.All(c => IsLetter(c) || IsDigit(c));

    /// <summary>キーにできない理由。使えるなら null。</summary>
    /// <param name="taken">ほかのプロジェクトが使っているキー。</param>
    public static string? Problem(string key, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!IsValid(key))
        {
            return $"キーは英字で始まる、英字と数字だけの {MinLength}〜{MaxLength} 文字にしてください。";
        }

        return taken.Contains(key, StringComparer.Ordinal) ? $"{key} はほかのプロジェクトで使っています。" : null;
    }

    /// <summary>
    /// プロジェクトの名前からキーを提案する。名前が複数の語ならその頭文字（例: Web Renewal → WR）、
    /// 1 語なら先頭の 3 文字（例: Tasklabe → TAS）とする。名前から作れなければリポジトリ名から作り、
    /// ほかのプロジェクトと重なるときは数字を添える。
    /// </summary>
    public static string Suggest(string title, string? repositoryNameWithOwner, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var repository = repositoryNameWithOwner?[(repositoryNameWithOwner.IndexOf('/') + 1)..];
        var key = FromName(title) ?? FromName(repository) ?? Fallback;
        if (!used.Contains(key))
        {
            return key;
        }

        for (int n = 2; ; n++)
        {
            var suffix = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var numbered = key[..Math.Min(key.Length, MaxLength - suffix.Length)] + suffix;
            if (!used.Contains(numbered))
            {
                return numbered;
            }
        }
    }

    /// <summary>
    /// プロジェクトごとのキー（プロジェクトの ID → キー）。保存したキーはそのまま使い、
    /// まだ保存していないプロジェクトには、保存したキーと重ならないよう提案したキーを充てる。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(IEnumerable<Project> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var list = projects.ToList();
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in list.Where(p => p.Settings.Key is not null))
        {
            keys[p.Id] = p.Settings.Key!;
        }

        // 提案の順を ID で決め、同じプロジェクトの集まりならいつも同じキーになるようにする
        foreach (var p in list.Where(p => p.Settings.Key is null).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            keys[p.Id] = p.Kind == ProjectKind.Inbox && !keys.ContainsValue(InboxKey)
                ? InboxKey
                : Suggest(p.Title, p.RepositoryNameWithOwner, keys.Values);
        }

        return keys;
    }

    /// <summary>名前の英数字からキーを作る。作れなければ null。</summary>
    private static string? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // 英字で始まる語だけを使う（数字だけの語や、英字でない文字は区切りとみなす）
        var words = Words(name).Where(w => IsLetter(char.ToUpperInvariant(w[0]))).ToList();
        if (words.Count == 0)
        {
            return null;
        }

        string key = words.Count >= MinLength
            ? new string([.. words.Take(4).Select(w => char.ToUpperInvariant(w[0]))])
            : words[0].ToUpperInvariant()[..Math.Min(3, words[0].Length)];
        return IsValid(key) ? key : null;
    }

    /// <summary>英数字の続く語に分ける。小文字から大文字に変わるところ（camelCase）も区切る。</summary>
    private static IEnumerable<string> Words(string name)
    {
        var word = new StringBuilder();
        char previous = '\0';
        foreach (var c in name)
        {
            bool ascii = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (!ascii || (char.IsUpper(c) && char.IsLower(previous)))
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }
            }

            if (ascii)
            {
                word.Append(c);
            }

            previous = c;
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static bool IsLetter(char c) => c is >= 'A' and <= 'Z';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
