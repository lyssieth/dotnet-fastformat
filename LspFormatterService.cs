using System.Collections.Concurrent;

namespace FastFormat;

internal sealed class LspFormatterService : IDisposable
{
    private const int MaxFormattedCacheEntries = 256;

    private readonly Formatter _formatter;
    private readonly ConcurrentDictionary<string, FormatCache> _diskCaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _formattedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _formattedCacheOrder = new();
    private readonly object _formattedCacheLock = new();

    public LspFormatterService()
    {
        _formatter = new Formatter(check: false, verbose: false, parallel: 1);
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormatDocumentAsync(string uri, string text, CancellationToken cancellationToken)
    {
        var path = TryGetLocalPath(uri);
        var inputHash = CacheHelper.ComputeHash(text);
        var cachePath = path ?? uri;
        var cacheKey = CreateFormattedCacheKey(cachePath, inputHash);

        string? relativePath = null;
        var diskCache = path != null ? TryGetDiskCache(path, out relativePath) : null;
        if (diskCache != null && relativePath != null && diskCache.TryGet(relativePath, inputHash))
            return [];

        if (TryGetFormattedText(cacheKey, out var cachedFormatted))
            return CreateFullDocumentEditIfChanged(text, cachedFormatted);

        string formatted;
        try
        {
            formatted = await _formatter.FormatTextAsync(text, path).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        if (text == formatted)
        {
            diskCache?.Set(relativePath!, inputHash);
            return [];
        }

        SetFormattedText(cacheKey, formatted);
        return CreateFullDocumentEditIfChanged(text, formatted);
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormatRangeAsync(string uri, string text, LspRange range, CancellationToken cancellationToken)
    {
        var path = TryGetLocalPath(uri);
        var start = PositionToOffset(text, range.Start);
        var end = PositionToOffset(text, range.End);
        if (end < start)
            return [];
        string formatted;
        try
        {
            var span = new Microsoft.CodeAnalysis.Text.TextSpan(start, end - start);
            formatted = await _formatter.FormatRangeAsync(text, path, span).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        return CreateMinimalEditIfChanged(text, formatted);
    }

    public void Dispose()
    {
        foreach (var cache in _diskCaches.Values)
            cache.Flush();
    }

    private static IReadOnlyList<LspTextEdit> CreateFullDocumentEditIfChanged(string original, string formatted)
    {
        if (original == formatted)
            return [];

        return [new LspTextEdit(new LspRange(new LspPosition(0, 0), PositionAtEnd(original)), formatted)];
    }

    private static IReadOnlyList<LspTextEdit> CreateMinimalEditIfChanged(string original, string formatted)
    {
        if (original == formatted)
            return [];

        var prefix = 0;
        var maxPrefix = Math.Min(original.Length, formatted.Length);
        while (prefix < maxPrefix && original[prefix] == formatted[prefix])
            prefix++;

        var originalSuffix = original.Length;
        var formattedSuffix = formatted.Length;
        while (originalSuffix > prefix && formattedSuffix > prefix && original[originalSuffix - 1] == formatted[formattedSuffix - 1])
        {
            originalSuffix--;
            formattedSuffix--;
        }

        var range = new LspRange(PositionAt(original, prefix), PositionAt(original, originalSuffix));
        return [new LspTextEdit(range, formatted[prefix..formattedSuffix])];
    }

    private FormatCache? TryGetDiskCache(string path, out string? relativePath)
    {
        relativePath = null;
        var dir = Path.GetDirectoryName(path);
        var gitRoot = GitIgnoreFilter.FindGitRoot(dir);
        if (gitRoot == null)
            return null;

        relativePath = CacheHelper.GetRelativePath(path, gitRoot);
        return _diskCaches.GetOrAdd(gitRoot, static root => new FormatCache(root));
    }

    private bool TryGetFormattedText(string key, out string formatted)
    {
        lock (_formattedCacheLock)
            return _formattedCache.TryGetValue(key, out formatted!);
    }

    private void SetFormattedText(string key, string formatted)
    {
        lock (_formattedCacheLock)
        {
            if (_formattedCache.ContainsKey(key))
                return;

            if (_formattedCache.Count >= MaxFormattedCacheEntries && _formattedCacheOrder.TryDequeue(out var oldest))
                _formattedCache.Remove(oldest);

            _formattedCache[key] = formatted;
            _formattedCacheOrder.Enqueue(key);
        }
    }

    private static string CreateFormattedCacheKey(string pathOrUri, ReadOnlySpan<byte> hash)
    {
        return string.Concat(pathOrUri, "|", Convert.ToHexString(hash));
    }


    private static string? TryGetLocalPath(string uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath
            : null;
    }

    private static int PositionToOffset(string text, LspPosition position)
    {
        var line = 0;
        var character = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (line == position.Line && character == position.Character)
                return i;

            if (text[i] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return text.Length;
    }

    private static LspPosition PositionAtEnd(string text)
    {
        return PositionAt(text, text.Length);
    }

    private static LspPosition PositionAt(string text, int offset)
    {
        var line = 0;
        var character = 0;
        var length = Math.Min(offset, text.Length);
        for (var i = 0; i < length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return new LspPosition(line, character);
    }
}
