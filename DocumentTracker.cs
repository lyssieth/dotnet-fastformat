using System.Collections.Concurrent;

namespace FastFormat;

internal sealed class DocumentTracker
{
    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);

    public void Open(string uri, string text)
    {
        _documents[uri] = text;
    }

    public void Change(string uri, string text)
    {
        _documents[uri] = text;
    }

    public void Close(string uri)
    {
        _documents.TryRemove(uri, out _);
    }

    public bool TryGetText(string uri, out string? text)
    {
        return _documents.TryGetValue(uri, out text);
    }
}
