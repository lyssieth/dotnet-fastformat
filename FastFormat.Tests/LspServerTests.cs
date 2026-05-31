using System.Text;
using System.Text.Json;

namespace FastFormat.Tests;

public sealed class LspServerTests : IDisposable
{
    private readonly string _tempDir;

    public LspServerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FastFormat.LspServer.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunAsync_Initialize_AdvertisesFormattingCapabilities()
    {
        var output = await RunServerAsync(
            Request(1, "initialize", "{\"processId\":null,\"rootUri\":null,\"capabilities\":{}}"),
            Request(2, "shutdown", "null"),
            Notification("exit", "null"));

        using var response = ParseResponse(output, 1);
        var capabilities = response.RootElement.GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("documentRangeFormattingProvider").GetBoolean());
        Assert.Equal(1, capabilities.GetProperty("textDocumentSync").GetInt32());
    }

    [Fact]
    public async Task RunAsync_DidOpenThenFormatting_ReturnsTextEdit()
    {
        var path = Path.Combine(_tempDir, "a.cs");
        var uri = new Uri(path).AbsoluteUri;
        var didOpen = "{\"textDocument\":{\"uri\":" + JsonSerializer.Serialize(uri) + ",\"languageId\":\"csharp\",\"version\":1,\"text\":\"class C{\\n}\\n\"}}";
        var formatting = "{\"textDocument\":{\"uri\":" + JsonSerializer.Serialize(uri) + "},\"options\":{\"tabSize\":4,\"insertSpaces\":true}}";

        var output = await RunServerAsync(
            Notification("textDocument/didOpen", didOpen),
            Request(3, "textDocument/formatting", formatting),
            Request(4, "shutdown", "null"),
            Notification("exit", "null"));

        using var response = ParseResponse(output, 3);
        var edit = response.RootElement.GetProperty("result")[0];
        Assert.Equal("class C\n{\n}\n", edit.GetProperty("newText").GetString());
        Assert.Equal(0, edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task RunAsync_RangeFormatting_ReturnsTextEdit()
    {
        var path = Path.Combine(_tempDir, "a.cs");
        var uri = new Uri(path).AbsoluteUri;
        var didOpen = "{\"textDocument\":{\"uri\":" + JsonSerializer.Serialize(uri) + ",\"languageId\":\"csharp\",\"version\":1,\"text\":\"class C\\n{\\nvoid M(){\\n}\\n}\\n\"}}";
        var rangeFormatting = "{\"textDocument\":{\"uri\":" + JsonSerializer.Serialize(uri) + "},\"range\":{\"start\":{\"line\":2,\"character\":0},\"end\":{\"line\":4,\"character\":0}},\"options\":{\"tabSize\":4,\"insertSpaces\":true}}";

        var output = await RunServerAsync(
            Notification("textDocument/didOpen", didOpen),
            Request(5, "textDocument/rangeFormatting", rangeFormatting),
            Request(6, "shutdown", "null"),
            Notification("exit", "null"));

        using var response = ParseResponse(output, 5);
        Assert.NotEmpty(response.RootElement.GetProperty("result").EnumerateArray());
    }

    private static async Task<string> RunServerAsync(params string[] messages)
    {
        var inputBytes = Encoding.UTF8.GetBytes(string.Concat(messages.Select(Frame)));
        await using var input = new MemoryStream(inputBytes);
        await using var output = new MemoryStream();
        using var service = new LspFormatterService();
        var server = new LspServer(input, output, service);

        var exit = await server.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static JsonDocument ParseResponse(string output, int id)
    {
        foreach (var payload in Unframe(output))
        {
            var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                return document;
            document.Dispose();
        }

        throw new InvalidOperationException($"Response {id} not found in {output}");
    }

    private static IEnumerable<string> Unframe(string output)
    {
        var cursor = 0;
        while (cursor < output.Length)
        {
            var headerEnd = output.IndexOf("\r\n\r\n", cursor, StringComparison.Ordinal);
            if (headerEnd < 0)
                yield break;

            var header = output[cursor..headerEnd];
            var lengthPrefix = "Content-Length: ";
            var lengthStart = header.IndexOf(lengthPrefix, StringComparison.Ordinal);
            Assert.True(lengthStart >= 0, output);
            var lengthText = header[(lengthStart + lengthPrefix.Length)..].Trim();
            var length = int.Parse(lengthText);
            var bodyStart = headerEnd + 4;
            yield return output.Substring(bodyStart, length);
            cursor = bodyStart + length;
        }
    }

    private static string Request(int id, string method, string parameters)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":" + JsonSerializer.Serialize(method) + ",\"params\":" + parameters + "}";
    }

    private static string Notification(string method, string parameters)
    {
        return "{\"jsonrpc\":\"2.0\",\"method\":" + JsonSerializer.Serialize(method) + ",\"params\":" + parameters + "}";
    }

    private static string Frame(string payload)
    {
        return $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";
    }
}
