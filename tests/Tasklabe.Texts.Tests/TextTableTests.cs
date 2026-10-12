using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tasklabe.Texts.Tests;

/// <summary>
/// 文言の表とソースが食い違っていないか（技術設計書 7.3 節）。ソースと文言の表をファイルとして読んで確かめる。
/// </summary>
public partial class TextTableTests
{
    private static readonly string Root = FindRoot();

    private static readonly string App = Path.Combine(Root, "src", "Tasklabe.App");

    public static TheoryData<string, string> Tables => new()
    {
        { Path.Combine("src", "Tasklabe.App", "Strings", "ja-JP", "Resources.resw"), Path.Combine("src", "Tasklabe.App", "Strings", "en-US", "Resources.resw") },
        { Path.Combine("src", "Tasklabe.Core", "Resources", "Strings.ja.resx"), Path.Combine("src", "Tasklabe.Core", "Resources", "Strings.resx") },
        { Path.Combine("src", "Tasklabe.Data", "Resources", "Strings.ja.resx"), Path.Combine("src", "Tasklabe.Data", "Resources", "Strings.resx") },
        { Path.Combine("src", "Tasklabe.GitHub", "Resources", "Strings.ja.resx"), Path.Combine("src", "Tasklabe.GitHub", "Resources", "Strings.resx") },
    };

    public static TheoryData<string> Libraries => ["Tasklabe.Core", "Tasklabe.Data", "Tasklabe.GitHub"];

    [Theory]
    [MemberData(nameof(Tables))]
    public void JapaneseAndEnglishHaveSameKeysAndPlaceholders(string japanese, string english)
    {
        var ja = Load(Path.Combine(Root, japanese));
        var en = Load(Path.Combine(Root, english));
        Assert.Empty(ja.Keys.Except(en.Keys).Order());
        Assert.Empty(en.Keys.Except(ja.Keys).Order());
        var mismatched = ja.Keys.Intersect(en.Keys)
            .Where(k => !Placeholders(ja[k]).SetEquals(Placeholders(en[k])))
            .Order()
            .ToList();
        Assert.Empty(mismatched);
        Assert.Empty(ja.Concat(en).Where(p => p.Value.Length == 0).Select(p => p.Key).Distinct().Order());
    }

    [Fact]
    public void AppKeysExist()
    {
        var table = Load(Path.Combine(App, "Strings", "ja-JP", "Resources.resw"));
        var uids = AppUids().ToHashSet();
        var missingUids = uids.Where(u => !table.Keys.Any(k => k.StartsWith(u + ".", StringComparison.Ordinal))).Order().ToList();
        Assert.Empty(missingUids);

        var (plain, counted) = AppCodeKeys();
        Assert.Empty(plain.Where(k => !table.ContainsKey(k)).Order());
        Assert.Empty(counted.SelectMany(k => new[] { k + "_One", k + "_Other" }).Where(k => !table.ContainsKey(k)).Order());
    }

    [Fact]
    public void AppKeysAreUsed()
    {
        var table = Load(Path.Combine(App, "Strings", "ja-JP", "Resources.resw"));
        var uids = AppUids().ToHashSet();
        var (plain, counted) = AppCodeKeys();
        var unused = table.Keys.Where(k =>
        {
            int dot = k.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0)
            {
                return !uids.Contains(k[..dot]);
            }

            return !plain.Contains(k) && !(PluralBase(k) is { } b && counted.Contains(b));
        });
        Assert.Empty(unused.Order());
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void LibraryKeysMatchStringsClass(string project)
    {
        var dir = Path.Combine(Root, "src", project, "Resources");
        var table = Load(Path.Combine(dir, "Strings.ja.resx"));
        var source = File.ReadAllText(Path.Combine(dir, "Strings.cs"));

        // プロパティの名前をキーにするもの（Get() と nameof）と、文字列で書いたキー（単数・複数、ID から作る接頭辞）
        var members = MemberPattern().Matches(source)
            .Where(m => m.Groups[2].Value.Contains("Get()", StringComparison.Ordinal) || m.Groups[2].Value.Contains($"nameof({m.Groups[1].Value})", StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var literals = LiteralPattern().Matches(source).Select(m => m.Groups[1].Value).ToHashSet();
        var prefixes = literals.Where(l => l.EndsWith('_')).ToList();

        Assert.Empty(members.Where(m => !table.ContainsKey(m)).Order());
        Assert.Empty(literals.Where(l => !l.EndsWith('_') && KeyPattern().IsMatch(l) && !table.ContainsKey(l)).Order());
        Assert.Empty(table.Keys.Where(k => !members.Contains(k) && !literals.Contains(k) && !prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).Order());
    }

    private static string? PluralBase(string key) =>
        key.EndsWith("_One", StringComparison.Ordinal) ? key[..^4]
        : key.EndsWith("_Other", StringComparison.Ordinal) ? key[..^6]
        : null;

    private static IEnumerable<string> AppUids() =>
        SourceFiles("*.xaml").SelectMany(f => UidPattern().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value));

    private static (HashSet<string> Plain, HashSet<string> Counted) AppCodeKeys()
    {
        var plain = new HashSet<string>(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles("*.cs").Concat(SourceFiles("*.xaml")))
        {
            foreach (Match m in CodeKeyPattern().Matches(File.ReadAllText(file)))
            {
                (m.Groups[1].Value == "Count" ? counted : plain).Add(m.Groups[2].Value);
            }
        }

        return (plain, counted);
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(App, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);

    private static HashSet<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value)];

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tasklabe.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Tasklabe.slnx が見つかりません。");
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"x:Uid=""([^""]+)""")]
    private static partial Regex UidPattern();

    /// <summary>Texts.Get("キー")、Texts.Format("キー", …)、Texts.Count("キー", …)。XAML の x:Bind から呼ぶものも含む。</summary>
    [GeneratedRegex(@"Texts\.(Get|Format|Count)\(\s*[""']([A-Za-z0-9_]+)[""']")]
    private static partial Regex CodeKeyPattern();

    [GeneratedRegex(@"public static string (\w+)(?:\([^)]*\))?\s*(=>[^;]*;)")]
    private static partial Regex MemberPattern();

    [GeneratedRegex(@"""([A-Za-z0-9_]+)""")]
    private static partial Regex LiteralPattern();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9_]*$")]
    private static partial Regex KeyPattern();
}
