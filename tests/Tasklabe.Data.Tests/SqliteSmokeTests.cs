namespace Tasklabe.Data.Tests;

public class SqliteSmokeTests
{
    [Fact]
    public void Sqlite_in_memory_database_opens()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        Assert.False(string.IsNullOrEmpty(command.ExecuteScalar() as string));
    }
}
