using System;
using System.Data;
using System.Data.SQLite;
using System.IO;
using Dapper;

namespace MdQuiz.Server.Data
{
    /// <summary>
    /// 教师端 SQLite 数据库。读操作各自开连接；写操作串行化并在事务中执行。
    /// </summary>
    public sealed class Database
    {
        private const int SchemaVersion = 1;
        private readonly object _writeLock = new object();
        private readonly string _connectionString;

        static Database()
        {
            DefaultTypeMap.MatchNamesWithUnderscores = true;
        }

        public Database(string path)
        {
            Path = path;
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                Version = 3,
                ForeignKeys = true,
                Pooling = true,
                FailIfMissing = false,
            };
            _connectionString = builder.ToString();
        }

        public string Path { get; }

        public IDbConnection Open()
        {
            var conn = new SQLiteConnection(_connectionString);
            conn.Open();
            conn.Execute("PRAGMA busy_timeout = 5000;");
            return conn;
        }

        public T Read<T>(Func<IDbConnection, T> query)
        {
            using (var conn = Open()) return query(conn);
        }

        public T Write<T>(Func<IDbConnection, IDbTransaction, T> action)
        {
            lock (_writeLock)
            {
                using (var conn = Open())
                using (var tx = conn.BeginTransaction())
                {
                    var result = action(conn, tx);
                    tx.Commit();
                    return result;
                }
            }
        }

        public void Write(Action<IDbConnection, IDbTransaction> action)
        {
            Write<object>((c, t) =>
            {
                action(c, t);
                return null;
            });
        }

        /// <summary>创建数据库文件和表结构（已存在时只做升级）。</summary>
        public void Initialize()
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_writeLock)
            {
                using (var conn = Open())
                {
                    conn.Execute("PRAGMA journal_mode = WAL;");
                    var version = conn.ExecuteScalar<long>("PRAGMA user_version;");
                    if (version < 1)
                    {
                        using (var tx = conn.BeginTransaction())
                        {
                            conn.Execute(Schema.V1, transaction: tx);
                            conn.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: tx);
                            tx.Commit();
                        }
                    }
                }
            }
        }

        /// <summary>备份数据库到指定文件（在线备份，考试中也可执行）。</summary>
        public void BackupTo(string targetPath)
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            lock (_writeLock)
            {
                using (var source = (SQLiteConnection)Open())
                using (var target = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = targetPath, Version = 3 }.ToString()))
                {
                    target.Open();
                    source.BackupDatabase(target, "main", "main", -1, null, 0);
                }
            }
        }
    }

    internal static class Schema
    {
        public const string V1 = @"
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);

CREATE TABLE IF NOT EXISTS questions (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    type         INTEGER NOT NULL,
    stem         TEXT    NOT NULL,
    options_json TEXT    NOT NULL DEFAULT '[]',
    answer       TEXT    NOT NULL DEFAULT '',
    blanks_json  TEXT    NOT NULL DEFAULT '[]',
    analysis     TEXT    NOT NULL DEFAULT '',
    tags         TEXT    NOT NULL DEFAULT '',
    created_at   INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_questions_type ON questions(type);

CREATE TABLE IF NOT EXISTS resources (
    hash       TEXT PRIMARY KEY,
    mime       TEXT NOT NULL,
    data       BLOB NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS papers (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    items_json  TEXT NOT NULL DEFAULT '[]',
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS classes (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS students (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    class_id   INTEGER NOT NULL REFERENCES classes(id) ON DELETE CASCADE,
    student_no TEXT    NOT NULL UNIQUE,
    name       TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_students_class ON students(class_id);

CREATE TABLE IF NOT EXISTS exams (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    title          TEXT    NOT NULL,
    paper_id       INTEGER,
    paper_name     TEXT    NOT NULL DEFAULT '',
    questions_json TEXT    NOT NULL,
    question_count INTEGER NOT NULL DEFAULT 0,
    total_score    REAL    NOT NULL DEFAULT 0,
    class_ids_json TEXT    NOT NULL DEFAULT '[]',
    settings_json  TEXT    NOT NULL,
    status         INTEGER NOT NULL DEFAULT 0,
    extra_minutes  INTEGER NOT NULL DEFAULT 0,
    created_at     INTEGER NOT NULL,
    started_at     INTEGER,
    ended_at       INTEGER
);

CREATE TABLE IF NOT EXISTS participants (
    exam_id          INTEGER NOT NULL REFERENCES exams(id) ON DELETE CASCADE,
    student_no       TEXT    NOT NULL,
    name             TEXT    NOT NULL,
    class_name       TEXT    NOT NULL DEFAULT '',
    status           INTEGER NOT NULL DEFAULT 0,
    token            TEXT,
    machine          TEXT,
    ip               TEXT,
    login_at         INTEGER,
    submitted_at     INTEGER,
    forced           INTEGER NOT NULL DEFAULT 0,
    focus_lost       INTEGER NOT NULL DEFAULT 0,
    objective_score  REAL,
    subjective_score REAL,
    total_score      REAL,
    pending_manual   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (exam_id, student_no)
);

CREATE TABLE IF NOT EXISTS answers (
    exam_id     INTEGER NOT NULL REFERENCES exams(id) ON DELETE CASCADE,
    student_no  TEXT    NOT NULL,
    question_id INTEGER NOT NULL,
    value_json  TEXT    NOT NULL DEFAULT '[]',
    ticks       INTEGER NOT NULL DEFAULT 0,
    score       REAL,
    manual      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (exam_id, student_no, question_id)
);
CREATE INDEX IF NOT EXISTS ix_answers_question ON answers(exam_id, question_id);
";
    }

    internal static class Time
    {
        public static long ToUnixMs(DateTime utc) => (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        public static long? ToUnixMs(DateTime? utc) => utc.HasValue ? ToUnixMs(utc.Value) : (long?)null;

        public static DateTime FromUnixMs(long ms) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

        public static DateTime? FromUnixMs(long? ms) => ms.HasValue ? FromUnixMs(ms.Value) : (DateTime?)null;
    }
}
