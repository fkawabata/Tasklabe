using Microsoft.Data.Sqlite;

namespace Tasklabe.Data;

/// <summary>ローカルキャッシュの SQLite データベース（技術設計書 4 章）。</summary>
public sealed class TasklabeDatabase
{
    /// <summary>このアプリのデータベースであることを示す識別子（"TSKL"）。</summary>
    private const int ApplicationId = 0x54534B4C;

    /// <summary>スキーマの移行手順。添字 + 1 がそのバージョン。</summary>
    private static readonly string[] Migrations =
    [
        // 1: プロジェクトとタスクのキャッシュ
        """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            number INTEGER NOT NULL,
            title TEXT NOT NULL,
            kind INTEGER NOT NULL,
            owner_login TEXT NOT NULL,
            url TEXT,
            repository_id TEXT,
            repository_name TEXT,
            closed INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE status_options (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            id TEXT NOT NULL,
            name TEXT NOT NULL,
            color TEXT NOT NULL,
            category INTEGER NOT NULL,
            sort_order INTEGER NOT NULL,
            PRIMARY KEY (project_id, id)
        );

        CREATE TABLE tasks (
            item_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            issue_id TEXT NOT NULL,
            repository_name TEXT NOT NULL,
            number INTEGER NOT NULL,
            title TEXT NOT NULL,
            url TEXT,
            is_closed INTEGER NOT NULL,
            status_option_id TEXT,
            status_name TEXT,
            category INTEGER NOT NULL,
            kind INTEGER NOT NULL,
            start TEXT,
            target TEXT,
            actual_start TEXT,
            actual_end TEXT,
            estimate_hours REAL,
            progress REAL NOT NULL,
            parent_issue_id TEXT,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX ix_tasks_project ON tasks(project_id);

        CREATE TABLE task_assignees (
            item_id TEXT NOT NULL REFERENCES tasks(item_id) ON DELETE CASCADE,
            login TEXT NOT NULL,
            PRIMARY KEY (item_id, login)
        );

        CREATE TABLE sync_state (
            project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
            last_synced_at TEXT NOT NULL,
            remote_updated_at TEXT NOT NULL
        );
        """,

        // 2: 編集（本文、フィールド ID、送信キュー、競合）
        """
        ALTER TABLE tasks ADD COLUMN body TEXT;

        CREATE TABLE project_fields (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            name TEXT NOT NULL,
            id TEXT NOT NULL,
            PRIMARY KEY (project_id, kind, name)
        );

        CREATE TABLE outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind INTEGER NOT NULL,
            project_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            issue_id TEXT,
            field INTEGER,
            old_value TEXT,
            new_value TEXT,
            payload TEXT,
            created_at TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            last_error TEXT,
            failed INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_outbox_item ON outbox(item_id);

        CREATE TABLE conflicts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id TEXT NOT NULL,
            task_title TEXT NOT NULL,
            field INTEGER NOT NULL,
            local_value TEXT,
            remote_value TEXT,
            detected_at TEXT NOT NULL,
            acknowledged INTEGER NOT NULL DEFAULT 0
        );

        -- フィールド ID と本文を取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 3: チーム（Organization のアクセス状態、担当者の候補）
        """
        CREATE TABLE organizations (
            login TEXT PRIMARY KEY,
            id TEXT NOT NULL,
            name TEXT,
            can_create_projects INTEGER NOT NULL,
            can_create_repositories INTEGER NOT NULL,
            problem INTEGER NOT NULL DEFAULT 0,
            problem_detail TEXT
        );

        CREATE TABLE assignable_users (
            repository_name TEXT NOT NULL,
            login TEXT NOT NULL,
            id TEXT NOT NULL,
            name TEXT,
            PRIMARY KEY (repository_name, login)
        );
        """,

        // 4: WBS（兄弟の間の並び順）
        """
        ALTER TABLE tasks ADD COLUMN sort_order REAL NOT NULL DEFAULT 0;

        -- 並び順を取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 5: 依存関係（先行タスク）
        """
        ALTER TABLE tasks ADD COLUMN blocked_by TEXT;

        -- 依存関係を取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 6: 計画と課題の区分
        """
        -- 区分を取り込み直すため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 7: 後続を待たせないタスク（日程の扱い）
        """
        ALTER TABLE tasks ADD COLUMN non_blocking INTEGER NOT NULL DEFAULT 0;

        -- 日程の扱いを取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 8: プロジェクトの設定（README の注記）
        """
        ALTER TABLE projects ADD COLUMN settings TEXT;

        -- 設定を取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 9: 競合は発生したときに知らせるだけで読み返さないため、記録の表を除く
        """
        DROP TABLE IF EXISTS conflicts;
        """,

        // 10: 作業するリポジトリ
        """
        ALTER TABLE tasks ADD COLUMN repositories TEXT;

        -- 作業するリポジトリを取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 11: 選んだ既存のブランチ
        """
        ALTER TABLE tasks ADD COLUMN branches TEXT;

        -- 選んだブランチを取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,

        // 12: マイルストーン（リポジトリの Milestone）と、タスクの所属
        """
        CREATE TABLE milestones (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            id TEXT NOT NULL,
            number INTEGER NOT NULL,
            title TEXT NOT NULL,
            due TEXT,
            description TEXT,
            closed INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (project_id, id)
        );

        ALTER TABLE tasks ADD COLUMN milestone_id TEXT;

        -- マイルストーンを取り込むため、次回の同期ですべての Project を取り直す
        DELETE FROM sync_state;
        """,
    ];

    private readonly string _connectionString;

    public TasklabeDatabase(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// ファイルのデータベースを開く。他のアプリのデータベースが同じ場所にあった場合は、
    /// 削除せずに別名へ退避してから新しく作成する（キャッシュは GitHub から再取得できる）。
    /// </summary>
    public static TasklabeDatabase OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new TasklabeDatabase(new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString());

        if (File.Exists(path) && !db.IsOwnedOrEmpty())
        {
            SqliteConnection.ClearAllPools();
            var backup = $"{path}.foreign-{DateTime.Now:yyyyMMddHHmmss}";
            foreach (var suffix in (string[])["", "-wal", "-shm"])
            {
                if (File.Exists(path + suffix))
                {
                    File.Move(path + suffix, backup + suffix);
                }
            }
        }

        db.Migrate();
        return db;
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        using var connection = Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(check.ExecuteScalar());

        for (int v = version; v < Migrations.Length; v++)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v];
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {v + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>このアプリのデータベースか、まだ何もない（新規の）データベースか。</summary>
    private bool IsOwnedOrEmpty()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA application_id; PRAGMA user_version;";
        using var r = cmd.ExecuteReader();
        r.Read();
        int applicationId = r.GetInt32(0);
        r.NextResult();
        r.Read();
        int userVersion = r.GetInt32(0);
        return applicationId == ApplicationId || (applicationId == 0 && userVersion == 0);
    }
}
