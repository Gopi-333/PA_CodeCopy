using System.Text.RegularExpressions;

namespace PaCodeCopy.Core.Detection;

/// <summary>Result of analysing a block of text.</summary>
/// <param name="IsPadScript">True when the text looks like Power Automate Desktop code.</param>
/// <param name="Score">Total heuristic score (compare with the threshold).</param>
/// <param name="ActionCount">Number of lines recognised as PAD actions or control statements.</param>
/// <param name="FirstAction">
/// First module action (e.g. "Excel.ReadFromExcel"), or the first statement keyword (e.g. "SET") if there is
/// no module action. Used for default snippet names.
/// </param>
public sealed record DetectionResult(bool IsPadScript, int Score, int ActionCount, string? FirstAction);

/// <summary>
/// Decides whether clipboard text is Power Automate Desktop code.
/// </summary>
/// <remarks>
/// When you select actions in the PAD designer and press Ctrl+C, PAD copies them as plain text
/// in its scripting language, for example:
/// <code>
/// SET Counter TO 0
/// Excel.LaunchExcel.LaunchAndOpenUnderExistingProcess Path: $'''C:\Data\In.xlsx''' Visible: True Instance=> ExcelInstance
/// LOOP FOREACH Row IN ExcelData
///     Variables.IncreaseVariable Value: Counter IncrementValue: 1
/// END
/// </code>
/// The detector scores each line against those patterns. Matching is heuristic: the threshold is a
/// user setting so it can be tuned if real-world copies are missed or false positives appear.
/// </remarks>
public sealed class PadScriptDetector
{
    private const int StrongStatementScore = 3;
    private const int ActionScore = 3;
    private const int MarkerScore = 1;
    private const int AnnotationScore = 2;
    private const int ControlRepositoryScore = 3;

    // Very large clipboard contents are scanned only partially to keep the UI responsive.
    private const int MaxLinesToScan = 2000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // Disabled actions are copied with a "DISABLE " prefix.
    private static readonly Regex DisablePrefix = new(@"^\s*DISABLE\s+", RegexOptions.Compiled, RegexTimeout);

    // Control statements. Case-sensitive on purpose: PAD writes keywords in upper case,
    // which keeps ordinary prose and SQL (e.g. "SET NOCOUNT ON") from matching.
    private static readonly (string Keyword, Regex Pattern)[] Statements =
    {
        ("SET", Pattern(@"^SET\s+\S+\s+TO\b")),
        ("IF", Pattern(@"^IF\s+.+\s+THEN\s*$")),
        ("ELSE IF", Pattern(@"^ELSE\s+IF\s+.+\s+THEN\s*$")),
        ("LOOP", Pattern(@"^LOOP\s+\S+\s+FROM\s+.+\s+TO\s+.+\s+STEP\s+\S+")),
        ("LOOP WHILE", Pattern(@"^LOOP\s+WHILE\s+.+")),
        ("FOREACH", Pattern(@"^(LOOP\s+)?FOREACH\s+\S+\s+IN\s+\S+")),
        ("SWITCH", Pattern(@"^SWITCH\s+\S+")),
        ("CASE", Pattern(@"^CASE\s+.+")),
        ("DEFAULT", Pattern(@"^DEFAULT\s*$")),
        ("CALL", Pattern(@"^CALL\s+\S+")),
        ("LABEL", Pattern(@"^LABEL\s+\S+")),
        ("GOTO", Pattern(@"^GOTO\s+\S+")),
        ("BLOCK", Pattern(@"^BLOCK\s+\S+")),
        ("ON BLOCK ERROR", Pattern(@"^ON\s+BLOCK\s+ERROR\b")),
        ("ON ERROR", Pattern(@"^ON\s+ERROR\b")),
        ("WAIT", Pattern(@"^WAIT\s+\S+")),
        ("EXIT", Pattern(@"^EXIT\s+(LOOP|FUNCTION|Code:)")),
        ("NEXT LOOP", Pattern(@"^NEXT\s+LOOP\s*$")),
        ("FUNCTION", Pattern(@"^FUNCTION\s+\S+\s+GLOBAL\b")),
    };

