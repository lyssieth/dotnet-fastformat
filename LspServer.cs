using System.Text;
using System.Text.Json;

namespace FastFormat;

internal sealed class LspServer
{
    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly LspFormatterService _formatter;
    private readonly DocumentTracker _documents;
    private bool _shutdownRequested;

    public LspServer(Stream input, Stream output, LspFormatterService? formatter = null, DocumentTracker? documents = null)
    {
        _input = input;
        _output = output;
        _formatter = formatter ?? new LspFormatterService();
        _documents = documents ?? new DocumentTracker();
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await ReadMessageAsync(cancellationToken);
            if (payload == null)
                return 0;

            using var message = JsonDocument.Parse(payload);
            if (!message.RootElement.TryGetProperty("method", out var methodElement))
                continue;

            var method = methodElement.GetString();
            var hasId = message.RootElement.TryGetProperty("id", out var id);
            var hasParams = message.RootElement.TryGetProperty("params", out var parameters);

            try
            {
                if (!hasId)
                {
                    if (HandleNotification(method, hasParams ? parameters : default))
                        return _shutdownRequested ? 0 : 1;
                    continue;
                }

                var result = await HandleRequestAsync(method, hasParams ? parameters : default, cancellationToken);
                await WriteResultAsync(id, result, cancellationToken);
            }
            catch (NotSupportedException ex) when (hasId)
            {
                await WriteErrorAsync(id, -32601, ex.Message, cancellationToken);
            }
            catch (Exception ex) when (hasId)
            {
                await WriteErrorAsync(id, -32603, ex.Message, cancellationToken);
            }
        }

