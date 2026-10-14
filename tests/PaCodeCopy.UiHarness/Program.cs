using System.Diagnostics;
using PaCodeCopy.Core.Detection;
using PaCodeCopy.Core.Extraction;
using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Services;
using PaCodeCopy.Core.Storage;
using PaCodeCopy.Interop;

namespace PaCodeCopy.UiHarness;

/// <summary>
/// End-to-end check of "Extract PAD flow" with real Windows UI Automation:
/// starts the fake PAD designer, extracts all subflows, and verifies what was saved.
/// Usage: PaCodeCopy.UiHarness &lt;path to PAD.Designer.exe of tools/FakePadDesigner&gt;. Exit code 0 = pass.
/// </summary>
internal static class Program
{
    private const string ExpectedLogin =
        "WebAutomation.LaunchChrome.LaunchChrome Url: $'''https://example.com''' BrowserInstance=> Browser\r\nWAIT 2\r\n";

    [STAThread] // clipboard access
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: PaCodeCopy.UiHarness <path to fake PAD.Designer.exe>");
            return 2;
        }

        var temp = Directory.CreateTempSubdirectory("pacodecopy-ui-");
        var fakePadPath = Path.GetFullPath(args[0]);
        using var fakePad = Process.Start(new ProcessStartInfo(fakePadPath) { UseShellExecute = false })!;
        try
        {
            var window = WaitForPadWindow(TimeSpan.FromSeconds(60));
            Console.WriteLine($"Found PAD window: '{window.Title}' (process {window.ProcessName}), flow '{window.FlowName}'.");

            var repository = new SqliteSnippetRepository(Path.Combine(temp.FullName, "index.db"));
            repository.Initialize();
            var logger = new ConsoleLogger();
            var service = new SnippetService(repository, new SnippetFileStore(Path.Combine(temp.FullName, "Snippets")),
                new PadScriptDetector(3), logger);

            var report = new FlowExtractor(service, logger).Extract(
                new UiaPadDesigner(window, logger), new Progress<string>(Console.WriteLine), CancellationToken.None);
            Console.WriteLine(report.Summary);
            Console.WriteLine(report.Details);

            var failures = new List<string>();
            Check(failures, !report.CurrentSubflowOnly, "subflow tabs were found");
            Check(failures, report.Subflows.Select(s => s.Subflow).SequenceEqual(new[] { "Main", "Login", "Empty" }),
                $"subflows are Main, Login, Empty (got {string.Join(", ", report.Subflows.Select(s => s.Subflow))})");
            Check(failures, report.Subflows.Count == 3 && report.Subflows[0].Outcome == SubflowOutcome.Saved, "Main saved");
            Check(failures, report.Subflows.Count == 3 && report.Subflows[1].Outcome == SubflowOutcome.Saved, "Login saved");
            Check(failures, report.Subflows.Count == 3 && report.Subflows[2].Outcome == SubflowOutcome.Empty, "Empty reported as empty");

            var snippets = repository.GetAll();
            var login = snippets.FirstOrDefault(s => s.Name == "Login");
            Check(failures, login is not null && File.ReadAllText(login.FilePath) == ExpectedLogin, "Login code copied exactly");
            Check(failures, snippets.All(s => s.Category == "Demo invoices"), "category is the flow name");
            Check(failures, snippets.FirstOrDefault(s => s.Name == "Main")?.ActionCount == 4, "Main has 4 detected actions");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("FAILED:\n  " + string.Join("\n  ", failures));
                Console.WriteLine("---- UI Automation tree of the fake PAD window ----");
                Console.WriteLine(File.ReadAllText(PadWindowFinder.ScanToFile(WaitForPadWindow(TimeSpan.FromSeconds(5)))));
                return 1;
            }

            Console.WriteLine("PASSED: all subflows extracted correctly.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED with exception: " + ex);
            return 1;
        }
        finally
        {
            try { fakePad.Kill(); } catch (InvalidOperationException) { }
            try { temp.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private static PadWindow WaitForPadWindow(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var window = PadWindowFinder.FindDesignerWindows().FirstOrDefault(w => w.Title.StartsWith("Demo invoices", StringComparison.Ordinal));
            if (window is not null)
            {
                Thread.Sleep(1000); // let it finish drawing
                return window;
            }

            Thread.Sleep(500);
        }

        throw new TimeoutException("The fake PAD designer window did not appear.");
    }

    private static void Check(List<string> failures, bool ok, string description)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {description}");
        if (!ok)
        {
            failures.Add(description);
        }
    }

    private sealed class ConsoleLogger : IAppLogger
    {
        public void Info(string message) => Console.WriteLine("[info] " + message);

        public void Warn(string message, Exception? exception = null) => Console.WriteLine($"[warn] {message} {exception?.Message}");

        public void Error(string message, Exception? exception = null) => Console.WriteLine($"[error] {message} {exception}");
    }
}
