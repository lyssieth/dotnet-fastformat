using System.Text;
using System.Text.Json;

namespace FastFormat;

internal sealed class LspServer : IDisposable
{
    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly LspFormatterService _formatter;
    private readonly DocumentTracker _documents;
    private readonly Dictionary<string, CancellationTokenSource> _requestCts = new(StringComparer.Ordinal);
    private readonly object _requestCtsLock = new();
    private readonly List<string> _workspaceRoots = new();
    private readonly byte[] _readBuffer = new byte[4096];
    private int _readBufferRemaining;
    private bool _shutdownRequested;
    public LspServer(Stream input, Stream output, LspFormatterService? formatter = null, DocumentTracker? documents = null)
    {
        _input = input;
        _output = output;
        _formatter = formatter ?? new LspFormatterService();
        _documents = documents ?? new DocumentTracker();
    }
    public void Dispose()
    {
        lock (_requestCtsLock)
        {
            foreach (var cts in _requestCts.Values)
                cts.Dispose();
            _requestCts.Clear();
        }
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
            if (!hasId)
            {
                if (HandleNotification(method, hasParams ? parameters : default))
                    return _shutdownRequested ? 0 : 1;
                continue;
            }
            var requestKey = GetRequestKey(id);
            var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_requestCtsLock)
                _requestCts[requestKey] = requestCts;
            try
            {
                var result = await HandleRequestAsync(method, hasParams ? parameters : default, requestCts.Token);
                if (!requestCts.Token.IsCancellationRequested)
                    await WriteResultAsync(id, result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Request was canceled; do not send a response
            }
            catch (NotSupportedException ex)
            {
                await WriteErrorAsync(id, -32601, ex.Message, cancellationToken);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(id, -32603, ex.Message, cancellationToken);
            }
            finally
            {
                lock (_requestCtsLock)
                    _requestCts.Remove(requestKey);
                requestCts.Dispose();
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
            case "$/cancelRequest":
                HandleCancelRequest(parameters);
                return false;
            default:
                return false;
        }
    }
    private void HandleCancelRequest(JsonElement parameters)
    {
        if (parameters.TryGetProperty("id", out var id))
        {
            var requestKey = GetRequestKey(id);
            lock (_requestCtsLock)
            {
                if (_requestCts.TryGetValue(requestKey, out var cts))
                {
                    cts.Cancel();
                }
            }
        }
    }
    private async Task<object?> HandleRequestAsync(string? method, JsonElement parameters, CancellationToken cancellationToken)
    {
        return method switch
        {
            "initialize" => HandleInitialize(parameters),
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
    private object? HandleInitialize(JsonElement parameters)
    {
        _workspaceRoots.Clear();
        if (parameters.TryGetProperty("rootUri", out var rootUri))
        {
            var uri = rootUri.GetString();
            if (!string.IsNullOrEmpty(uri))
                _workspaceRoots.Add(uri);
        }
        if (parameters.TryGetProperty("workspaceFolders", out var folders))
        {
            foreach (var folder in folders.EnumerateArray())
            {
                if (folder.TryGetProperty("uri", out var folderUri))
                {
                    var uri = folderUri.GetString();
                    if (!string.IsNullOrEmpty(uri))
                        _workspaceRoots.Add(uri);
                }
            }
        }
        return InitializeResult.Instance;
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
        try
        {
            return await _formatter.FormatDocumentAsync(uri, text, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await WriteShowMessageAsync(1, $"FastFormat: {ex.Message}", cancellationToken);
            return [];
        }
    }
    private async Task<IReadOnlyList<LspTextEdit>> HandleRangeFormattingAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString();
        if (uri == null || !TryGetDocumentText(uri, out var text))
            return [];
        var rangeElement = parameters.GetProperty("range");
        var range = new LspRange(ReadPosition(rangeElement.GetProperty("start")), ReadPosition(rangeElement.GetProperty("end")));
        try
        {
            return await _formatter.FormatRangeAsync(uri, text, range, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await WriteShowMessageAsync(1, $"FastFormat: {ex.Message}", cancellationToken);
            return [];
        }
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
            if (IsUriInWorkspace(uri))
            {
                text = File.ReadAllText(parsed.LocalPath);
                return true;
            }
        }
        text = string.Empty;
        return false;
    }
    private bool IsUriInWorkspace(string uri)
    {
        if (_workspaceRoots.Count == 0)
            return true; // No workspace roots known, allow fallback
        foreach (var root in _workspaceRoots)
        {
            if (uri.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string GetRequestKey(JsonElement id)
    {
        return string.Concat(id.ValueKind.ToString(), ":", id.GetRawText());
    }

    private static LspPosition ReadPosition(JsonElement element)
    {
        return new LspPosition(element.GetProperty("line").GetInt32(), element.GetProperty("character").GetInt32());
    }
    private async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var header = new List<byte>(256);
        var match = 0;
        while (true)
        {
            if (_readBufferRemaining == 0)
            {
                var read = await _input.ReadAsync(_readBuffer.AsMemory(0, _readBuffer.Length), cancellationToken);
                if (read == 0)
                    return null;
                _readBufferRemaining = read;
            }
            for (int i = 0; i < _readBufferRemaining; i++)
            {
                var value = _readBuffer[i];
                header.Add(value);
                match = value == HeaderDelimiter[match] ? match + 1 : value == HeaderDelimiter[0] ? 1 : 0;
                if (match == HeaderDelimiter.Length)
                {
                    _readBufferRemaining -= i + 1;
                    if (_readBufferRemaining > 0)
                        Buffer.BlockCopy(_readBuffer, i + 1, _readBuffer, 0, _readBufferRemaining);
                    goto headerComplete;
                }
            }
            _readBufferRemaining = 0;
        }
    headerComplete:
        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var contentLength = ParseContentLength(headerText);
        if (contentLength < 0)
            return null;
        var payload = new byte[contentLength];
        var payloadOffset = 0;
        var payloadRemaining = contentLength;
        if (_readBufferRemaining > 0)
        {
            var fromBuffer = Math.Min(payloadRemaining, _readBufferRemaining);
            Buffer.BlockCopy(_readBuffer, 0, payload, payloadOffset, fromBuffer);
            payloadOffset += fromBuffer;
            payloadRemaining -= fromBuffer;
            _readBufferRemaining -= fromBuffer;
            if (_readBufferRemaining > 0)
                Buffer.BlockCopy(_readBuffer, fromBuffer, _readBuffer, 0, _readBufferRemaining);
        }
        while (payloadRemaining > 0)
        {
            var read = await _input.ReadAsync(payload.AsMemory(payloadOffset, payloadRemaining), cancellationToken);
            if (read == 0)
                return null;
            payloadOffset += read;
            payloadRemaining -= read;
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
                int.TryParse(line[(colon + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var length))
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
    private async Task WriteShowMessageAsync(int type, string message, CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "window/showMessage");
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteNumber("type", type);
            writer.WriteString("message", message);
            writer.WriteEndObject();
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
