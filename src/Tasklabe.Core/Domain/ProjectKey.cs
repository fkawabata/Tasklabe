using System.Text;

namespace Tasklabe.Core.Domain;

/// <summary>
/// タスクの番号のキー（要件 F-TSK-14〜15）。番号は「キー-Issue 番号」（例: TLB-123）の通し番号か、
/// 計画の中の位置を表す「キー_1_2」の階層番号とし、区分は含めない。
/// </summary>
public static class ProjectKey
{
    public const int MinLength = 2;
    public const int MaxLength = 10;

    /// <summary>キーの候補が作れないときのキー。</summary>
    public const string Fallback = "TASK";

    /// <summary>既定の個人プロジェクト（画面では「未分類」）のキー。ほかのプロジェクトには使わせない。</summary>
    public const string InboxKey = "MY";

    /// <summary>階層番号の区切り。通し番号の「-」と分け、自動リンク（キー-）に拾われないようにする。</summary>
    public const char OutlineSeparator = '_';

    /// <summary>階層番号で、あらかじめ用意できる段の数の上限。</summary>
    public const int MaxOutlineLevels = 5;

    /// <summary>番号の表記（例: TLB-123）。</summary>
    public static string Format(string key, int number) => $"{key}-{number}";

    /// <summary>
    /// 階層番号の表記（例: TLB_1_2_0）。<paramref name="path"/> は最上位から自分までの、兄弟の中での順番（1 から）。
    /// 段が <paramref name="levels"/> に満たなければ後ろを 0 で埋め、超えればそのまま段を足す。
    /// </summary>
    public static string FormatOutline(string key, IReadOnlyList<int> path, int levels)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sb = new StringBuilder(key);
        for (int i = 0; i < Math.Max(path.Count, levels); i++)
        {
            sb.Append(OutlineSeparator).Append(i < path.Count ? path[i] : 0);
        }

        return sb.ToString();
    }

    /// <summary>入力されたキーを、保存する形（前後の空白を除いた大文字）にする。</summary>
    public static string Normalize(string? text) => (text ?? "").Trim().ToUpperInvariant();

    /// <summary>キーとして使えるか（英大文字で始まり、英大文字と数字だけの 2〜10 文字）。</summary>
    public static bool IsValid(string? key) =>
        key is { Length: >= MinLength and <= MaxLength } && IsLetter(key[0]) && key.All(c => IsLetter(c) || IsDigit(c));

    /// <summary>キーにできない理由。使えるなら null。</summary>
    /// <param name="taken">ほかのプロジェクトが使っているキー。</param>
    /// <param name="linked">リポジトリの自動リンクに、別の行き先で登録してあるキー。</param>
    public static string? Problem(string key, IEnumerable<string> taken, IEnumerable<string>? linked = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!IsValid(key))
        {
            return $"キーは英字で始まる、英字と数字だけの {MinLength}〜{MaxLength} 文字にしてください。";
        }

        if (key == InboxKey)
        {
            return $"{InboxKey} は未分類のタスクの番号に使うため、ほかのプロジェクトには使えません。";
        }

        if (taken.Contains(key, StringComparer.Ordinal))
        {
            return $"{key} はほかのプロジェクトで使っています。";
        }

        return linked?.Contains(key, StringComparer.Ordinal) == true ? $"{key}- はリポジトリの自動リンクで別の行き先に使われています。" : null;
    }

    /// <summary>
    /// プロジェクトの名前からキーを提案する。名前が複数の語ならその頭文字（例: Web Renewal → WR）、
    /// 1 語なら先頭の 3 文字（例: Tasklabe → TAS）とする。名前から作れなければリポジトリ名から作る。
    /// ほかと重なるときは、名前から長めに取ったキー（TAS → TASK、WR → WER）を試し、それでも重なるときに数字を添える。
    /// <see cref="InboxKey"/> は提案しない。
    /// </summary>
    public static string Suggest(string title, string? repositoryNameWithOwner, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var used = taken.ToHashSet(StringComparer.Ordinal);
        used.Add(InboxKey);
        var repository = repositoryNameWithOwner?[(repositoryNameWithOwner.IndexOf('/') + 1)..];
        var candidates = Candidates(title).ToList();
        if (candidates.Count == 0)
        {
            candidates = Candidates(repository).ToList();
        }

        if (candidates.FirstOrDefault(c => !used.Contains(c)) is { } free)
        {
            return free;
        }

        var key = candidates.FirstOrDefault() ?? Fallback;
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
    /// プロジェクトごとのキー（プロジェクトの ID → キー）。未分類のプロジェクトはいつも <see cref="InboxKey"/> とする。
    /// 保存したキーはそのまま使い、まだ保存していないプロジェクトには、保存したキーと重ならないよう提案したキーを充てる。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(IEnumerable<Project> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var list = projects.ToList();
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in list)
        {
            if (p.Kind == ProjectKind.Inbox)
            {
                keys[p.Id] = InboxKey;
            }
            else if (SavedKey(p) is { } saved)
            {
                keys[p.Id] = saved;
            }
        }

        // 提案の順を ID で決め、同じプロジェクトの集まりならいつも同じキーになるようにする
        foreach (var p in list.Where(p => !keys.ContainsKey(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            keys[p.Id] = Suggest(p.Title, p.RepositoryNameWithOwner, keys.Values);
        }

        return keys;
    }

    /// <summary>
    /// プロジェクトに保存したキー。未分類のプロジェクトと、まだ保存していないプロジェクトでは null。
    /// ほかのプロジェクトに保存した <see cref="InboxKey"/> は、保存していないものとして扱う。
    /// </summary>
    public static string? SavedKey(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Kind != ProjectKind.Inbox && project.Settings.Key is { } key && key != InboxKey ? key : null;
    }

    /// <summary>名前の英数字から作るキーの候補（よい順）。作れなければ空。</summary>
    private static IEnumerable<string> Candidates(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        // 英字で始まる語だけを使う（数字だけの語や、英字でない文字は区切りとみなす）
        var words = Words(name).Select(w => w.ToUpperInvariant()).Where(w => IsLetter(w[0])).ToList();
        if (words.Count == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (words.Count >= MinLength)
        {
            // 頭文字（WR）、続けて先頭の語を長めに取ったもの（WER、WEBR）
            var rest = new string([.. words.Skip(1).Take(3).Select(w => w[0])]);
            for (int n = 1; n <= words[0].Length && n + rest.Length <= MaxLength; n++)
            {
                if (words[0][..n] + rest is var key && IsValid(key) && seen.Add(key))
                {
                    yield return key;
                }
            }
        }
        else
        {
            // 先頭の 3 文字（TAS）、続けて 1 文字ずつ長くしたもの（TASK、TASKL）
            for (int n = Math.Min(3, words[0].Length); n <= Math.Min(words[0].Length, MaxLength); n++)
            {
                if (words[0][..n] is var key && IsValid(key) && seen.Add(key))
                {
                    yield return key;
                }
            }
        }
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
