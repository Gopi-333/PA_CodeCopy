namespace PaCodeCopy.Core.Logging;

public interface IAppLogger
{
    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

/// <summary>Logger that discards everything (used in tests and as a safe fallback).</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    public void Info(string message) { }

    public void Warn(string message, Exception? exception = null) { }

    public void Error(string message, Exception? exception = null) { }
}
