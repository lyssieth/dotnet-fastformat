namespace FastFormat;

internal static class GitIgnoreFilter
{
    public static async Task<List<string>> FilterIgnoredFilesAsync(List<string> files, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return files;

        // Group files by git root so multi-repo invocations work correctly
        var groups = new Dictionary<string, List<string>>();
        // Files not in any git repo pass through unchanged

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file);
            var gitRoot = FindGitRoot(dir);
            if (gitRoot == null)
            {
                // Not in a git repo; keep the file (pass through)
            }
            else
            {
                if (!groups.TryGetValue(gitRoot, out var group))
                {
                    group = new List<string>();
                    groups[gitRoot] = group;
                }
                group.Add(file);
            }
        }

        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 100;

        foreach (var (gitRoot, groupFiles) in groups)
        {
            try
            {
                for (int i = 0; i < groupFiles.Count; i += batchSize)
                {
                    var batch = groupFiles.Skip(i).Take(batchSize);
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "check-ignore --stdin -z",
                        WorkingDirectory = gitRoot,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };

                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process == null) continue;

                    using var reg = cancellationToken.Register(() =>
                    {
                        try { process.Kill(); } catch { /* process may already be exiting */ }
                    });

                    foreach (var file in batch)
                    {
                        var relative = Path.GetRelativePath(gitRoot, file);
                        await process.StandardInput.WriteAsync(relative.Replace("\\", "/").AsMemory(), cancellationToken);
                        await process.StandardInput.WriteAsync("\0".AsMemory(), cancellationToken);
                    }
                    process.StandardInput.Close();

                    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                    await process.WaitForExitAsync(cancellationToken);

                    if (process.ExitCode == 0 || process.ExitCode == 1)
                    {
                        if (!string.IsNullOrEmpty(output))
                        {
                            foreach (var line in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                            {
                                ignored.Add(Path.Combine(gitRoot, line.Replace('/', Path.DirectorySeparatorChar)));
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // If git fails for this repo, keep all files from this group
            }
        }

        return files.Where(f => !ignored.Contains(f)).ToList();
    }

    internal static string? FindGitRoot(string? startDir)
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
}
