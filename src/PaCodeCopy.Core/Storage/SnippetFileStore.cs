using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaCodeCopy.Core.Storage;

/// <summary>
/// Stores each snippet as a readable .txt file: &lt;root&gt;\&lt;Category&gt;\yyyyMMdd_HHmmss_&lt;Name&gt;.txt
/// </summary>
public sealed class SnippetFileStore
{
    public const string FileExtension = ".txt";
    public const string TimestampFormat = "yyyyMMdd_HHmmss";

    private const int MaxNameLength = 60;

    // Windows rules are applied on every OS so files stay portable to Windows.
    private static readonly char[] InvalidFileNameChars = "<>:\"/\\|?*".ToCharArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Regex TimestampPrefix = new(@"^(?<ts>\d{8}_\d{6})_(?<name>.+)$", RegexOptions.Compiled);

    // UTF-8 without BOM: PAD pastes plain text, and a BOM would end up in the clipboard.
    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public SnippetFileStore(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            throw new ArgumentException("Snippets folder must not be empty.", nameof(rootFolder));
        }

        RootFolder = Path.GetFullPath(rootFolder);
    }

    public string RootFolder { get; }

    /// <summary>Writes a new file and returns its full path. Never overwrites an existing file.</summary>
    public string Write(string category, string name, DateTime timestamp, string content)
    {
        var folder = Path.Combine(RootFolder, SanitizeFileName(category, MaxNameLength));
        Directory.CreateDirectory(folder);

        var path = UniquePath(folder, BuildFileName(name, timestamp));

        // FileMode.CreateNew fails instead of silently replacing a file created in the meantime.
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, FileEncoding))
        {
            writer.Write(content);
        }

        return path;
    }

    public string Read(string path) => File.ReadAllText(path, FileEncoding);

    /// <summary>Deletes a snippet file. A missing file is not an error. Empty category folders are removed.</summary>
    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        TryRemoveEmptyFolder(Path.GetDirectoryName(path));
    }

    /// <summary>Renames/moves a snippet file to match a new name or category. Returns the new path.</summary>
    public string Move(string currentPath, string category, string name, DateTime timestamp)
    {
        var folder = Path.Combine(RootFolder, SanitizeFileName(category, MaxNameLength));
        var wantedPath = Path.Combine(folder, BuildFileName(name, timestamp));

        if (string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(wantedPath), StringComparison.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        Directory.CreateDirectory(folder);
        var newPath = UniquePath(folder, BuildFileName(name, timestamp));
        File.Move(currentPath, newPath);
        TryRemoveEmptyFolder(Path.GetDirectoryName(currentPath));
        return newPath;
    }

    public IEnumerable<string> EnumerateSnippetFiles() =>
        Directory.Exists(RootFolder)
            ? Directory.EnumerateFiles(RootFolder, "*" + FileExtension, SearchOption.AllDirectories)
            : Enumerable.Empty<string>();

    /// <summary>
    /// Reads category, name and timestamp back from a file path written by <see cref="Write"/>.
    /// Used when rebuilding the index from disk.
    /// </summary>
    public (string Category, string Name, DateTime? Timestamp) ParsePath(string path, string defaultCategory)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? RootFolder;
        var relativeFolder = Path.GetRelativePath(RootFolder, folder);
        var category = relativeFolder is "." or "" ? defaultCategory : relativeFolder.Split(Path.DirectorySeparatorChar)[0];

        var fileName = Path.GetFileNameWithoutExtension(path);
        var match = TimestampPrefix.Match(fileName);
        if (match.Success &&
            DateTime.TryParseExact(match.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var timestamp))
        {
            return (category, match.Groups["name"].Value, timestamp);
        }

        return (category, fileName, null);
    }

    /// <summary>Makes text safe to use as a Windows file or folder name.</summary>
    public static string SanitizeFileName(string? value, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Snippet";
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            // Whitespace (including tabs/new lines) collapses to one space; other control chars are invalid.
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            builder.Append(char.IsControl(c) || Array.IndexOf(InvalidFileNameChars, c) >= 0 ? '_' : c);
        }

        var result = builder.ToString().Trim().Trim('.').Trim();
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd(' ', '.');
        }

        if (result.Length == 0 || result.All(c => c == '_'))
        {
            return "Snippet";
        }

        var stem = result.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + result : result;
    }

    private static string BuildFileName(string name, DateTime timestamp) =>
        $"{timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture)}_{SanitizeFileName(name)}{FileExtension}";

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{stem}_{i}{FileExtension}");
        }

        return path;
    }

    private void TryRemoveEmptyFolder(string? folder)
    {
        try
        {
            if (folder is not null &&
                !string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                    RootFolder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(folder) &&
                !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cosmetic clean-up only.
        }
    }
}
