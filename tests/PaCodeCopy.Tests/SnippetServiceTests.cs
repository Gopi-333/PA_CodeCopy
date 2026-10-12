using Microsoft.Data.Sqlite;
using PaCodeCopy.Core.Detection;
using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Models;
using PaCodeCopy.Core.Services;
using PaCodeCopy.Core.Storage;

namespace PaCodeCopy.Tests;

public sealed class SnippetServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SqliteSnippetRepository _repository;
    private readonly SnippetService _service;
    private DateTime _now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    public SnippetServiceTests()
    {
        _repository = new SqliteSnippetRepository(_temp.Combine("index.db"));
        _repository.Initialize();
        _service = CreateService(_repository);
    }

    public void Dispose() => _temp.Dispose();

    private string SnippetsRoot => _temp.Combine("Snippets");

    [Fact]
    public void Auto_save_of_pad_code_writes_file_and_index()
    {
        var result = _service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snippet = result.Snippet!;
        Assert.True(File.Exists(snippet.FilePath));
        Assert.Equal(Samples.ExcelLoop, File.ReadAllText(snippet.FilePath));
        Assert.StartsWith("SET ", snippet.Name);
        Assert.Equal("General", snippet.Category);
        Assert.Equal(6, snippet.ActionCount);
        Assert.NotNull(_repository.GetByHash(snippet.Hash));
    }

    [Fact]
    public void Auto_save_ignores_non_pad_text()
    {
        var result = _service.Save(Samples.Prose, SnippetSource.Auto, requirePadScript: true);

        Assert.Equal(SaveOutcome.NotPadScript, result.Outcome);
        Assert.Empty(_repository.GetAll());
        Assert.False(Directory.Exists(SnippetsRoot));
    }

    [Fact]
    public void Manual_save_accepts_any_text_with_given_details()
    {
        var result = _service.Save(Samples.Prose, SnippetSource.Manual, requirePadScript: false,
            name: "Notes", category: "Misc", tags: "a, b ,a");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(("Notes", "Misc", "a, b"), (result.Snippet!.Name, result.Snippet.Category, result.Snippet.Tags));
        Assert.Equal("Misc", Path.GetFileName(Path.GetDirectoryName(result.Snippet.FilePath)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  \r\n ")]
    public void Empty_text_is_not_saved(string? text)
    {
        Assert.Equal(SaveOutcome.Empty, _service.Save(text, SnippetSource.Manual, requirePadScript: false).Outcome);
    }

    [Fact]
    public void Same_code_with_different_line_endings_is_a_duplicate()
    {
        var first = _service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true);
        _now = _now.AddMinutes(5);

        var second = _service.Save(Samples.ExcelLoop.Replace("\n", "\r\n") + "\r\n  ", SnippetSource.Auto, requirePadScript: true);

        Assert.Equal(SaveOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.Snippet!.Id, second.Snippet!.Id);
        Assert.Single(_repository.GetAll());
        Assert.Equal(_now, _repository.GetById(first.Snippet.Id)!.LastCopiedAt);
        Assert.Single(Directory.GetFiles(SnippetsRoot, "*.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void Duplicate_whose_file_was_deleted_is_saved_again()
    {
        var first = _service.Save(Samples.SingleMessageBox, SnippetSource.Auto, requirePadScript: true);
        File.Delete(first.Snippet!.FilePath);

        var second = _service.Save(Samples.SingleMessageBox, SnippetSource.Auto, requirePadScript: true);

        Assert.Equal(SaveOutcome.Saved, second.Outcome);
        Assert.True(File.Exists(second.Snippet!.FilePath));
        Assert.Single(_repository.GetAll());
    }

    [Fact]
    public void Failed_index_insert_removes_the_written_file()
    {
        var service = CreateService(new FailingInsertRepository(_repository));

        var result = service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.Exists(SnippetsRoot)
            ? Directory.GetFiles(SnippetsRoot, "*.txt", SearchOption.AllDirectories)
            : Array.Empty<string>());
    }

    [Fact]
    public void UpdateDetails_moves_file_and_updates_index()
    {
        var snippet = _service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true).Snippet!;
        var oldPath = snippet.FilePath;

        _service.UpdateDetails(snippet, "Excel loop", "Excel", "excel,loop");

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(snippet.FilePath));
        Assert.EndsWith("_Excel loop.txt", snippet.FilePath);
        var stored = _repository.GetById(snippet.Id)!;
        Assert.Equal(("Excel loop", "Excel", "excel, loop", snippet.FilePath), (stored.Name, stored.Category, stored.Tags, stored.FilePath));
    }

    [Fact]
    public void Delete_removes_file_and_row()
    {
        var snippet = _service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true).Snippet!;

        _service.Delete(snippet);

        Assert.False(File.Exists(snippet.FilePath));
        Assert.Null(_repository.GetById(snippet.Id));
    }

    [Fact]
    public void ReadContent_falls_back_to_index_copy_when_file_is_missing()
    {
        var snippet = _service.Save(Samples.IfElse, SnippetSource.Auto, requirePadScript: true).Snippet!;
        File.Delete(snippet.FilePath);

        Assert.Equal(Samples.IfElse, _service.ReadContent(snippet));
    }

    [Fact]
    public void RebuildIndex_picks_up_files_and_keeps_tags()
    {
        var tagged = _service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true, tags: "keep-me").Snippet!;
        var removed = _service.Save(Samples.IfElse, SnippetSource.Auto, requirePadScript: true).Snippet!;
        File.Delete(removed.FilePath);
        Directory.CreateDirectory(Path.Combine(SnippetsRoot, "Imported"));
        File.WriteAllText(Path.Combine(SnippetsRoot, "Imported", "20260101_120000_From backup.txt"), Samples.SingleMessageBox);

        var count = _service.RebuildIndex();

        Assert.Equal(2, count);
        Assert.Equal("keep-me", _repository.GetByHash(tagged.Hash)!.Tags);
        var imported = _repository.GetByHash(SnippetService.ComputeHash(Samples.SingleMessageBox))!;
        Assert.Equal(("From backup", "Imported", 1), (imported.Name, imported.Category, imported.ActionCount));
        Assert.Null(_repository.GetByHash(removed.Hash));
    }

    [Fact]
    public void ChangeSnippetsFolder_redirects_new_files()
    {
        var newRoot = _temp.Combine("Other");
        _service.ChangeSnippetsFolder(newRoot);

        var snippet = _service.Save(Samples.ExcelLoop, SnippetSource.Auto, requirePadScript: true).Snippet!;

        Assert.StartsWith(newRoot, snippet.FilePath);
    }

    [Fact]
    public void Hash_ignores_line_endings_and_trailing_whitespace_only()
    {
        Assert.Equal(SnippetService.ComputeHash("a\r\nb  \r\n"), SnippetService.ComputeHash("a\nb"));
        Assert.NotEqual(SnippetService.ComputeHash("a\nb"), SnippetService.ComputeHash("a\n  b"));
    }

    private SnippetService CreateService(ISnippetRepository repository) =>
        new(repository, new SnippetFileStore(SnippetsRoot), new PadScriptDetector(3), NullLogger.Instance, () => _now);

    /// <summary>Delegates to a real repository but fails every insert, to test rollback.</summary>
    private sealed class FailingInsertRepository : ISnippetRepository
    {
        private readonly ISnippetRepository _inner;

        public FailingInsertRepository(ISnippetRepository inner) => _inner = inner;

        public long Insert(Snippet snippet) => throw new SqliteException("disk I/O error", 10);

        public void Initialize() => _inner.Initialize();

        public Snippet? GetById(long id) => _inner.GetById(id);

        public Snippet? GetByHash(string hash) => _inner.GetByHash(hash);

        public void Update(Snippet snippet) => _inner.Update(snippet);

        public void TouchLastCopied(long id, DateTime lastCopiedUtc) => _inner.TouchLastCopied(id, lastCopiedUtc);

        public void Delete(long id) => _inner.Delete(id);

        public IReadOnlyList<Snippet> Search(string? text, string? category) => _inner.Search(text, category);

        public IReadOnlyList<string> GetCategories() => _inner.GetCategories();

        public IReadOnlyList<Snippet> GetAll() => _inner.GetAll();

        public void ReplaceAll(IEnumerable<Snippet> snippets) => _inner.ReplaceAll(snippets);
    }
}
