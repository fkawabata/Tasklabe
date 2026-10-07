using Tasklabe.App.Controls;

namespace Tasklabe.App.Views;

/// <summary>
/// キーボードで操作する中身を持つ画面。フォーカスが一覧の外へ外れたときに、表示中の一覧へ戻すために使う。
/// </summary>
public interface IKeyboardContent
{
    /// <summary>表示中のビューへフォーカスを移す。選んでいるものがなければ先頭を選ぶ。</summary>
    void FocusContent();
}

/// <summary>
/// 詳細パネルを開いたまま、↑↓ で前後のタスクへ移れる画面（UX 規約 UX-10）。
/// </summary>
internal interface ITaskSequence
{
    /// <summary>
    /// 表示中のビューで、指定したタスクの前（-1）か後（1）のタスクを選び、そのタスクを返す。
    /// 端にいるとき、ビューにそのタスクがないときは何も選ばずに null を返す。
    /// </summary>
    Tasklabe.Core.Domain.TaskItem? Step(string itemId, int delta);
}

/// <summary>タスクを作り始めたときの文脈（UX 規約 UX-12）を持つ画面。</summary>
public interface INewTaskContextSource
{
    NewTaskContext NewTaskContext();
}

/// <summary>絞り込みを持つ画面（UX 規約 UX-24）。F キーで絞り込みのピッカーを開く。</summary>
public interface IFilterHost
{
    Task OpenFilterAsync();
}

/// <summary>並び順とグループを選べる画面（Y・G キー。UI デザイン設計書 4.4 節）。</summary>
public interface IViewOptionsHost
{
    Task OpenOrderingAsync();

    Task OpenGroupingAsync();
}

/// <summary>コマンドパレットに出す、画面に固有の操作（UX 規約 UX-18）。</summary>
public interface ICommandSource
{
    IEnumerable<PaletteCommand> Commands();
}

/// <summary>コマンドパレットの 1 つの操作。</summary>
/// <param name="Keys">割り当てたキー（右端に示す）。</param>
public sealed record PaletteCommand(string Label, string? Keys, Func<Task> Run, string Group = "操作");