    // Lines like "END" / "ELSE" add a little weight but do not count as actions on their own.
    private static readonly Regex WeakStatement = Pattern(@"^(END(\s+\w+)?|ELSE|END FUNCTION)\s*$");

    // Module.Action[.Variant] followed by "Param: value" or "Output=> var", or alone on the line.
    // Examples: "Excel.CloseExcel.Close Instance: ExcelInstance", "Display.ShowMessageDialog.ShowMessage Title: ..."
    private static readonly Regex ActionLine = Pattern(
        @"^(?<action>[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,3})(?<rest>\s+[A-Za-z][A-Za-z0-9]*\s*(?::|=>).*|\s*)$");

    private static readonly Regex OutputBinding = Pattern(@"[A-Za-z0-9]=>\s*\S");
    private static readonly Regex VerbatimString = Pattern(@"\$'''");
    private static readonly Regex VariableReference = Pattern(@"%[A-Za-z_][A-Za-z0-9_.\[\]'""]*%");
    private static readonly Regex Annotation = Pattern(@"^@@\S+");
    private static readonly Regex ControlRepository = Pattern(@"^#\s*\[ControlRepository\]");

    private int _threshold;

    public PadScriptDetector(int threshold = 3)
    {
        Threshold = threshold;
    }

    /// <summary>Minimum score for text to count as PAD code.</summary>
    public int Threshold
    {
        get => _threshold;
        set => _threshold = Math.Max(1, value);
    }

    public DetectionResult Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DetectionResult(false, 0, 0, null);
        }

        var score = 0;
        var actionCount = 0;
        string? firstKeyword = null;
        string? firstModuleAction = null;
        var hasControlRepository = false;

        var lines = text.Split('\n');
        var lineCount = Math.Min(lines.Length, MaxLinesToScan);

        try
        {
            for (var i = 0; i < lineCount; i++)
            {
                var line = DisablePrefix.Replace(lines[i].Trim(), string.Empty);
                if (line.Length == 0)
                {
                    continue;
                }

                if (ControlRepository.IsMatch(line))
                {
                    hasControlRepository = true;
                    score += ControlRepositoryScore;
                    continue;
                }

                if (Annotation.IsMatch(line))
                {
                    score += AnnotationScore;
                    continue;
                }

                var keyword = MatchStatement(line);
                if (keyword is not null)
                {
                    score += StrongStatementScore;
                    actionCount++;
                    firstKeyword ??= keyword;
                    score += MarkerScoreFor(line);
                    continue;
                }

                if (WeakStatement.IsMatch(line))
                {
                    score += MarkerScore;
                    continue;
                }

                var action = ActionLine.Match(line);
                if (action.Success)
                {
                    var hasParameters = action.Groups["rest"].Value.Trim().Length > 0;

                    // A bare "Word.Word" line is too ambiguous to count on its own.
                    score += hasParameters ? ActionScore : MarkerScore;
                    if (hasParameters)
                    {
                        actionCount++;
                        firstModuleAction ??= ShortActionName(action.Groups["action"].Value);
                    }

                    score += MarkerScoreFor(line);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: treat as "not PAD" rather than freezing the UI.
            return new DetectionResult(false, score, actionCount, firstModuleAction ?? firstKeyword);
        }

        var isPad = score >= Threshold && (actionCount > 0 || hasControlRepository);
        return new DetectionResult(isPad, score, actionCount, firstModuleAction ?? firstKeyword);
    }

    private static string? MatchStatement(string line)
    {
        foreach (var (keyword, pattern) in Statements)
        {
            if (pattern.IsMatch(line))
            {
                return keyword;
            }
        }

        return null;
    }

    private static int MarkerScoreFor(string line)
    {
        var markers = 0;
        if (OutputBinding.IsMatch(line))
        {
            markers++;
        }

        if (VerbatimString.IsMatch(line))
        {
            markers++;
        }

        if (VariableReference.IsMatch(line))
        {
            markers++;
        }

        return markers * MarkerScore;
    }

    /// <summary>"Excel.ReadFromExcel.ReadAllCells" → "Excel.ReadFromExcel".</summary>
    private static string ShortActionName(string fullName)
    {
        var parts = fullName.Split('.');
        return parts.Length > 2 ? $"{parts[0]}.{parts[1]}" : fullName;
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);
}