        return 1;
    }

    private bool HandleNotification(string? method, JsonElement parameters)
    {
        switch (method)
        {
            case "initialized":
                return false;
            case "exit":
                return true;
            case "textDocument/didOpen":
                HandleDidOpen(parameters);
                return false;
            case "textDocument/didChange":
                HandleDidChange(parameters);
                return false;
            case "textDocument/didClose":
                HandleDidClose(parameters);
                return false;
            default:
                return false;
        }
    }

    private async Task<object?> HandleRequestAsync(string? method, JsonElement parameters, CancellationToken cancellationToken)
    {
        return method switch
        {
            "initialize" => InitializeResult.Instance,
            "shutdown" => HandleShutdown(),
            "textDocument/formatting" => await HandleFormattingAsync(parameters, cancellationToken),
            "textDocument/rangeFormatting" => await HandleRangeFormattingAsync(parameters, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported LSP method: {method}"),
        };
    }

    private object? HandleShutdown()
    {
        _shutdownRequested = true;
        _formatter.Dispose();
        return null;
    }

    private void HandleDidOpen(JsonElement parameters)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        var text = textDocument.GetProperty("text").GetString();
        if (uri != null && text != null)
            _documents.Open(uri, text);
    }

    private void HandleDidChange(JsonElement parameters)
    {
        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString();
        var changes = parameters.GetProperty("contentChanges");
        if (uri == null || changes.GetArrayLength() == 0)
            return;

        var first = changes[0];
        if (first.TryGetProperty("text", out var text))
            _documents.Change(uri, text.GetString() ?? string.Empty);
    }

    private void HandleDidClose(JsonElement parameters)
    {
        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString();
        if (uri != null)
            _documents.Close(uri);
    }

    private async Task<IReadOnlyList<LspTextEdit>> HandleFormattingAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString();
        if (uri == null || !TryGetDocumentText(uri, out var text))
            return [];

        return await _formatter.FormatDocumentAsync(uri, text, cancellationToken);
    }

    private async Task<IReadOnlyList<LspTextEdit>> HandleRangeFormattingAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString();
        if (uri == null || !TryGetDocumentText(uri, out var text))
            return [];

        var rangeElement = parameters.GetProperty("range");
        var range = new LspRange(ReadPosition(rangeElement.GetProperty("start")), ReadPosition(rangeElement.GetProperty("end")));
        return await _formatter.FormatRangeAsync(uri, text, range, cancellationToken);
    }

    private bool TryGetDocumentText(string uri, out string text)
    {
        if (_documents.TryGetText(uri, out var trackedText) && trackedText != null)
        {
            text = trackedText;
            return true;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile && File.Exists(parsed.LocalPath))
        {
            text = File.ReadAllText(parsed.LocalPath);
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static LspPosition ReadPosition(JsonElement element)
    {
        return new LspPosition(element.GetProperty("line").GetInt32(), element.GetProperty("character").GetInt32());
    }

    private async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var header = new List<byte>(64);
        var match = 0;
        while (true)
        {
            var buffer = new byte[1];
            var read = await _input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return null;

            var value = buffer[0];
            header.Add(value);
            match = value == HeaderDelimiter[match] ? match + 1 : value == HeaderDelimiter[0] ? 1 : 0;
            if (match == HeaderDelimiter.Length)
                break;
        }

        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var contentLength = ParseContentLength(headerText);
        if (contentLength < 0)
            return null;

        var payload = new byte[contentLength];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await _input.ReadAsync(payload.AsMemory(offset, payload.Length - offset), cancellationToken);
            if (read == 0)
                return null;
            offset += read;
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static int ParseContentLength(string headerText)
    {
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            if (line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(colon + 1)..].Trim(), out var length))
                return length;
        }

        return -1;
    }

    private async Task WriteResultAsync(JsonElement id, object? result, CancellationToken cancellationToken)
    {
        await WriteMessageAsync(id, result, error: null, cancellationToken);
    }

    private async Task WriteErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken)
    {
        await WriteMessageAsync(id, result: null, new JsonRpcError(code, message), cancellationToken);
    }

    private async Task WriteMessageAsync(JsonElement id, object? result, JsonRpcError? error, CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            if (error == null)
            {
                writer.WritePropertyName("result");
                WriteResultValue(writer, result);
            }
            else
            {
                writer.WritePropertyName("error");
                WriteErrorValue(writer, error);
            }
            writer.WriteEndObject();
        }

        var bytes = body.ToArray();
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        await _output.WriteAsync(header, cancellationToken);
        await _output.WriteAsync(bytes, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    private static void WriteResultValue(Utf8JsonWriter writer, object? result)
    {
        switch (result)
        {
            case null:
                writer.WriteNullValue();
                break;
            case InitializeResult:
                writer.WriteStartObject();
                writer.WritePropertyName("capabilities");
                writer.WriteStartObject();
                writer.WriteNumber("textDocumentSync", 1);
                writer.WriteBoolean("documentFormattingProvider", true);
                writer.WriteBoolean("documentRangeFormattingProvider", true);
                writer.WriteEndObject();
                writer.WritePropertyName("serverInfo");
                writer.WriteStartObject();
                writer.WriteString("name", "FastFormat");
                writer.WriteString("version", typeof(Program).Assembly.GetName().Version?.ToString());
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case IReadOnlyList<LspTextEdit> edits:
                writer.WriteStartArray();
                foreach (var edit in edits)
                    WriteTextEdit(writer, edit);
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException($"Unsupported LSP result type: {result.GetType().Name}");
        }
    }

    private static void WriteTextEdit(Utf8JsonWriter writer, LspTextEdit edit)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("range");
        WriteRange(writer, edit.Range);
        writer.WriteString("newText", edit.NewText);
        writer.WriteEndObject();
    }

    private static void WriteRange(Utf8JsonWriter writer, LspRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, range.Start);
        writer.WritePropertyName("end");
        WritePosition(writer, range.End);
        writer.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter writer, LspPosition position)
    {
        writer.WriteStartObject();
        writer.WriteNumber("line", position.Line);
        writer.WriteNumber("character", position.Character);
        writer.WriteEndObject();
    }

    private static void WriteErrorValue(Utf8JsonWriter writer, JsonRpcError error)
    {
        writer.WriteStartObject();
        writer.WriteNumber("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WriteEndObject();
    }

    private sealed class InitializeResult
    {
        public static readonly InitializeResult Instance = new();
        private InitializeResult() { }
    }

    private sealed record JsonRpcError(int Code, string Message);
}
