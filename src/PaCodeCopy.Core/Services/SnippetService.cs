using System.Security.Cryptography;
using System.Text;
using PaCodeCopy.Core.Detection;
using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Models;
using PaCodeCopy.Core.Storage;

namespace PaCodeCopy.Core.Services;

/// <summary>
/// Single entry point for the UI: detects PAD code, removes duplicates, writes the file and indexes it.
/// </summary>
/// <remarks>
/// Snippet code is never written to the log, because PAD flows can contain passwords or other secrets.
/// Only names and short hash prefixes are logged.
/// </remarks>
public sealed class SnippetService
{
    private readonly ISnippetRepository _repository;
    private readonly PadScriptDetector _detector;
    private readonly IAppLogger _logger;
    private readonly Func<DateTime> _utcNow;

    public SnippetService(
        ISnippetRepository repository,
        SnippetFileStore fileStore,
        PadScriptDetector detector,
        IAppLogger logger,
        Func<DateTime>? utcNow = null)
    {
        _repository = repository;
        FileStore = fileStore;
        _detector = detector;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public SnippetFileStore FileStore { get; private set; }

    public string DefaultCategory { get; set; } = "General";

    /// <summary>Switches the folder new snippets are written to. Existing snippets keep their paths.</summary>
    public void ChangeSnippetsFolder(string rootFolder)
    {
        FileStore = new SnippetFileStore(rootFolder);
        _logger.Info($"Snippets folder changed to '{FileStore.RootFolder}'.");
    }

    public void SetDetectionThreshold(int threshold) => _detector.Threshold = threshold;

    public DetectionResult Analyze(string? text) => _detector.Analyze(text);

    /// <summary>
    /// Saves code as a snippet.
    /// </summary>
    /// <param name="text">The code (usually clipboard text).</param>
    /// <param name="source">Auto (clipboard watcher) or Manual (user action).</param>
    /// <param name="requirePadScript">When true, text that does not look like PAD code is ignored.</param>
    /// <param name="name">Optional name; defaults to the first action, e.g. "Excel.LaunchExcel +5 more".</param>
    /// <param name="category">Optional category; defaults to <see cref="DefaultCategory"/>.</param>
    /// <param name="tags">Optional comma-separated tags.</param>
    /// <param name="namePrefix">
    /// Optional prefix for the generated name (used when <paramref name="name"/> is empty), e.g. the PAD subflow:
    /// "Main" gives "Main - Excel.LaunchExcel +5 more".
    /// </param>
    public SaveResult Save(
        string? text,
        SnippetSource source,
        bool requirePadScript,
        string? name = null,
        string? category = null,
        string? tags = null,
        string? namePrefix = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SaveResult(SaveOutcome.Empty, null, "Nothing to save: the text is empty.");
        }

        var detection = _detector.Analyze(text);
        if (requirePadScript && !detection.IsPadScript)
        {
            return new SaveResult(SaveOutcome.NotPadScript, null, "Clipboard text is not Power Automate Desktop code.");
        }

        var hash = ComputeHash(text);
        var now = _utcNow();

        try
        {
            var existing = _repository.GetByHash(hash);
            if (existing is not null)
            {
                if (File.Exists(existing.FilePath))
                {
                    _repository.TouchLastCopied(existing.Id, now);
                    existing.LastCopiedAt = now;
                    return new SaveResult(SaveOutcome.Duplicate, existing, $"Already saved as '{existing.Name}'.");
                }

                // The file was deleted outside the app: drop the stale row and save again.
                _logger.Warn($"Snippet '{existing.Name}' ({ShortHash(hash)}) file is missing; saving it again.");
                _repository.Delete(existing.Id);
            }

            var snippet = new Snippet
            {
                Name = !string.IsNullOrWhiteSpace(name) ? name.Trim()
                    : !string.IsNullOrWhiteSpace(namePrefix) ? $"{namePrefix.Trim()} - {DefaultName(detection)}"
                    : DefaultName(detection),
                Category = string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim(),
                Tags = NormalizeTags(tags),
                Hash = hash,
                ActionCount = detection.ActionCount,
                Source = source,
                Content = text,
                CreatedAt = now,
                LastCopiedAt = now,
            };

            snippet.FilePath = FileStore.Write(snippet.Category, snippet.Name, now.ToLocalTime(), text);

            try
            {
                _repository.Insert(snippet);
            }
            catch
            {
                // Keep disk and index in sync: no file without an index row.
                TryDeleteFile(snippet.FilePath);
                throw;
            }

            _logger.Info($"Saved snippet '{snippet.Name}' ({ShortHash(hash)}, {snippet.ActionCount} actions, {source}).");
            return new SaveResult(SaveOutcome.Saved, snippet, $"Saved '{snippet.Name}'.");
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            _logger.Error($"Failed to save snippet ({ShortHash(hash)}).", ex);
            return new SaveResult(SaveOutcome.Failed, null, $"Could not save the snippet: {ex.Message}");
        }
    }

