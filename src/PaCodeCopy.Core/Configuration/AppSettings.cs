namespace PaCodeCopy.Core.Configuration;

/// <summary>User settings, stored as JSON in <see cref="AppPaths.SettingsPath"/>.</summary>
public sealed class AppSettings
{
    public const int MinDetectionThreshold = 1;
    public const int MaxDetectionThreshold = 20;

    /// <summary>Folder where snippet .txt files are written (one sub-folder per category).</summary>
    public string SnippetsRootFolder { get; set; } = AppPaths.DefaultSnippetsRoot;

    /// <summary>Save PAD code automatically whenever it is copied.</summary>
    public bool AutoCapture { get; set; } = true;

    /// <summary>Minimum detector score for clipboard text to count as PAD code. Higher = stricter.</summary>
    public int DetectionThreshold { get; set; } = 3;

    public string DefaultCategory { get; set; } = "General";

    /// <summary>Hide the window in the system tray when it is minimised.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Show a tray balloon when a snippet is auto-saved.</summary>
    public bool ShowNotifications { get; set; } = true;

    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Fixes out-of-range values (e.g. after manual edits of settings.json).</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(SnippetsRootFolder))
        {
            SnippetsRootFolder = AppPaths.DefaultSnippetsRoot;
        }

        if (string.IsNullOrWhiteSpace(DefaultCategory))
        {
            DefaultCategory = "General";
        }

        DetectionThreshold = Math.Clamp(DetectionThreshold, MinDetectionThreshold, MaxDetectionThreshold);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
    }
}
