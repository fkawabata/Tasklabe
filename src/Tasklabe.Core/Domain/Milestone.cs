namespace Tasklabe.Core.Domain;

/// <summary>
/// マイルストーン（要件 F-MS-01）。プロジェクトの期限を表す、名前と期日の目印で、タスクではない。
/// GitHub では、プロジェクトにつないだリポジトリの Milestone に対応する。
/// </summary>
/// <param name="Id">GitHub の Milestone の node ID。</param>
/// <param name="Number">リポジトリの中での番号（REST API で変更するときに使う）。</param>
public sealed record Milestone(string Id, int Number, string Title, DateOnly? Due, string? Description = null, bool IsClosed = false);
