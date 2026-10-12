using System.Globalization;
using Microsoft.Data.Sqlite;
using PaCodeCopy.Core.Models;

namespace PaCodeCopy.Core.Storage;

/// <summary>SQLite implementation of <see cref="ISnippetRepository"/>. All SQL is parameterised.</summary>
public sealed class SqliteSnippetRepository : ISnippetRepository
{
    private const string Columns =
        "Id, Name, Category, Tags, FilePath, Hash, ActionCount, Source, Content, CreatedAt, LastCopiedAt";

    private readonly string _connectionString;

    public SqliteSnippetRepository(string databasePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // No pooling: the file handle is released after each call, so the DB can be backed up or deleted.
            Pooling = false,
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Snippets (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Name         TEXT    NOT NULL,
                Category     TEXT    NOT NULL,
                Tags         TEXT    NOT NULL DEFAULT '',
                FilePath     TEXT    NOT NULL,
                Hash         TEXT    NOT NULL UNIQUE,
                ActionCount  INTEGER NOT NULL DEFAULT 0,
                Source       TEXT    NOT NULL,
                Content      TEXT    NOT NULL,
                CreatedAt    TEXT    NOT NULL,
                LastCopiedAt TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Snippets_Category     ON Snippets (Category);
            CREATE INDEX IF NOT EXISTS IX_Snippets_LastCopiedAt ON Snippets (LastCopiedAt);
            """;
        command.ExecuteNonQuery();
    }

    public long Insert(Snippet snippet)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        InsertCore(command, snippet);
        return snippet.Id;
    }

    public Snippet? GetById(long id) => QuerySingle("Id = $value", id);

    public Snippet? GetByHash(string hash) => QuerySingle("Hash = $value", hash);

    public void Update(Snippet snippet)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Snippets
            SET Name = $name, Category = $category, Tags = $tags, FilePath = $filePath
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$name", snippet.Name);
        command.Parameters.AddWithValue("$category", snippet.Category);
        command.Parameters.AddWithValue("$tags", snippet.Tags);
        command.Parameters.AddWithValue("$filePath", snippet.FilePath);
        command.Parameters.AddWithValue("$id", snippet.Id);
        command.ExecuteNonQuery();
    }

    public void TouchLastCopied(long id, DateTime lastCopiedUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Snippets SET LastCopiedAt = $when WHERE Id = $id";
        command.Parameters.AddWithValue("$when", FormatDate(lastCopiedUtc));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Snippets WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Snippet> Search(string? text, string? category)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Escape LIKE wildcards so "%" or "_" in the search box are matched literally.
            var pattern = "%" + text.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            where.Add(@"(Name LIKE $text ESCAPE '\' OR Tags LIKE $text ESCAPE '\' OR Content LIKE $text ESCAPE '\')");
            command.Parameters.AddWithValue("$text", pattern);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            where.Add("Category = $category");
            command.Parameters.AddWithValue("$category", category);
        }

        command.CommandText =
            $"SELECT {Columns} FROM Snippets" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty) +
            " ORDER BY LastCopiedAt DESC, Id DESC";

        return ReadAll(command);
    }

    public IReadOnlyList<string> GetCategories()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT Category FROM Snippets ORDER BY Category COLLATE NOCASE";

        var categories = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            categories.Add(reader.GetString(0));
        }

        return categories;
    }

    public IReadOnlyList<Snippet> GetAll() => Search(null, null);

    public void ReplaceAll(IEnumerable<Snippet> snippets)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Snippets";
            delete.ExecuteNonQuery();
        }

        foreach (var snippet in snippets)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            InsertCore(insert, snippet);
        }

        transaction.Commit();
    }

    private static void InsertCore(SqliteCommand command, Snippet snippet)
    {
        command.CommandText = """
            INSERT INTO Snippets (Name, Category, Tags, FilePath, Hash, ActionCount, Source, Content, CreatedAt, LastCopiedAt)
            VALUES ($name, $category, $tags, $filePath, $hash, $actionCount, $source, $content, $createdAt, $lastCopiedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", snippet.Name);
        command.Parameters.AddWithValue("$category", snippet.Category);
        command.Parameters.AddWithValue("$tags", snippet.Tags);
        command.Parameters.AddWithValue("$filePath", snippet.FilePath);
        command.Parameters.AddWithValue("$hash", snippet.Hash);
        command.Parameters.AddWithValue("$actionCount", snippet.ActionCount);
        command.Parameters.AddWithValue("$source", snippet.Source.ToString());
        command.Parameters.AddWithValue("$content", snippet.Content);
        command.Parameters.AddWithValue("$createdAt", FormatDate(snippet.CreatedAt));
        command.Parameters.AddWithValue("$lastCopiedAt", FormatDate(snippet.LastCopiedAt));
        snippet.Id = (long)command.ExecuteScalar()!;
    }

    private Snippet? QuerySingle(string condition, object value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Snippets WHERE {condition} LIMIT 1";
        command.Parameters.AddWithValue("$value", value);
        return ReadAll(command).FirstOrDefault();
    }

    private static List<Snippet> ReadAll(SqliteCommand command)
    {
        var result = new List<Snippet>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Snippet
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Category = reader.GetString(2),
                Tags = reader.GetString(3),
                FilePath = reader.GetString(4),
                Hash = reader.GetString(5),
                ActionCount = reader.GetInt32(6),
                Source = Enum.TryParse<SnippetSource>(reader.GetString(7), out var source) ? source : SnippetSource.Manual,
                Content = reader.GetString(8),
                CreatedAt = ParseDate(reader.GetString(9)),
                LastCopiedAt = ParseDate(reader.GetString(10)),
            });
        }

        return result;
    }

    // ISO-8601 round-trip format sorts correctly as text.
    private static string FormatDate(DateTime value) =>
        DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
