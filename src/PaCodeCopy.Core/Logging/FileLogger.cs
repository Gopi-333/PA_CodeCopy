using System.Globalization;
using System.Text;

namespace PaCodeCopy.Core.Logging;

/// <summary>
/// Thread-safe logger that writes one file per day (pacodecopy-yyyyMMdd.log) and deletes files
/// older than the retention period. Logging must never crash the app, so IO errors are swallowed.
/// </summary>
public sealed class FileLogger : IAppLogger
{
    private const string FilePrefix = "pacodecopy-";

    private readonly string _folder;
    private readonly object _sync = new();

    public FileLogger(string folder, int retentionDays)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            DeleteOldFiles(retentionDays);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; the app keeps working without it.
        }
    }

    public void Info(string message) => Write("INFO ", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN ", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();
        var path = Path.Combine(_folder, $"{FilePrefix}{now:yyyyMMdd}.log");

        lock (_sync)
        {
            try
            {
                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore: never let logging failures break a save.
            }
        }
    }

    private void DeleteOldFiles(int retentionDays)
    {
        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(_folder, FilePrefix + "*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }
}
