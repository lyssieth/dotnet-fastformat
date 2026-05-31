using System.Text.RegularExpressions;

namespace FastFormat;

class EditorConfigOptions
{
    public bool? UseTabs { get; set; }
    public int? IndentSize { get; set; }
    public int? TabWidth { get; set; }
    public string? NewLine { get; set; }
    public bool? InsertFinalNewline { get; set; }
    public string? NewLineBeforeOpenBrace { get; set; }
}

class EditorConfigParser
{
    public static EditorConfigOptions GetOptionsForFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var dir = Path.GetDirectoryName(filePath);
        var gitRoot = FindGitRoot(dir);
        var configs = new List<(string Directory, EditorConfigFile Config)>();

        // Walk up the directory tree collecting .editorconfig files, stopping at git root
        while (!string.IsNullOrEmpty(dir))
        {
            var editorConfigPath = Path.Combine(dir, ".editorconfig");
            if (File.Exists(editorConfigPath))
            {
                var config = ParseFile(editorConfigPath);
                configs.Add((dir, config));
                if (config.IsRoot)
                    break;
            }
            if (dir.Equals(gitRoot, StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        // Reverse so closest config applies last (overrides earlier ones)
        configs.Reverse();

        var result = new EditorConfigOptions();
        foreach (var (configDir, config) in configs)
        {
            var relativePath = filePath.Substring(configDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var section in config.Sections)
            {
                if (MatchesGlob(relativePath, fileName, section.Glob))
                {
                    ApplySection(result, section);
                }
            }
        }

        return result;
    }

    private static string? FindGitRoot(string? startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static void ApplySection(EditorConfigOptions result, EditorConfigSection section)
    {
        foreach (var kvp in section.Options)
        {
            switch (kvp.Key.ToLowerInvariant())
            {
                case "indent_style":
                    result.UseTabs = kvp.Value.Equals("tab", StringComparison.OrdinalIgnoreCase);
                    break;
                case "indent_size":
                    if (int.TryParse(kvp.Value, out var indentSize))
                        result.IndentSize = indentSize;
                    break;
                case "tab_width":
                    if (int.TryParse(kvp.Value, out var tabWidth))
                        result.TabWidth = tabWidth;
                    break;
                case "end_of_line":
                    result.NewLine = kvp.Value.ToLowerInvariant() switch
                    {
                        "lf" => "\n",
                        "crlf" => "\r\n",
                        "cr" => "\r",
                        _ => result.NewLine
                    };
                    break;
                case "insert_final_newline":
                    result.InsertFinalNewline = kvpValueToBool(kvp.Value);
                    break;
                case "csharp_new_line_before_open_brace":
                    result.NewLineBeforeOpenBrace = kvp.Value;
                    break;
            }
        }
    }

    private static bool? kvpValueToBool(string value)
    {
        if (bool.TryParse(value, out var b))
            return b;
        return null;
    }

    private static EditorConfigFile ParseFile(string path)
    {
        var file = new EditorConfigFile();
        var lines = File.ReadAllLines(path);
        EditorConfigSection? currentSection = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = new EditorConfigSection { Glob = line[1..^1] };
                file.Sections.Add(currentSection);
            }
            else if (line.Contains('='))
            {
                var eq = line.IndexOf('=');
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();

                if (key.Equals("root", StringComparison.OrdinalIgnoreCase))
                {
                    file.IsRoot = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else if (currentSection != null)
                {
                    currentSection.Options[key] = value;
                }
            }
        }

        return file;
    }

    private static bool MatchesGlob(string relativePath, string fileName, string glob)
    {
        // Very simple glob matching - supports *, **, ?
        // For our purposes, this is sufficient for common patterns like [*], [*.cs], [src/**.cs]
        if (glob == "*")
            return true;

        // Convert glob to regex
        var pattern = glob
            .Replace(".**/", "###DOUBLESTAR###")
            .Replace("**", "###DOUBLESTAR###")
            .Replace("*", "###STAR###")
            .Replace("?", "###QUESTION###")
            .Replace(".", "\\.")
            .Replace("###DOUBLESTAR###", ".*")
            .Replace("###STAR###", "[^/]*")
            .Replace("###QUESTION###", ".");

        pattern = "^" + pattern + "$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        return regex.IsMatch(relativePath) || regex.IsMatch(fileName);
    }
}

class EditorConfigFile
{
    public bool IsRoot { get; set; }
    public List<EditorConfigSection> Sections { get; } = new();
}

class EditorConfigSection
{
    public string Glob { get; set; } = "*";
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
}
