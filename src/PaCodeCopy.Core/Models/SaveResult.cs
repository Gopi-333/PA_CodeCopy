namespace PaCodeCopy.Core.Models;

public enum SaveOutcome
{
    /// <summary>A new snippet was written to disk and indexed.</summary>
    Saved,

    /// <summary>The same code is already saved; only its "last copied" time was updated.</summary>
    Duplicate,

    /// <summary>The text does not look like Power Automate Desktop code (auto-capture only).</summary>
    NotPadScript,

    /// <summary>Nothing to save (null or whitespace).</summary>
    Empty,

    /// <summary>Saving failed; see the log for details.</summary>
    Failed
}

public sealed record SaveResult(SaveOutcome Outcome, Snippet? Snippet, string Message)
{
    public bool IsSuccess => Outcome is SaveOutcome.Saved or SaveOutcome.Duplicate;
}
