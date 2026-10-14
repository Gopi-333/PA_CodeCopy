namespace PaCodeCopy.Core.Extraction;

public enum SubflowOutcome
{
    /// <summary>Saved as a new snippet.</summary>
    Saved,

    /// <summary>The same code was already saved; nothing new was written.</summary>
    Unchanged,

    /// <summary>Nothing was copied (empty subflow).</summary>
    Empty,

    /// <summary>Copying or saving failed; see Message.</summary>
    Failed
}

public sealed record SubflowResult(string Subflow, SubflowOutcome Outcome, string Message, long? SnippetId = null);

public sealed record ExtractionReport(
    string FlowName,
    IReadOnlyList<SubflowResult> Subflows,
    bool Cancelled,
    bool CurrentSubflowOnly)
{
    public int Count(SubflowOutcome outcome) => Subflows.Count(s => s.Outcome == outcome);

    /// <summary>One line for the status bar, e.g. "Flow 'Invoices': 4 subflows → 3 saved, 1 unchanged".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Count(SubflowOutcome.Saved)} saved" };
            if (Count(SubflowOutcome.Unchanged) > 0) parts.Add($"{Count(SubflowOutcome.Unchanged)} unchanged");
            if (Count(SubflowOutcome.Empty) > 0) parts.Add($"{Count(SubflowOutcome.Empty)} empty");
            if (Count(SubflowOutcome.Failed) > 0) parts.Add($"{Count(SubflowOutcome.Failed)} failed");

            var text = $"Flow '{FlowName}': {Subflows.Count} subflow(s) → {string.Join(", ", parts)}";
            if (CurrentSubflowOnly) text += " (only the open subflow: subflow tabs were not found)";
            if (Cancelled) text += " — cancelled";
            return text;
        }
    }

    /// <summary>Multi-line text for the result dialog.</summary>
    public string Details => string.Join(Environment.NewLine,
        Subflows.Select(s => $"{Icon(s.Outcome)} {s.Subflow}: {s.Message}"));

    private static string Icon(SubflowOutcome outcome) => outcome switch
    {
        // Plain characters: symbol glyphs are missing from some message-box fonts.
        SubflowOutcome.Saved => "[+]",
        SubflowOutcome.Unchanged => "[=]",
        SubflowOutcome.Empty => "[ ]",
        _ => "[!]",
    };
}
