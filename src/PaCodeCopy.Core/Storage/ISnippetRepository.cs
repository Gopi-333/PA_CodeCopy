using PaCodeCopy.Core.Models;

namespace PaCodeCopy.Core.Storage;

/// <summary>Searchable index of saved snippets.</summary>
public interface ISnippetRepository
{
    void Initialize();

    long Insert(Snippet snippet);

    Snippet? GetById(long id);

    Snippet? GetByHash(string hash);

    /// <summary>Updates name, category, tags and file path.</summary>
    void Update(Snippet snippet);

    void TouchLastCopied(long id, DateTime lastCopiedUtc);

    void Delete(long id);

    /// <summary>Searches name, tags and code. Null/empty arguments mean "no filter". Newest first.</summary>
    IReadOnlyList<Snippet> Search(string? text, string? category);

    IReadOnlyList<string> GetCategories();

    IReadOnlyList<Snippet> GetAll();

    /// <summary>Replaces the whole index in one transaction (used by "Rebuild index").</summary>
    void ReplaceAll(IEnumerable<Snippet> snippets);
}
