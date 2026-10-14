namespace PaCodeCopy.Core.Extraction;

/// <summary>
/// An open flow in the Power Automate Desktop designer. The Windows implementation drives the real
/// designer through UI Automation; tests use a fake.
/// </summary>
public interface IPadDesigner
{
    /// <summary>Name of the open flow (used as the snippet category).</summary>
    string FlowName { get; }

    /// <summary>
    /// Names of all subflow tabs ("Main" first). Empty when the tabs cannot be found; the extractor then
    /// copies only the subflow that is currently shown.
    /// </summary>
    IReadOnlyList<string> GetSubflowNames();

    /// <summary>
    /// Shows the subflow (null = keep the current one), selects all its actions and copies them.
    /// Returns the copied PAD code, or null when nothing was copied (for example an empty subflow).
    /// </summary>
    string? CopySubflow(string? subflowName, CancellationToken cancellationToken);
}
