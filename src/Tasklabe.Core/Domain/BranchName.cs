using System.Text;

namespace Tasklabe.Core.Domain;

/// <summary>
/// タスクから作るブランチの名前（要件 F-TSK-19）。番号で始め（例: TAS-33-login-form）、
/// コミットや PR に番号が残るようにする。
/// </summary>
public static class BranchName
{
    /// <summary>タイトルから添える部分の長さの上限（文字）。</summary>
    private const int MaxSlugLength = 40;

    /// <summary>
    /// 番号とタイトルからブランチ名を提案する。タイトルの英数字を小文字にしてハイフンでつなぎ、番号の後に添える。
    /// タイトルに英数字がなければ番号だけとする。番号がなければ（GitHub に送る前）タイトルだけとする。
    /// </summary>
    public static string Suggest(string? taskKey, string title)
    {
        var slug = Slug(title);
        return (taskKey, slug) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{taskKey}-{slug}",
            ({ Length: > 0 }, _) => taskKey,
            (_, { Length: > 0 }) => slug,
            _ => "task",
        };
    }

    /// <summary>ブランチ名にできない理由（Git の参照名の規則）。使えるなら null。</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "ブランチ名を入力してください。";
        }

        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            return "空白と ~ ^ : ? * [ \\ はブランチ名に使えません。";
        }

        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')
            || name.EndsWith(".lock", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)
            || name.Contains("//", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name == "@"
            || name.Split('/').Any(part => part.StartsWith('.')))
        {
            return "ブランチ名の形が正しくありません（- や / で始めない、.. や // を含めない、. や / や .lock で終えない）。";
        }

        return null;
    }

    private static string Slug(string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        return text.Length <= MaxSlugLength ? text : text[..MaxSlugLength].TrimEnd('-');
    }
}
