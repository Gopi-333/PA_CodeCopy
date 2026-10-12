using PaCodeCopy.Core.Configuration;
using PaCodeCopy.Core.Detection;
using PaCodeCopy.Core.Logging;
using PaCodeCopy.Core.Services;
using PaCodeCopy.Core.Storage;
using PaCodeCopy.Forms;

namespace PaCodeCopy;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\PaCodeCopy.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Two instances would both auto-save every copy, so only one may run.
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("PA CodeCopy is already running. Look for its icon in the system tray.",
                "PA CodeCopy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        IAppLogger logger = new FileLogger(AppPaths.LogFolder, retentionDays: 14);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUnexpected(logger, e.Exception, canContinue: true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportUnexpected(logger, e.ExceptionObject as Exception, canContinue: false);

        try
        {
            var settingsStore = new SettingsStore(AppPaths.SettingsPath, logger);
            var settings = settingsStore.Load();
            if (settings.LogRetentionDays != 14)
            {
                logger = new FileLogger(AppPaths.LogFolder, settings.LogRetentionDays);
            }

            var repository = new SqliteSnippetRepository(AppPaths.DatabasePath);
            repository.Initialize();

            var service = new SnippetService(
                repository,
                new SnippetFileStore(settings.SnippetsRootFolder),
                new PadScriptDetector(settings.DetectionThreshold),
                logger)
            {
                DefaultCategory = settings.DefaultCategory,
            };

            logger.Info($"PA CodeCopy started. Index: '{AppPaths.DatabasePath}', snippets: '{service.FileStore.RootFolder}'.");
            Application.Run(new MainForm(service, settingsStore, settings, logger));
            logger.Info("PA CodeCopy stopped.");
        }
        catch (Exception ex)
        {
            ReportUnexpected(logger, ex, canContinue: false);
        }
    }

    private static void ReportUnexpected(IAppLogger logger, Exception? exception, bool canContinue)
    {
        logger.Error("Unexpected error.", exception);
        var message = $"An unexpected error occurred:\n{exception?.Message}\n\nDetails were written to:\n{AppPaths.LogFolder}";
        if (canContinue)
        {
            message += "\n\nThe application will keep running.";
        }

        MessageBox.Show(message, "PA CodeCopy", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