    public IReadOnlyList<Snippet> Search(string? text, string? category) => _repository.Search(text, category);

    public IReadOnlyList<string> GetCategories() => _repository.GetCategories();

    /// <summary>Reads the code from disk, falling back to the indexed copy if the file is gone.</summary>
    public string ReadContent(Snippet snippet)
    {
        try
        {
            return FileStore.Read(snippet.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not read '{snippet.FilePath}'; using the indexed copy.", ex);
            return snippet.Content;
        }
    }

    /// <summary>Renames / re-categorises a snippet and moves its file to match.</summary>
    public void UpdateDetails(Snippet snippet, string name, string category, string tags)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        var newCategory = string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim();
        var newPath = snippet.FilePath;
        if (File.Exists(snippet.FilePath))
        {
            newPath = FileStore.Move(snippet.FilePath, newCategory, name.Trim(), snippet.CreatedAt.ToLocalTime());
        }

        var oldPath = snippet.FilePath;
        snippet.Name = name.Trim();
        snippet.Category = newCategory;
        snippet.Tags = NormalizeTags(tags);
        snippet.FilePath = newPath;

        try
        {
            _repository.Update(snippet);
        }
        catch
        {
            // Put the file back so the index still points at it.
            if (!string.Equals(oldPath, newPath, StringComparison.Ordinal) && File.Exists(newPath))
            {
                File.Move(newPath, oldPath);
            }

            throw;
        }

        _logger.Info($"Updated snippet '{snippet.Name}' (#{snippet.Id}).");
    }

    public void Delete(Snippet snippet)
    {
        _repository.Delete(snippet.Id);
        TryDeleteFile(snippet.FilePath);
        _logger.Info($"Deleted snippet '{snippet.Name}' (#{snippet.Id}).");
    }

    /// <summary>
    /// Rebuilds the index from the files in the snippets folder. Tags and source of snippets
    /// that are still on disk are kept. Returns the number of indexed snippets.
    /// </summary>
    public int RebuildIndex()
    {
        var previous = _repository.GetAll().ToDictionary(s => s.Hash, StringComparer.Ordinal);
        var rebuilt = new Dictionary<string, Snippet>(StringComparer.Ordinal);

        foreach (var path in FileStore.EnumerateSnippetFiles())
        {
            try
            {
                var content = FileStore.Read(path);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var hash = ComputeHash(content);
                if (rebuilt.ContainsKey(hash))
                {
                    _logger.Warn($"Duplicate snippet file skipped during rebuild: '{path}'.");
                    continue;
                }

                var (category, name, timestamp) = FileStore.ParsePath(path, DefaultCategory);
                var created = timestamp?.ToUniversalTime() ?? File.GetCreationTimeUtc(path);
                previous.TryGetValue(hash, out var old);

                rebuilt[hash] = new Snippet
                {
                    Name = old?.Name ?? name,
                    Category = category,
                    Tags = old?.Tags ?? string.Empty,
                    FilePath = path,
                    Hash = hash,
                    ActionCount = _detector.Analyze(content).ActionCount,
                    Source = old?.Source ?? SnippetSource.Manual,
                    Content = content,
                    CreatedAt = old?.CreatedAt ?? created,
                    LastCopiedAt = old?.LastCopiedAt ?? created,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"Skipped unreadable file during rebuild: '{path}'.", ex);
            }
        }

        _repository.ReplaceAll(rebuilt.Values);
        _logger.Info($"Index rebuilt from '{FileStore.RootFolder}': {rebuilt.Count} snippets.");
        return rebuilt.Count;
    }

    /// <summary>Line endings and trailing whitespace do not change the hash.</summary>
    public static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim('\n');
    }

    public static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    /// <summary>"excel,  Invoices ,excel" → "excel, Invoices".</summary>
    public static string NormalizeTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? string.Empty
            : string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));

    // The file name already starts with the date/time, so it is not repeated here.
    private static string DefaultName(DetectionResult detection) =>
        detection.FirstAction is null ? "Snippet"
        : detection.ActionCount > 1 ? $"{detection.FirstAction} +{detection.ActionCount - 1} more"
        : detection.FirstAction;

    private static string ShortHash(string hash) => hash.Length > 8 ? hash[..8] : hash;

    private static bool IsStorageException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException;

    private void TryDeleteFile(string path)
    {
        try
        {
            FileStore.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not delete file '{path}'.", ex);
        }
    }
}
