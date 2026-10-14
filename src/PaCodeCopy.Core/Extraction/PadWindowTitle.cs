namespace PaCodeCopy.Core.Extraction;

/// <summary>Reads the flow name from a PAD designer window title.</summary>
/// <remarks>
/// Assumption: the designer title starts with the flow name followed by a separator and the product name,
/// e.g. "Invoice processing | Power Automate". Anything that does not match falls back to the whole title.
/// </remarks>
public static class PadWindowTitle
{
    private static readonly string[] Separators = { " | ", " - ", " – " };

    public static string ParseFlowName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "PAD flow";
        }

        var text = title.Trim().TrimStart('*').Trim(); // "*" marks unsaved changes in some editors
        foreach (var separator in Separators)
        {
            var index = text.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return text[..index].Trim();
            }
        }

        return text;
    }

    /// <summary>True for titles that look like a PAD window (used when the process name is unknown).</summary>
    public static bool LooksLikePad(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        title.Contains("Power Automate", StringComparison.OrdinalIgnoreCase);
}
