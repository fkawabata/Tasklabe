using Tasklabe.Core.Domain;

namespace Tasklabe.App.Services;

/// <summary>ステータスのカテゴリの呼び名とアイコン（UI デザイン設計書 2.4 節）。</summary>
public static class StatusVisuals
{
    public static string Name(StatusCategory category) => category switch
    {
        StatusCategory.Backlog => "Backlog",
        StatusCategory.InProgress => "In Progress",
        StatusCategory.Done => "Done",
        StatusCategory.Pending => "Pending",
        StatusCategory.Canceled => "Canceled",
        _ => "Todo",
    };

    public static string Glyph(StatusCategory category) => category switch
    {
        StatusCategory.Backlog => "",
        StatusCategory.InProgress => "",
        StatusCategory.Done => "",
        StatusCategory.Pending => "",
        StatusCategory.Canceled => "",
        _ => "",
    };

    /// <summary>アイコンの色のリソース名。進めているものと終えたものを強調色、止めているものを控えめな色にする。</summary>
    public static string BrushKey(StatusCategory? category) => category switch
    {
        StatusCategory.InProgress or StatusCategory.Done => "Viz.Progress",
        StatusCategory.Backlog or StatusCategory.Canceled => "TextFillColorTertiaryBrush",
        _ => "TextFillColorSecondaryBrush",
    };
}
