using System.Text;

namespace FastFormat.Tests;

public sealed class LspFormatterServiceTests : IDisposable
{
    private readonly string _tempDir;

    public LspFormatterServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Lsp.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task FormatDocumentAsync_UnformattedText_ReturnsFullDocumentEdit()
    {
        var path = WriteFile("a.cs", "class C{\n}\n");
        var service = new LspFormatterService();

        var edits = await service.FormatDocumentAsync(PathToUri(path), "class C{\n}\n", CancellationToken.None);

        var edit = Assert.Single(edits);
        Assert.Equal(new LspPosition(0, 0), edit.Range.Start);
        Assert.Equal(new LspPosition(2, 0), edit.Range.End);
        Assert.Equal("class C\n{\n}\n", edit.NewText);
    }

    [Fact]
    public async Task FormatDocumentAsync_AlreadyFormattedText_ReturnsNoEditsAndCachesFormattedHash()
    {
        var path = WriteFile("a.cs", "class C\n{\n}\n");
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        using (var service = new LspFormatterService())
        {
            var first = await service.FormatDocumentAsync(PathToUri(path), "class C\n{\n}\n", CancellationToken.None);
            var second = await service.FormatDocumentAsync(PathToUri(path), "class C\n{\n}\n", CancellationToken.None);

            Assert.Empty(first);
            Assert.Empty(second);
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, ".fastformat-cache")));
    }

    [Fact]
    public async Task FormatDocumentAsync_ParseError_ReturnsNoEdits()
    {
        var path = WriteFile("a.cs", "class C { void M() { }");
        var service = new LspFormatterService();

        var edits = await service.FormatDocumentAsync(PathToUri(path), "class C { void M() { }", CancellationToken.None);

        Assert.Empty(edits);
    }

    [Fact]
    public async Task FormatRangeAsync_UnformattedMethodBody_ReturnsMinimalEdit()
    {
        var path = WriteFile("a.cs", "class C\n{\nvoid M(){\n}\n}\n");
        var service = new LspFormatterService();

        var edits = await service.FormatRangeAsync(
            PathToUri(path),
            "class C\n{\nvoid M(){\n}\n}\n",
            new LspRange(new LspPosition(2, 0), new LspPosition(4, 0)),
            CancellationToken.None);

        var edit = Assert.Single(edits);
        Assert.Contains("    void M()", edit.NewText);
    }

    private static string PathToUri(string path) => new Uri(path).AbsoluteUri;
}
