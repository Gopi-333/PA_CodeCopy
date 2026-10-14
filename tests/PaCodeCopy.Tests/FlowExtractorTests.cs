using PaCodeCopy.Core.Detection;
using PaCodeCopy.Core.Extraction;
using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Services;
using PaCodeCopy.Core.Storage;

namespace PaCodeCopy.Tests;

public sealed class FlowExtractorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SqliteSnippetRepository _repository;
    private readonly SnippetService _service;
    private readonly FlowExtractor _extractor;

    public FlowExtractorTests()
    {
        _repository = new SqliteSnippetRepository(_temp.Combine("index.db"));
        _repository.Initialize();
        _service = new SnippetService(_repository, new SnippetFileStore(_temp.Combine("Snippets")),
            new PadScriptDetector(3), NullLogger.Instance);
        _extractor = new FlowExtractor(_service, NullLogger.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Each_subflow_becomes_a_snippet_named_after_it_in_the_flow_category()
    {
        var designer = new FakeDesigner("Invoices", new()
        {
            ["Main"] = Samples.ExcelLoop,
            ["Login"] = Samples.IfElse,
            ["Notify"] = Samples.SingleMessageBox,
        });

        var report = _extractor.Extract(designer, null, CancellationToken.None);

        Assert.Equal(3, report.Count(SubflowOutcome.Saved));
        Assert.False(report.CurrentSubflowOnly);
        var snippets = _repository.GetAll();
        Assert.Equal(new[] { "Login", "Main", "Notify" }, snippets.Select(s => s.Name).OrderBy(n => n));
        Assert.All(snippets, s => Assert.Equal("Invoices", s.Category));
        Assert.Equal(new[] { "Main", "Login", "Notify" }, designer.Copied);
    }

    [Fact]
    public void Empty_subflow_is_reported_and_skipped()
    {
        var designer = new FakeDesigner("Flow", new() { ["Main"] = Samples.ExcelLoop, ["Empty"] = null });

        var report = _extractor.Extract(designer, null, CancellationToken.None);

        Assert.Equal(SubflowOutcome.Empty, report.Subflows.Single(s => s.Subflow == "Empty").Outcome);
        Assert.Single(_repository.GetAll());
    }

    [Fact]
    public void Extracting_the_same_flow_twice_reports_unchanged()
    {
        var designer = new FakeDesigner("Flow", new() { ["Main"] = Samples.ExcelLoop, ["Sub"] = Samples.IfElse });
        _extractor.Extract(designer, null, CancellationToken.None);

        var second = _extractor.Extract(designer, null, CancellationToken.None);

        Assert.Equal(2, second.Count(SubflowOutcome.Unchanged));
        Assert.Equal(2, _repository.GetAll().Count);
    }

    [Fact]
    public void A_failing_subflow_does_not_stop_the_others()
    {
        var designer = new FakeDesigner("Flow", new() { ["Main"] = Samples.ExcelLoop, ["Broken"] = "THROW", ["Last"] = Samples.IfElse });

        var report = _extractor.Extract(designer, null, CancellationToken.None);

        Assert.Equal(SubflowOutcome.Failed, report.Subflows[1].Outcome);
        Assert.Equal(2, report.Count(SubflowOutcome.Saved));
    }

    [Fact]
    public void Without_subflow_tabs_only_the_current_subflow_is_copied()
    {
        var designer = new FakeDesigner("Flow", new()) { CurrentCode = Samples.ExcelLoop };

        var report = _extractor.Extract(designer, null, CancellationToken.None);

        Assert.True(report.CurrentSubflowOnly);
        Assert.Equal(FlowExtractor.CurrentSubflowName, Assert.Single(report.Subflows).Subflow);
        Assert.Equal(FlowExtractor.CurrentSubflowName, Assert.Single(_repository.GetAll()).Name);
        Assert.Contains("only the open subflow", report.Summary);
    }

    [Fact]
    public void Cancellation_stops_before_the_next_subflow()
    {
        using var cts = new CancellationTokenSource();
        var designer = new FakeDesigner("Flow", new() { ["Main"] = Samples.ExcelLoop, ["Sub"] = Samples.IfElse })
        {
            AfterCopy = cts.Cancel,
        };

        var report = _extractor.Extract(designer, null, cts.Token);

        Assert.True(report.Cancelled);
        Assert.Single(report.Subflows);
        Assert.EndsWith("cancelled", report.Summary);
    }

    [Fact]
    public void Progress_is_reported_per_subflow()
    {
        var messages = new List<string>();
        var designer = new FakeDesigner("Flow", new() { ["Main"] = Samples.ExcelLoop, ["Sub"] = Samples.IfElse });

        _extractor.Extract(designer, new SyncProgress(messages.Add), CancellationToken.None);

        Assert.Equal(new[] { "Copying 'Main' (1/2)…", "Copying 'Sub' (2/2)…" }, messages);
    }

    [Theory]
    [InlineData("Invoice processing | Power Automate", "Invoice processing")]
    [InlineData("*Invoice processing | Power Automate", "Invoice processing")]
    [InlineData("Daily report - Power Automate", "Daily report")]
    [InlineData("JustAName", "JustAName")]
    [InlineData("", "PAD flow")]
    [InlineData(null, "PAD flow")]
    public void Flow_name_is_read_from_the_window_title(string? title, string expected)
    {
        Assert.Equal(expected, PadWindowTitle.ParseFlowName(title));
    }

    [Fact]
    public void Pad_titles_are_recognised()
    {
        Assert.True(PadWindowTitle.LooksLikePad("Flow | Power Automate"));
        Assert.False(PadWindowTitle.LooksLikePad("Untitled - Notepad"));
    }

    private sealed class FakeDesigner : IPadDesigner
    {
        private readonly Dictionary<string, string?> _subflows;

        public FakeDesigner(string flowName, Dictionary<string, string?> subflows)
        {
            FlowName = flowName;
            _subflows = subflows;
        }

        public string FlowName { get; }

        public string? CurrentCode { get; init; }

        public Action? AfterCopy { get; init; }

        public List<string> Copied { get; } = new();

        public IReadOnlyList<string> GetSubflowNames() => _subflows.Keys.ToList();

        public string? CopySubflow(string? subflowName, CancellationToken cancellationToken)
        {
            if (subflowName is null)
            {
                return CurrentCode;
            }

            Copied.Add(subflowName);
            var code = _subflows[subflowName];
            AfterCopy?.Invoke();
            return code == "THROW" ? throw new InvalidOperationException("PAD did not respond") : code;
        }
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need the calls in order.</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _report;

        public SyncProgress(Action<string> report) => _report = report;

        public void Report(string value) => _report(value);
    }
}
