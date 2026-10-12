using Microsoft.Data.Sqlite;
using PaCodeCopy.Core.Models;
using PaCodeCopy.Core.Storage;

namespace PaCodeCopy.Tests;

public sealed class SqliteSnippetRepositoryTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SqliteSnippetRepository _repository;

    public SqliteSnippetRepositoryTests()
    {
        _repository = new SqliteSnippetRepository(_temp.Combine("data", "index.db"));
        _repository.Initialize();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Insert_assigns_id_and_values_round_trip()
    {
        var snippet = NewSnippet("Read Excel", "Excel", "excel, input", "H1", "Excel.ReadFromExcel ...");

        var id = _repository.Insert(snippet);
        var loaded = _repository.GetByHash("H1");

        Assert.True(id > 0);
        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.Id);
        Assert.Equal("Read Excel", loaded.Name);
        Assert.Equal("excel, input", loaded.Tags);
        Assert.Equal(SnippetSource.Auto, loaded.Source);
        Assert.Equal(snippet.CreatedAt, loaded.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        _repository.Initialize();
        _repository.Insert(NewSnippet("A", "General", "", "H1", "x"));
        _repository.Initialize();

        Assert.Single(_repository.GetAll());
    }

    [Fact]
    public void Duplicate_hash_is_rejected()
    {
        _repository.Insert(NewSnippet("A", "General", "", "SAME", "x"));

        Assert.Throws<SqliteException>(() => _repository.Insert(NewSnippet("B", "General", "", "SAME", "y")));
    }

    [Fact]
    public void Search_matches_name_tags_and_content_and_filters_category()
    {
        _repository.Insert(NewSnippet("Read invoices", "Excel", "", "H1", "Excel.ReadFromExcel"));
        _repository.Insert(NewSnippet("Login", "Web", "portal", "H2", "WebAutomation.LaunchChrome"));
        _repository.Insert(NewSnippet("Send mail", "Outlook", "", "H3", "Outlook.SendEmail"));

        Assert.Equal("Read invoices", Assert.Single(_repository.Search("invoice", null)).Name);
        Assert.Equal("Login", Assert.Single(_repository.Search("PORTAL", null)).Name);
        Assert.Equal("Send mail", Assert.Single(_repository.Search("SendEmail", null)).Name);
        Assert.Equal("Login", Assert.Single(_repository.Search(null, "Web")).Name);
        Assert.Empty(_repository.Search("invoice", "Web"));
        Assert.Equal(3, _repository.Search("  ", null).Count);
    }

    [Fact]
    public void Search_treats_like_wildcards_literally()
    {
        _repository.Insert(NewSnippet("Discount 100%", "General", "", "H1", "x"));
        _repository.Insert(NewSnippet("Other", "General", "", "H2", "y"));

        Assert.Single(_repository.Search("100%", null));
        Assert.DoesNotContain(_repository.Search("%", null), s => s.Name == "Other");
        Assert.Empty(_repository.Search("_", null));
    }

    [Fact]
    public void Search_returns_most_recently_copied_first()
    {
        var older = NewSnippet("Older", "General", "", "H1", "x");
        var newer = NewSnippet("Newer", "General", "", "H2", "y");
        _repository.Insert(older);
        _repository.Insert(newer);

        _repository.TouchLastCopied(older.Id, DateTime.UtcNow.AddHours(1));

        Assert.Equal(new[] { "Older", "Newer" }, _repository.GetAll().Select(s => s.Name));
    }

    [Fact]
    public void Update_and_delete_work()
    {
        var snippet = NewSnippet("A", "General", "", "H1", "x");
        _repository.Insert(snippet);

        snippet.Name = "Renamed";
        snippet.Category = "Excel";
        snippet.Tags = "t1";
        snippet.FilePath = "new.txt";
        _repository.Update(snippet);
        var updated = _repository.GetById(snippet.Id)!;

        Assert.Equal(("Renamed", "Excel", "t1", "new.txt"), (updated.Name, updated.Category, updated.Tags, updated.FilePath));

        _repository.Delete(snippet.Id);
        Assert.Null(_repository.GetById(snippet.Id));
    }

    [Fact]
    public void GetCategories_returns_distinct_sorted_values()
    {
        _repository.Insert(NewSnippet("A", "web", "", "H1", "x"));
        _repository.Insert(NewSnippet("B", "Excel", "", "H2", "y"));
        _repository.Insert(NewSnippet("C", "Excel", "", "H3", "z"));

        Assert.Equal(new[] { "Excel", "web" }, _repository.GetCategories());
    }

    [Fact]
    public void ReplaceAll_swaps_the_whole_index()
    {
        _repository.Insert(NewSnippet("Old", "General", "", "H1", "x"));

        _repository.ReplaceAll(new[] { NewSnippet("New1", "General", "", "N1", "a"), NewSnippet("New2", "General", "", "N2", "b") });

        Assert.Equal(new[] { "New1", "New2" }, _repository.GetAll().Select(s => s.Name).OrderBy(n => n));
    }

    [Fact]
    public void ReplaceAll_rolls_back_on_error()
    {
        _repository.Insert(NewSnippet("Keep", "General", "", "H1", "x"));

        Assert.Throws<SqliteException>(() => _repository.ReplaceAll(new[]
        {
            NewSnippet("Dup1", "General", "", "DUP", "a"),
            NewSnippet("Dup2", "General", "", "DUP", "b"),
        }));

        Assert.Equal("Keep", Assert.Single(_repository.GetAll()).Name);
    }

    private static Snippet NewSnippet(string name, string category, string tags, string hash, string content)
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        return new Snippet
        {
            Name = name,
            Category = category,
            Tags = tags,
            Hash = hash,
            Content = content,
            FilePath = name + ".txt",
            ActionCount = 1,
            Source = SnippetSource.Auto,
            CreatedAt = now,
            LastCopiedAt = now,
        };
    }
}
