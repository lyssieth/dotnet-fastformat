using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;

namespace FastFormat;

internal class FormatCache
{
    private readonly string _cacheFilePath;
    private readonly ConcurrentDictionary<string, string> _entries;
    private volatile bool _dirty;

    public FormatCache(string gitRoot)
    {
        _cacheFilePath = Path.Combine(gitRoot, ".fastformat-cache");
        _entries = Load(_cacheFilePath);
    }

    private static ConcurrentDictionary<string, string> Load(string path)
    {
        var dict = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return dict;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var pipe = line.IndexOf('|');
            if (pipe < 0)
                continue;
            var relPath = line[..pipe];
            var hash = line[(pipe + 1)..];
            dict[relPath] = hash;
        }

        return dict;
    }

    public bool TryGet(string relativePath, ReadOnlySpan<byte> hash)
    {
        if (!_entries.TryGetValue(relativePath, out var cachedHex))
            return false;
        return cachedHex.Equals(Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase);
    }

    public void Set(string relativePath, ReadOnlySpan<byte> hash)
    {
        var hex = Convert.ToHexString(hash);
        if (_entries.TryGetValue(relativePath, out var existing) && existing.Equals(hex, StringComparison.OrdinalIgnoreCase))
            return;
        _entries[relativePath] = hex;
        _dirty = true;
    }

    public void Flush()
    {
        if (!_dirty)
            return;

        var tmp = _cacheFilePath + ".tmp";
        using (var writer = new StreamWriter(tmp, false, Encoding.UTF8))
        {
            foreach (var kvp in _entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.Write(kvp.Key);
                writer.Write('|');
                writer.WriteLine(kvp.Value);
            }
        }

        File.Move(tmp, _cacheFilePath, overwrite: true);
        _dirty = false;
    }
}
