using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.GitHub;

namespace Tasklabe.Data.Tests;

/// <summary>元に戻す・やり直す操作が、キャッシュと送信キューの両方に反映されること（要件 F-UNDO-01）。</summary>
public sealed class UndoTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAlive;
    private readonly SqliteTaskStore _store;
    private readonly TaskEditService _edits;

    public UndoTests()
    {
        var cs = $"Data Source=file:undo-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var db = new TasklabeDatabase(cs);
        db.Migrate();
        _store = new SqliteTaskStore(db);

        // 送信は常にオフライン扱いにして、送信キューに残ったものを確かめる
        var sync = new SyncEngine(new GitHubApi(new HttpClient(new Offline()), GitHubHost.Default, () => "token"), _store, TimeProvider.System);
        _edits = new TaskEditService(_store, sync, TimeProvider.System, () => "me");
    }

    public void Dispose() => _keepAlive.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    private async Task<TaskItem> SeedAsync()
    {
        var project = new Project
        {
            Id = "P1",
            Number = 1,
            Title = "P1",
            Kind = ProjectKind.Personal,
            OwnerLogin = "me",
            RepositoryId = "R_1",
            RepositoryNameWithOwner = "me/repo",
            UpdatedAt = Now,
            StatusOptions = [new("todo", "Todo", "GRAY", StatusCategory.Todo), new("done", "Done", "GREEN", StatusCategory.Done)],
        };
        var task = new TaskItem
        {
            ItemId = "I1",
            ProjectId = "P1",
            IssueId = "ISSUE_1",
            RepositoryNameWithOwner = "me/repo",
            Number = 1,
            Title = "設計",
            StatusOptionId = "todo",
            ProgressPercent = 40,
            UpdatedAt = Now,
        };
        await _store.ReplaceProjectAsync(new ProjectSnapshot(project, [task]), Now, Ct);
        return (await _store.GetTaskAsync("I1", Ct))!;
    }

    [Fact]
    public async Task Undo_restores_the_cache_and_queues_the_reverse_change()
    {
        var task = await SeedAsync();
        await _edits.SetAsync(task, TaskField.Progress, TaskValues.Number(70));

        var outcome = await _edits.UndoAsync();

        Assert.Equal("「設計」の進捗率の変更", outcome!.Description);
        Assert.Equal(40, (await _store.GetTaskAsync("I1", Ct))!.ProgressPercent);
        Assert.True(_edits.History.CanRedo);
    }

    [Fact]
    public async Task Redo_applies_the_change_again()
    {
        var task = await SeedAsync();
        await _edits.SetAsync(task, TaskField.Title, "基本設計");
        await _edits.UndoAsync();

        await _edits.RedoAsync();

        Assert.Equal("基本設計", (await _store.GetTaskAsync("I1", Ct))!.Title);
        Assert.True(_edits.History.CanUndo);
        Assert.False(_edits.History.CanRedo);
    }

    [Fact]
    public async Task Undo_skips_a_step_whose_values_were_changed_since()
    {
        var task = await SeedAsync();
        await _edits.SetAsync(task, TaskField.Title, "基本設計");
        task = (await _store.GetTaskAsync("I1", Ct))!;
        await _edits.SetAsync(task, TaskField.Progress, TaskValues.Number(70));

        // 進捗率は、その後に GitHub の内容で 90 % に変わった
        await _store.ApplyEditAsync("I1", [new TaskChange(TaskField.Progress, "70", "90")], Ct);

        var outcome = await _edits.UndoAsync();

        Assert.Equal("「設計」のタイトルの変更", outcome!.Description);
        var now = (await _store.GetTaskAsync("I1", Ct))!;
        Assert.Equal("設計", now.Title);
        Assert.Equal(90, now.ProgressPercent);
    }

    [Fact]
    public async Task Nothing_to_undo_returns_null()
    {
        await SeedAsync();

        Assert.Null(await _edits.UndoAsync());
    }
}
