using Microsoft.Data.Sqlite;

namespace Tasklabe.Data.Tests;

public sealed class TasklabeDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tasklabe-test-{Guid.NewGuid():N}");

    public TasklabeDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static List<string> Tables(TasklabeDatabase db)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read())
        {
            names.Add(r.GetString(0));
        }

        return names;
    }

    [Fact]
    public void New_file_gets_schema()
    {
        var db = TasklabeDatabase.OpenFile(Path.Combine(_dir, "cache.db"));

        Assert.Contains("tasks", Tables(db));
    }

    [Fact]
    public void Reopening_own_database_keeps_it()
    {
        var path = Path.Combine(_dir, "cache.db");
        TasklabeDatabase.OpenFile(path);
        SqliteConnection.ClearAllPools();

        TasklabeDatabase.OpenFile(path);

        Assert.Empty(Directory.GetFiles(_dir, "*.foreign-*"));
    }

    [Fact]
    public void Foreign_database_is_moved_aside_not_deleted()
    {
        var path = Path.Combine(_dir, "cache.db");
        using (var foreign = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            foreign.Open();
            using var cmd = foreign.CreateCommand();
            cmd.CommandText = "CREATE TABLE project (id INTEGER); PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }

        var db = TasklabeDatabase.OpenFile(path);

        Assert.Contains("tasks", Tables(db));
        Assert.DoesNotContain("project", Tables(db));
        Assert.Single(Directory.GetFiles(_dir, "cache.db.foreign-*"), f => !f.EndsWith("-wal") && !f.EndsWith("-shm"));
    }
}
