namespace PaCodeCopy.Core.Extraction;

/// <summary>Where a copy came from: the PAD flow (project) and, when known, the subflow that was open.</summary>
/// <param name="FlowName">Flow name from the designer window title; used as the snippet folder (category).</param>
/// <param name="SubflowName">Open subflow tab (e.g. "Main"), or null when it could not be read.</param>
public sealed record PadCopySource(string FlowName, string? SubflowName);
