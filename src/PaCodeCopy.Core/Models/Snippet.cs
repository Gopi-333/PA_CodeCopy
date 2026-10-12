namespace PaCodeCopy.Core.Models;

/// <summary>How a snippet entered the library.</summary>
public enum SnippetSource
{
    /// <summary>Captured automatically by the clipboard watcher.</summary>
    Auto,

    /// <summary>Saved by the user (button or paste box).</summary>
    Manual
}

/// <summary>
/// One saved block of Power Automate Desktop code. The code itself lives in <see cref="FilePath"/>;
/// <see cref="Content"/> is a copy kept in the index so search can look inside the code.
/// </summary>
public sealed class Snippet
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    /// <summary>Comma-separated tags, e.g. "excel, invoices".</summary>
    public string Tags { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    /// <summary>SHA-256 of the normalised code, used to detect duplicates.</summary>
    public string Hash { get; set; } = string.Empty;

    public int ActionCount { get; set; }

    public SnippetSource Source { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>UTC time the snippet was first saved.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC time the same code was last copied (updated on duplicates).</summary>
    public DateTime LastCopiedAt { get; set; }
}
