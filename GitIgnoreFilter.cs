namespace FastFormat;

internal static class GitIgnoreFilter
{
    public static async Task<List<string>> FilterIgnoredFilesAsync(List<string> files, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return files;

        // Try to use git check-ignore for accurate filtering
        try
        {
            var gitRoot = FindGitRoot(Path.GetDirectoryName(files[0]));
            if (gitRoot == null)
                return files;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const int batchSize = 100;

            for (int i = 0; i < files.Count; i += batchSize)
            {
                var batch = files.Skip(i).Take(batchSize);
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

            return files.Where(f => !ignored.Contains(f)).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall back to returning all files if git is not available
            return files;
        }
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
