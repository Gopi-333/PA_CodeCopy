using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Models;
using PaCodeCopy.Core.Services;

namespace PaCodeCopy.Core.Extraction;

/// <summary>
/// Copies every subflow of an open PAD flow and saves each one as a snippet
/// (category = flow name, name = subflow name).
/// </summary>
public sealed class FlowExtractor
{
    public const string CurrentSubflowName = "Current subflow";

    private readonly SnippetService _service;
    private readonly IAppLogger _logger;

    public FlowExtractor(SnippetService service, IAppLogger logger)
    {
        _service = service;
        _logger = logger;
    }

    public ExtractionReport Extract(IPadDesigner designer, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var flowName = string.IsNullOrWhiteSpace(designer.FlowName) ? "PAD flow" : designer.FlowName.Trim();
        var names = designer.GetSubflowNames();
        var currentOnly = names.Count == 0;
        IReadOnlyList<string?> targets = currentOnly ? new string?[] { null } : names.Cast<string?>().ToList();

        _logger.Info($"Extracting flow '{flowName}': {(currentOnly ? "current subflow only" : $"{names.Count} subflows")}.");
        var results = new List<SubflowResult>();
        var cancelled = false;

        for (var i = 0; i < targets.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var subflow = targets[i];
            var label = subflow ?? CurrentSubflowName;
            progress?.Report($"Copying '{label}' ({i + 1}/{targets.Count})…");

            try
            {
                var code = designer.CopySubflow(subflow, cancellationToken);
                results.Add(SaveSubflow(flowName, label, code));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // One broken subflow must not stop the others.
                _logger.Error($"Extracting subflow '{label}' of flow '{flowName}' failed.", ex);
                results.Add(new SubflowResult(label, SubflowOutcome.Failed, ex.Message));
            }
        }

        var report = new ExtractionReport(flowName, results, cancelled, currentOnly);
        _logger.Info(report.Summary);
        return report;
    }

    private SubflowResult SaveSubflow(string flowName, string subflow, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new SubflowResult(subflow, SubflowOutcome.Empty, "nothing copied (empty subflow?)");
        }

        var result = _service.Save(code, SnippetSource.Manual, requirePadScript: false, name: subflow, category: flowName);
        return result.Outcome switch
        {
            SaveOutcome.Saved => new SubflowResult(subflow, SubflowOutcome.Saved,
                $"saved ({result.Snippet!.ActionCount} actions)", result.Snippet.Id),
            SaveOutcome.Duplicate => new SubflowResult(subflow, SubflowOutcome.Unchanged,
                $"already saved as '{result.Snippet!.Name}'", result.Snippet.Id),
            SaveOutcome.Empty => new SubflowResult(subflow, SubflowOutcome.Empty, "nothing copied (empty subflow?)"),
            _ => new SubflowResult(subflow, SubflowOutcome.Failed, result.Message),
        };
    }
}
