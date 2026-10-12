using System.Text.Json;
using PaCodeCopy.Core.Logging;

namespace PaCodeCopy.Core.Configuration;

/// <summary>Loads and saves <see cref="AppSettings"/> as indented JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IAppLogger _logger;

    public SettingsStore(string path, IAppLogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Returns saved settings, or defaults if the file is missing or unreadable.</summary>
    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not read settings from '{_path}'. Using defaults.", ex);
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Write to a temp file first so a crash cannot leave a half-written settings.json.
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }
}
