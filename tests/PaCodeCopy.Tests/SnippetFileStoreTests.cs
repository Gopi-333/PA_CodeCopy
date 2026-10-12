using PaCodeCopy.Core.Storage;

namespace PaCodeCopy.Tests;

public sealed class SnippetFileStoreTests : IDisposable
{
    private static readonly DateTime Timestamp = new(2026, 10, 12, 14, 30, 5);

    private readonly TempFolder _temp = new();
    private readonly SnippetFileStore _store;

    public SnippetFileStoreTests()
    {
        _store = new SnippetFileStore(_temp.Combine("Snippets"));
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("Read Excel", "Read Excel")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("  many    spaces\there ", "many spaces here")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("...", "Snippet")]
    [InlineData("", "Snippet")]
    [InlineData(null, "Snippet")]
    [InlineData("///", "Snippet")]
    public void SanitizeFileName_makes_names_safe(string? input, string expected)
    {
        Assert.Equal(expected, SnippetFileStore.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_truncates_long_names()
    {
        Assert.Equal(60, SnippetFileStore.SanitizeFileName(new string('x', 200)).Length);
    }

    [Fact]
    public void Write_cannot_escape_the_root_folder()
    {
        var path = _store.Write("../../outside", "..\\..\\evil", Timestamp, "x");

        Assert.StartsWith(_store.RootFolder + Path.DirectorySeparatorChar, Path.GetFullPath(path));
    }

    [Fact]
    public void Write_creates_category_folder_and_round_trips_content()
    {
        const string code = "SET a TO 1\r\nSET b TO 'ü'";

        var path = _store.Write("Excel", "Read file", Timestamp, code);

        Assert.Equal(Path.Combine(_store.RootFolder, "Excel", "20261012_143005_Read file.txt"), path);
        Assert.Equal(code, _store.Read(path));
        Assert.Equal((byte)'S', File.ReadAllBytes(path)[0]); // no UTF-8 BOM
    }

    [Fact]
    public void Write_never_overwrites_existing_files()
    {
        var first = _store.Write("General", "Same", Timestamp, "one");
        var second = _store.Write("General", "Same", Timestamp, "two");

        Assert.NotEqual(first, second);
        Assert.Equal("one", _store.Read(first));
        Assert.Equal("two", _store.Read(second));
    }

    [Fact]
    public void ParsePath_reads_back_category_name_and_timestamp()
    {
        var path = _store.Write("Web", "Login flow", Timestamp, "x");

        var (category, name, timestamp) = _store.ParsePath(path, "General");

        Assert.Equal("Web", category);
        Assert.Equal("Login flow", name);
        Assert.Equal(Timestamp, timestamp);
    }

    [Fact]
    public void ParsePath_uses_default_category_for_files_in_root()
    {
        Directory.CreateDirectory(_store.RootFolder);
        var path = Path.Combine(_store.RootFolder, "loose.txt");
        File.WriteAllText(path, "x");

        var (category, name, timestamp) = _store.ParsePath(path, "General");

        Assert.Equal("General", category);
        Assert.Equal("loose", name);
        Assert.Null(timestamp);
    }

    [Fact]
    public void Move_renames_and_removes_empty_old_folder()
    {
        var path = _store.Write("Old", "Name", Timestamp, "x");

        var moved = _store.Move(path, "New", "Renamed", Timestamp);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(moved));
        Assert.False(Directory.Exists(Path.Combine(_store.RootFolder, "Old")));
        Assert.Equal("New", Path.GetFileName(Path.GetDirectoryName(moved)));
    }

    [Fact]
    public void Delete_ignores_missing_files()
    {
        _store.Delete(Path.Combine(_store.RootFolder, "General", "missing.txt"));
    }
}
