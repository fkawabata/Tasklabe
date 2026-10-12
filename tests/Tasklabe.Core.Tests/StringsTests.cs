using System.Globalization;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Keyboard;

namespace Tasklabe.Core.Tests;

/// <summary>ID から引く文言が、日本語と英語の両方にあること（技術設計書 7.3 節）。</summary>
public class StringsTests
{
    public static TheoryData<string> Languages => ["ja-JP", "en-US"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryShortcutHasNameAndCategory(string language)
    {
        using var _ = Use(language);
        Assert.All(ShortcutCatalog.All, d =>
        {
            Assert.DoesNotContain("Action_", d.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("ShortcutCategory_", ShortcutCatalog.CategoryName(d.Category), StringComparison.Ordinal);
        });
        Assert.All(ShortcutCatalog.Fixed, f =>
        {
            Assert.DoesNotContain("Fixed_", f.Keys, StringComparison.Ordinal);
            Assert.DoesNotContain("Fixed_", f.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("ShortcutCategory_", ShortcutCatalog.CategoryName(f.Category), StringComparison.Ordinal);
        });
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryFieldHasName(string language)
    {
        using var _ = Use(language);
        Assert.All(Enum.GetValues<TaskField>(), f => Assert.DoesNotContain("Field_", FieldLabels.Of(f), StringComparison.Ordinal));
    }

    [Fact]
    public void HolidaysFollowLanguage()
    {
        using (Use("en-US"))
        {
            Assert.Equal("Constitution Memorial Day", JapaneseHolidays.NameOf(new DateOnly(2026, 5, 3)));
        }

        Assert.Equal("憲法記念日", JapaneseHolidays.NameOf(new DateOnly(2026, 5, 3)));
    }

    [Fact]
    public void ShortcutNamesFollowLanguage()
    {
        using (Use("en-US"))
        {
            Assert.Equal("Change assignee", ShortcutCatalog.Find("task.assign")!.Label);
        }

        Assert.Equal("担当者の変更", ShortcutCatalog.Find("task.assign")!.Label);
    }

    private static Restore Use(string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        return new Restore(previous);
    }

    private sealed class Restore(CultureInfo previous) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
}
