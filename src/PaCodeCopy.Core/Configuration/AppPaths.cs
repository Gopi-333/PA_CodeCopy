namespace PaCodeCopy.Core.Configuration;

/// <summary>Well-known locations used by the application.</summary>
public static class AppPaths
{
    private const string AppFolderName = "PaCodeCopy";

    /// <summary>%LOCALAPPDATA%\PaCodeCopy — index database, settings and logs.</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string DatabasePath => Path.Combine(DataFolder, "index.db");

    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public static string LogFolder => Path.Combine(DataFolder, "logs");

    /// <summary>Documents\PaCodeCopy\Snippets — default home for snippet files.</summary>
    public static string DefaultSnippetsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        AppFolderName,
        "Snippets");
}
