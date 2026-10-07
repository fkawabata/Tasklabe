using Tasklabe.Core.Domain;

namespace Tasklabe.App.Services;

/// <summary>プロジェクトの画面上の呼び方。既定の個人プロジェクト（Inbox）は「未分類」として見せる（画面の名前「マイタスク」と重ねない。UX 規約 UX-29）。</summary>
public static class ProjectDisplay
{
    public const string DefaultPersonalName = "未分類";

    public static string Name(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Kind == ProjectKind.Inbox ? DefaultPersonalName : project.Title;
    }
}
