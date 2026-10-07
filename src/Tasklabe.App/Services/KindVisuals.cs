using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Core.Domain;

namespace Tasklabe.App.Services;

/// <summary>
/// 区分（課題・タスク）の呼び名・説明・アイコン（UI デザイン設計書 2.4.1 節）。
/// 画面ごとに言い回しやアイコンを変えず、すべてここから取る。
/// </summary>
public static class KindVisuals
{
    public static string Name(TaskKind kind) => kind switch
    {
        TaskKind.Issue => "課題",
        _ => "タスク",
    };

    /// <summary>区分の意味（ツールチップや選択肢の説明に使う）。</summary>
    public static string Description(TaskKind kind) => kind switch
    {
        TaskKind.Issue => "計画に入れる前の検討事項。日程は持たず、「計画に移す」でタスクになる",
        _ => "計画の中の作業。日程・工数・進捗率を持つ",
    };

    public static string Glyph(TaskKind kind) => kind switch
    {
        TaskKind.Issue => "",       // 旗
        _ => "",                    // チェックリスト
    };

    /// <summary>既存のアイコンを、その区分の見た目にする。</summary>
    public static void Apply(FontIcon icon, TaskKind kind)
    {
        icon.FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["SymbolThemeFontFamily"];
        icon.Glyph = Glyph(kind);
    }

    public static FontIcon Icon(TaskKind kind, double size = 12)
    {
        var icon = new FontIcon { FontSize = size };
        Apply(icon, kind);
        return icon;
    }
}

/// <summary>マイルストーン（プロジェクトの期限）のアイコン。どの画面でも ◆ で示す（UI デザイン設計書 2.4.1 節）。</summary>
public static class MilestoneVisuals
{
    /// <summary>◆ はアイコンのフォントにないため、記号のフォントで描く。</summary>
    public static void Apply(FontIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        icon.FontFamily = new FontFamily("Segoe UI Symbol");
        icon.Glyph = "◆";
    }

    public static FontIcon Icon(double size = 12)
    {
        var icon = new FontIcon { FontSize = size };
        Apply(icon);
        return icon;
    }
}
