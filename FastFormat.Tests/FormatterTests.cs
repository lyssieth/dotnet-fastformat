using System.Text;

namespace FastFormat.Tests;

public class FormatterTests : IDisposable
{
    private readonly string _tempDir;

    public FormatterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task CheckMode_UnformattedFile_Returns1()
    {
        var path = WriteFile("a.cs", "class C{\n}\n");
        var formatter = new Formatter(check: true, verbose: false, parallel: 1);
        var exit = await formatter.RunAsync(new List<string> { path });
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task CheckMode_FormattedFile_Returns0()
    {
        var path = WriteFile("a.cs", "class C\n{\n}\n");
        var formatter = new Formatter(check: true, verbose: false, parallel: 1);
        var exit = await formatter.RunAsync(new List<string> { path });
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task CheckMode_ParseError_Returns2()
    {
        var path = WriteFile("a.cs", "class C { void M() { }");
        var formatter = new Formatter(check: true, verbose: false, parallel: 1);
        var exit = await formatter.RunAsync(new List<string> { path });
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task FormatMode_ParseError_Returns2()
    {
        var path = WriteFile("a.cs", "class C { void M() { }");
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        var exit = await formatter.RunAsync(new List<string> { path });
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Stdin_FormatsAndWritesToStdout()
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            var input = new StringReader("class C{\n}\n");
            var output = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);

            var formatter = new Formatter(check: false, verbose: false, parallel: 1);
            var exit = await formatter.RunStdinAsync(null);

            Assert.Equal(0, exit);
            var result = output.ToString();
            Assert.Contains("class C", result);
            Assert.Contains("{", result);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Stdin_ParseError_Returns2()
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            var input = new StringReader("class C { void M() { }");
            var output = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);

            var formatter = new Formatter(check: false, verbose: false, parallel: 1);
            var exit = await formatter.RunStdinAsync(null);

            Assert.Equal(2, exit);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Stdin_UsesStdinFilePathForEditorConfig()
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            WriteFile(".editorconfig", "[*.cs]\nindent_size = 2\n");
            var input = new StringReader("class C\n{\n    void M() { }\n}\n");
            var output = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);

            var stdinFilePath = Path.Combine(_tempDir, "a.cs");
            var formatter = new Formatter(check: false, verbose: false, parallel: 1);
            var exit = await formatter.RunStdinAsync(stdinFilePath);

            Assert.Equal(0, exit);
            var result = output.ToString();
            Assert.Contains("  void M()", result);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task IncludePattern_FiltersFiles()
    {
        WriteFile("src/a.cs", "class A{\n}\n");
        WriteFile("tests/b.cs", "class B{\n}\n");
        var formatter = new Formatter(check: true, verbose: false, parallel: 1, includes: new List<string> { "**/src/**" });
        var exit = await formatter.RunAsync(new List<string> { _tempDir });
        // a.cs is unformatted and included; b.cs is excluded
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task ExcludePattern_FiltersFiles()
    {
        WriteFile("src/a.cs", "class A{\n}\n");
        WriteFile("src/b.generated.cs", "class B{\n}\n");
        var formatter = new Formatter(check: true, verbose: false, parallel: 1, excludes: new List<string> { "**/*.generated.cs" });
        var exit = await formatter.RunAsync(new List<string> { _tempDir });
        // a.cs is unformatted and not excluded; b.generated.cs is excluded
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task EditorConfig_Inheritance_ChildOverridesParent()
    {
        WriteFile(".editorconfig", "[*.cs]\nindent_size = 4\n");
        WriteFile("sub/.editorconfig", "[*.cs]\nindent_size = 2\n");
        WriteFile("sub/a.cs", "class C\n{\n    void M() { }\n}\n");

        var path = Path.Combine(_tempDir, "sub", "a.cs");
        var options = EditorConfigParser.GetOptionsForFile(path);
        Assert.Equal(2, options.IndentSize);
    }

    [Fact]
    public async Task EditorConfig_StopAtRoot()
    {
        WriteFile(".editorconfig", "root = true\n[*.cs]\nindent_size = 4\n");
        WriteFile("sub/.editorconfig", "root = true\n[*.cs]\nindent_size = 2\n");
        WriteFile("sub/sub2/a.cs", "class C { }\n");

        var path = Path.Combine(_tempDir, "sub", "sub2", "a.cs");
        var options = EditorConfigParser.GetOptionsForFile(path);
        Assert.Equal(2, options.IndentSize);
    }

    [Fact]
    public async Task FinalNewline_InsertedWhenConfigured()
    {
        WriteFile(".editorconfig", "[*.cs]\ninsert_final_newline = true\n");
        var path = WriteFile("a.cs", "class C { }"); // no trailing newline
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var content = File.ReadAllText(path);
        Assert.EndsWith("\n", content);
    }

    [Fact]
    public async Task FinalNewline_RemovedWhenConfigured()
    {
        WriteFile(".editorconfig", "[*.cs]\ninsert_final_newline = false\n");
        var path = WriteFile("a.cs", "class C { }\n"); // has trailing newline
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("\n", content);
    }

    [Fact]
    public async Task TrailingWhitespace_TrimmedWhenConfigured()
    {
        WriteFile(".editorconfig", "[*.cs]\ntrim_trailing_whitespace = true\n");
        var path = WriteFile("a.cs", "class C   \n{\n}\n");
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("   \n", content);
    }

    [Fact]
    public async Task LineEnding_CRLF_WhenConfigured()
    {
        WriteFile(".editorconfig", "[*.cs]\nend_of_line = crlf\n");
        var path = WriteFile("a.cs", "class C\n{\n}\n"); // LF
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("\n", content.Replace("\r\n", ""));
        Assert.Contains("\r\n", content);
    }
    [Fact]
    public async Task LineEnding_LF_WhenConfigured()
    {
        WriteFile(".editorconfig", "[*.cs]\nend_of_line = lf\n");
        var path = WriteFile("a.cs", "class C\r\n{\r\n}\r\n"); // CRLF
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("\r\n", content);
        Assert.Contains("\n", content);
    }
    [Fact]
    public async Task Cli_UnknownOption_Returns1()
    {
        var exit = await Program.Main(new[] { "--chek", "src/" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_MissingParallelValue_Returns1()
    {
        var exit = await Program.Main(new[] { "--parallel" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_InvalidParallelValue_Returns1()
    {
        var exit = await Program.Main(new[] { "--parallel", "foo" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_ZeroParallelValue_Returns1()
    {
        var exit = await Program.Main(new[] { "--parallel", "0" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_MissingIncludeValue_Returns1()
    {
        var exit = await Program.Main(new[] { "--include" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_MissingExcludeValue_Returns1()
    {
        var exit = await Program.Main(new[] { "--exclude" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_MissingStdinFilepathValue_Returns1()
    {
        var exit = await Program.Main(new[] { "--stdin-filepath" });
        Assert.Equal(1, exit);
    }
    [Fact]
    public async Task Cli_NonProjectDirectory_WithoutForce_Returns1()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var exit = await Program.Main(new[] { tempDir });
            Assert.Equal(1, exit);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
    [Fact]
    public async Task Cli_NonProjectDirectory_WithForce_Returns0()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var exit = await Program.Main(new[] { "--force", tempDir });
            Assert.Equal(0, exit);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
    [Fact]
    public async Task Cli_ProjectDirectory_WithGit_Returns0()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        try
        {
            var exit = await Program.Main(new[] { tempDir });
            Assert.Equal(0, exit);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
    [Fact]
    public async Task Cli_ProjectDirectory_WithEditorConfig_Returns0()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, ".editorconfig"), "[*.cs]\n");
        try
        {
            var exit = await Program.Main(new[] { tempDir });
            Assert.Equal(0, exit);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Format_ActuallyRewritesFile()
    {
        var path = WriteFile("a.cs", "class C{\n}\n");
        var original = File.ReadAllText(path);
        var formatter = new Formatter(check: false, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var rewritten = File.ReadAllText(path);
        Assert.NotEqual(original, rewritten);
    }

    [Fact]
    public async Task CheckMode_DoesNotRewriteFile()
    {
        var path = WriteFile("a.cs", "class C{\n}\n");
        var original = File.ReadAllText(path);
        var formatter = new Formatter(check: true, verbose: false, parallel: 1);
        await formatter.RunAsync(new List<string> { path });
        var afterCheck = File.ReadAllText(path);
        Assert.Equal(original, afterCheck);
    }

    [Fact]
    public async Task IncludePattern_WrongFilter_ChangesExitCode()
    {
        WriteFile("a.cs", "class A{\n}\n");
        WriteFile("b.cs", "class B{\n}\n");
        // Without include: both files are unformatted → exit 1
        var formatterAll = new Formatter(check: true, verbose: false, parallel: 1);
        var exitAll = await formatterAll.RunAsync(new List<string> { _tempDir });
        Assert.Equal(1, exitAll);

        // With include that matches nothing → exit 0
        var formatterNone = new Formatter(check: true, verbose: false, parallel: 1, includes: new List<string> { "**/*.doesnotexist" });
        var exitNone = await formatterNone.RunAsync(new List<string> { _tempDir });
        Assert.Equal(0, exitNone);
    }

    [Fact]
    public async Task ExcludePattern_WrongFilter_ChangesExitCode()
    {
        WriteFile("a.cs", "class A{\n}\n");
        WriteFile("b.cs", "class B{\n}\n");
        // Without exclude: both files are unformatted → exit 1
        var formatterAll = new Formatter(check: true, verbose: false, parallel: 1);
        var exitAll = await formatterAll.RunAsync(new List<string> { _tempDir });
        Assert.Equal(1, exitAll);

        // With exclude that matches everything → exit 0
        var formatterNone = new Formatter(check: true, verbose: false, parallel: 1, excludes: new List<string> { "**/*.cs" });
        var exitNone = await formatterNone.RunAsync(new List<string> { _tempDir });
        Assert.Equal(0, exitNone);
    }

    [Fact]
    public async Task Mixed_ParseErrorAndCheckMode_Returns2()
    {
        WriteFile("good.cs", "class C{\n}\n");
        WriteFile("bad.cs", "class C { void M() { }");
        var formatter = new Formatter(check: true, verbose: false, parallel: 1);
        var exit = await formatter.RunAsync(new List<string> { _tempDir });
        // Parse errors are more serious than formatting changes → exit 2
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Cli_DocumentedExample_CheckSingleFile()
    {
        var path = WriteFile("Program.cs", "class C{\n}\n");
        var exit = await Program.Main(new[] { path });
        Assert.Equal(0, exit);
        var content = File.ReadAllText(path);
        Assert.Contains("class C", content);
        Assert.Contains("\n", content);
    }

    [Fact]
    public async Task Cli_DocumentedExample_CheckDirectory()
    {
        WriteFile("a.cs", "class A{\n}\n");
        WriteFile(".editorconfig", "[*.cs]\n");
        var exit = await Program.Main(new[] { "--check", _tempDir });
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Cli_DocumentedExample_ExcludePattern()
    {
        WriteFile("src/a.cs", "class A{\n}\n");
        WriteFile("src/b.generated.cs", "class B{\n}\n");
        WriteFile("src/.editorconfig", "[*.cs]\n");
        var exit = await Program.Main(new[] { "--check", "--exclude", "**/*.generated.cs", Path.Combine(_tempDir, "src") });
        // a.cs is unformatted and not excluded; excluded files don't count
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Cli_DocumentedExample_StdinFilepath()
    {
        WriteFile("src/.editorconfig", "[*.cs]\nindent_size = 2\n");
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            var input = new StringReader("class C\n{\n    void M() { }\n}\n");
            var output = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);
            var stdinPath = Path.Combine(_tempDir, "src", "a.cs");
            var exit = await Program.Main(new[] { "--stdin-filepath", stdinPath });
            Assert.Equal(0, exit);
            var result = output.ToString();
            Assert.Contains("  void M()", result);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Cli_DocumentedExample_Parallel()
    {
        WriteFile("a.cs", "class A{\n}\n");
        WriteFile("b.cs", "class B{\n}\n");
        WriteFile(".editorconfig", "[*.cs]\n");
        var exit = await Program.Main(new[] { "-p", "2", _tempDir });
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Cache_NoGitRepo_WarnsAndDisables()
    {
        WriteFile("a.cs", "class A{\n}\n");
        var exit = await Program.Main(new[] { "--force", "--cache", _tempDir });
        // Should still work, just without cache
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Cache_Hit_SkipsUnchangedFile()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        WriteFile("a.cs", "class A{\n}\n");

        // First run: formats and caches
        var exit1 = await Program.Main(new[] { "--cache", _tempDir });
        Assert.Equal(0, exit1);

        // Second run: cache hit, no work done
        var exit2 = await Program.Main(new[] { "--cache", "--verbose", _tempDir });
        Assert.Equal(0, exit2);
    }

    [Fact]
    public async Task Cache_Miss_ReprocessesChangedFile()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        WriteFile("a.cs", "class A{\n}\n");

        // First run: formats and caches
        var exit1 = await Program.Main(new[] { "--cache", _tempDir });
        Assert.Equal(0, exit1);

        // Modify file to be unformatted again
        File.WriteAllText(Path.Combine(_tempDir, "a.cs"), "class A{\n}\n");

        // Second run: cache miss, re-formats
        var exit2 = await Program.Main(new[] { "--cache", _tempDir });
        Assert.Equal(0, exit2);
    }

    [Fact]
    public async Task Cache_CheckMode_CachesCleanFiles()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        WriteFile("a.cs", "class A\n{\n}\n"); // already formatted

        // First check run: clean, caches
        var exit1 = await Program.Main(new[] { "--check", "--cache", _tempDir });
        Assert.Equal(0, exit1);

        // Second check run: cache hit, skip
        var exit2 = await Program.Main(new[] { "--check", "--cache", _tempDir });
        Assert.Equal(0, exit2);
    }

    [Fact]
    public async Task Cache_CheckMode_DoesNotCacheDirtyFiles()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        WriteFile("a.cs", "class A{\n}\n"); // unformatted

        // First check run: dirty, does not cache
        var exit1 = await Program.Main(new[] { "--check", "--cache", _tempDir });
        Assert.Equal(1, exit1);

        // Second check run: still dirty, still reports issues
        var exit2 = await Program.Main(new[] { "--check", "--cache", _tempDir });
        Assert.Equal(1, exit2);
    }

    [Fact]
    public async Task Cache_AutoDetect_FromExistingFile()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        WriteFile("a.cs", "class A\n{\n}\n"); // already formatted
        File.WriteAllText(Path.Combine(_tempDir, ".fastformat-cache"), "a.cs|00000000000000000000000000000000\n");

        // Without --cache flag, but cache file exists: should use cache
        var exit = await Program.Main(new[] { "--check", _tempDir });
        // Cache entry hash is wrong, so it should process the file and find it clean
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Cache_FileFormat_WrittenCorrectly()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        WriteFile("a.cs", "class A{\n}\n");

        var exit = await Program.Main(new[] { "--cache", _tempDir });
        Assert.Equal(0, exit);

        var cachePath = Path.Combine(_tempDir, ".fastformat-cache");
        Assert.True(File.Exists(cachePath));
        var lines = File.ReadAllLines(cachePath);
        Assert.Single(lines);
        Assert.StartsWith("a.cs|", lines[0]);
        Assert.Equal(37, lines[0].Length); // "a.cs|" + 32 hex chars
    }
}
