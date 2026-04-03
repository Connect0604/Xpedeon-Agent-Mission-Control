using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class MCPConnectionTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ServerName { get; set; }
    public string? ServerVersion { get; set; }
    public int ToolCount { get; set; }
}

public class MCPToolInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InputSchemaJson { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
}

public class MCPToolDiscoveryResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<MCPToolInfo> Tools { get; set; } = new();
}

public class MCPToolCallResult
{
    public bool Success { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ResultText { get; set; } = string.Empty;
    public string Trace { get; set; } = string.Empty;
}

public class MCPService
{
    private const string ProtocolVersion = "2025-03-26";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MCPService> _logger;

    public MCPService(IHttpClientFactory httpClientFactory, ILogger<MCPService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<MCPConnectionTestResult> TestConnectionAsync(MCPServer server, CancellationToken cancellationToken = default)
    {
        try
        {
            if (server.TransportType == MCPTransportType.SSE)
            {
                var client = CreateHttpClient(server);
                using var response = await client.GetAsync(server.Endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MCPConnectionTestResult
                    {
                        Success = false,
                        Message = $"SSE endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}."
                    };
                }

                return new MCPConnectionTestResult
                {
                    Success = true,
                    Message = "SSE endpoint is reachable. Tool discovery requires an HTTP, WebSocket, or stdio MCP transport.",
                    ToolCount = 0
                };
            }

            await using var session = await CreateSessionAsync(server, cancellationToken);
            var init = await InitializeAsync(session, cancellationToken);
            var tools = await ListToolsInternalAsync(session, cancellationToken);

            return new MCPConnectionTestResult
            {
                Success = true,
                Message = "MCP server responded successfully.",
                ServerName = init.ServerName,
                ServerVersion = init.ServerVersion,
                ToolCount = tools.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP test connection failed for {ServerName}", server.Name);
            return new MCPConnectionTestResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public async Task<MCPToolDiscoveryResult> ListToolsAsync(MCPServer server, CancellationToken cancellationToken = default)
    {
        try
        {
            if (server.TransportType == MCPTransportType.SSE)
            {
                return new MCPToolDiscoveryResult
                {
                    Success = false,
                    Message = "Tool discovery is not supported for SSE-only MCP registration yet. Use HTTP, WebSocket, or stdio."
                };
            }

            await using var session = await CreateSessionAsync(server, cancellationToken);
            await InitializeAsync(session, cancellationToken);
            var tools = await ListToolsInternalAsync(session, cancellationToken);
            tools.ForEach(t =>
            {
                t.ServerId = server.Id;
                t.ServerName = server.Name;
            });

            return new MCPToolDiscoveryResult
            {
                Success = true,
                Message = tools.Count == 0 ? "Connected, but no tools were reported." : $"Discovered {tools.Count} tool(s).",
                Tools = tools
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP list tools failed for {ServerName}", server.Name);
            return new MCPToolDiscoveryResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public async Task<MCPToolCallResult> InvokeToolAsync(MCPServer server, string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (server.TransportType == MCPTransportType.SSE)
                throw new NotSupportedException("SSE tool invocation is not supported yet. Use HTTP, WebSocket, or stdio.");

            await using var session = await CreateSessionAsync(server, cancellationToken);
            await InitializeAsync(session, cancellationToken);

            using var response = await session.SendRequestAsync("tools/call", new
            {
                name = toolName,
                arguments
            }, cancellationToken);

            var result = response.RootElement.GetProperty("result");
            var text = ExtractToolResultText(result);

            return new MCPToolCallResult
            {
                Success = true,
                ToolName = toolName,
                ServerName = server.Name,
                ResultText = text,
                Trace = $"MCP tools/call via {server.TransportType} on {server.Name}: {toolName}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP tool call failed for {ToolName} on {ServerName}", toolName, server.Name);
            return new MCPToolCallResult
            {
                Success = false,
                ToolName = toolName,
                ServerName = server.Name,
                ResultText = ex.Message,
                Trace = $"MCP tool call failed on {server.Name}: {toolName} -> {ex.Message}"
            };
        }
    }

    public async Task<List<MCPToolInfo>> ListToolsAsync(IEnumerable<MCPServer> servers, CancellationToken cancellationToken = default)
    {
        var result = new List<MCPToolInfo>();
        foreach (var server in servers.Where(s => s.IsEnabled))
        {
            var tools = await ListToolsAsync(server, cancellationToken);
            if (tools.Success)
                result.AddRange(tools.Tools);
        }
        return result;
    }

    private async Task<MCPInitializeResult> InitializeAsync(IMCPSession session, CancellationToken cancellationToken)
    {
        using var response = await session.SendRequestAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new
            {
                name = "XpedeonAgentMissionControl",
                version = "1.0.0"
            }
        }, cancellationToken);

        var result = response.RootElement.GetProperty("result");
        await session.SendNotificationAsync("notifications/initialized", new { }, cancellationToken);

        string? serverName = null;
        string? serverVersion = null;

        if (result.TryGetProperty("serverInfo", out var serverInfo))
        {
            serverName = serverInfo.TryGetProperty("name", out var name) ? name.GetString() : null;
            serverVersion = serverInfo.TryGetProperty("version", out var version) ? version.GetString() : null;
        }

        return new MCPInitializeResult
        {
            ServerName = serverName,
            ServerVersion = serverVersion
        };
    }

    private async Task<List<MCPToolInfo>> ListToolsInternalAsync(IMCPSession session, CancellationToken cancellationToken)
    {
        using var response = await session.SendRequestAsync("tools/list", null, cancellationToken);
        var result = response.RootElement.GetProperty("result");
        var tools = new List<MCPToolInfo>();

        if (!result.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
            return tools;

        foreach (var tool in toolsElement.EnumerateArray())
        {
            tools.Add(new MCPToolInfo
            {
                Name = tool.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                Description = tool.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty,
                InputSchemaJson = tool.TryGetProperty("inputSchema", out var schema) ? schema.GetRawText() : "{}"
            });
        }

        return tools;
    }

    private async Task<IMCPSession> CreateSessionAsync(MCPServer server, CancellationToken cancellationToken)
    {
        return server.TransportType switch
        {
            MCPTransportType.Http => new HttpMCPSession(CreateHttpClient(server), server.Endpoint),
            MCPTransportType.WebSocket => await WebSocketMCPSession.CreateAsync(server, cancellationToken),
            MCPTransportType.Stdio => await StdioMCPSession.CreateAsync(server, cancellationToken),
            _ => throw new NotSupportedException($"MCP transport '{server.TransportType}' is not supported for this operation.")
        };
    }

    private HttpClient CreateHttpClient(MCPServer server)
    {
        var client = _httpClientFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(server.AuthToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.AuthToken);
        return client;
    }

    private static string ExtractToolResultText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    parts.Add(text.GetString() ?? string.Empty);
                else
                    parts.Add(item.GetRawText());
            }

            if (parts.Count > 0)
                return string.Join(Environment.NewLine, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        return result.GetRawText();
    }

    private sealed class MCPInitializeResult
    {
        public string? ServerName { get; set; }
        public string? ServerVersion { get; set; }
    }

    private interface IMCPSession : IAsyncDisposable
    {
        Task<JsonDocument> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken);
        Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken);
    }

    private sealed class HttpMCPSession : IMCPSession
    {
        private readonly HttpClient _client;
        private readonly string _endpoint;
        private string? _sessionId;

        public HttpMCPSession(HttpClient client, string endpoint)
        {
            _client = client;
            _endpoint = endpoint;
        }

        public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken)
            => PostNotificationAsync(CreatePayload(method, @params, isNotification: true), cancellationToken);

        public Task<JsonDocument> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
            => PostAsync(CreatePayload(method, @params, isNotification: false), cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<JsonDocument> PostAsync(string payload, CancellationToken cancellationToken)
        {
            using var request = CreateRequest(payload);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            CaptureSessionId(response);
            return await ReadJsonRpcResponseAsync(response, cancellationToken);
        }

        private async Task PostNotificationAsync(string payload, CancellationToken cancellationToken)
        {
            using var request = CreateRequest(payload);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            CaptureSessionId(response);
        }

        private HttpRequestMessage CreateRequest(string payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);

            if (!string.IsNullOrWhiteSpace(_sessionId))
                request.Headers.TryAddWithoutValidation("MCP-Session-Id", _sessionId);

            return request;
        }

        private void CaptureSessionId(HttpResponseMessage response)
        {
            if (TryGetHeader(response, "MCP-Session-Id", out var sessionId) ||
                TryGetHeader(response, "Mcp-Session-Id", out sessionId))
            {
                _sessionId = sessionId;
            }
        }

        private static bool TryGetHeader(HttpResponseMessage response, string headerName, out string? value)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                value = values.FirstOrDefault();
                return !string.IsNullOrWhiteSpace(value);
            }

            if (response.Content.Headers.TryGetValues(headerName, out var contentValues))
            {
                value = contentValues.FirstOrDefault();
                return !string.IsNullOrWhiteSpace(value);
            }

            value = null;
            return false;
        }

        private static async Task<JsonDocument> ReadJsonRpcResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var eventData = ParseSsePayload(body);
                return ParseAndValidate(eventData);
            }

            return ParseAndValidate(body);
        }

        private static string ParseSsePayload(string body)
        {
            var dataLines = body.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[5..].Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (!dataLines.Any())
                throw new InvalidOperationException("MCP server returned an SSE stream without JSON-RPC data.");

            return string.Join("\n", dataLines);
        }
    }

    private sealed class WebSocketMCPSession : IMCPSession
    {
        private readonly ClientWebSocket _socket;

        private WebSocketMCPSession(ClientWebSocket socket)
        {
            _socket = socket;
        }

        public static async Task<WebSocketMCPSession> CreateAsync(MCPServer server, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(server.AuthToken))
                socket.Options.SetRequestHeader("Authorization", $"Bearer {server.AuthToken}");

            await socket.ConnectAsync(new Uri(server.Endpoint), cancellationToken);
            return new WebSocketMCPSession(socket);
        }

        public async Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            var payload = CreatePayload(method, @params, isNotification: true);
            await SendAsync(payload, cancellationToken);
        }

        public async Task<JsonDocument> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            var payload = CreatePayload(method, @params, isNotification: false);
            await SendAsync(payload, cancellationToken);
            var response = await ReceiveAsync(cancellationToken);
            return ParseAndValidate(response);
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);

            _socket.Dispose();
        }

        private Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            return _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private async Task<string> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var builder = new StringBuilder();

            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("WebSocket MCP server closed the connection.");

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                    return builder.ToString();
            }
        }
    }

    private sealed class StdioMCPSession : IMCPSession
    {
        private readonly Process _process;
        private readonly Stream _stdin;
        private readonly Stream _stdout;

        private StdioMCPSession(Process process)
        {
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            _stdout = process.StandardOutput.BaseStream;
        }

        public static async Task<StdioMCPSession> CreateAsync(MCPServer server, CancellationToken cancellationToken)
        {
            var tokens = TokenizeCommand(server.Endpoint);
            if (tokens.Count == 0)
                throw new InvalidOperationException("A stdio MCP server requires a command line in Endpoint.");

            var psi = new ProcessStartInfo
            {
                FileName = tokens[0],
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            for (var i = 1; i < tokens.Count; i++)
                psi.ArgumentList.Add(tokens[i]);

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start stdio MCP process.");

            await Task.Delay(100, cancellationToken);
            return new StdioMCPSession(process);
        }

        public async Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            var payload = CreatePayload(method, @params, isNotification: true);
            await WriteFrameAsync(payload, cancellationToken);
        }

        public async Task<JsonDocument> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken)
        {
            var payload = CreatePayload(method, @params, isNotification: false);
            await WriteFrameAsync(payload, cancellationToken);
            var response = await ReadFrameAsync(cancellationToken);
            return ParseAndValidate(response);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }

            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task WriteFrameAsync(string payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            await _stdin.WriteAsync(header, cancellationToken);
            await _stdin.WriteAsync(bytes, cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }

        private async Task<string> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var endMarker = Encoding.ASCII.GetBytes("\r\n\r\n");

            while (true)
            {
                var buffer = new byte[1];
                var read = await _stdout.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    throw new InvalidOperationException("Stdio MCP process closed without returning a response.");

                headerBytes.Add(buffer[0]);
                if (headerBytes.Count >= endMarker.Length &&
                    headerBytes.Skip(headerBytes.Count - endMarker.Length).SequenceEqual(endMarker))
                    break;
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var match = Regex.Match(headerText, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new InvalidOperationException("Invalid stdio MCP response header.");

            var contentLength = int.Parse(match.Groups[1].Value);
            var payload = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await _stdout.ReadAsync(payload.AsMemory(offset, contentLength - offset), cancellationToken);
                if (read == 0)
                    throw new InvalidOperationException("Unexpected end of stdio MCP response.");
                offset += read;
            }

            return Encoding.UTF8.GetString(payload);
        }
    }

    private static JsonDocument ParseAndValidate(string json)
    {
        var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException(message ?? "MCP request failed.");
        }

        return document;
    }

    private static string CreatePayload(string method, object? @params, bool isNotification)
    {
        object payload = isNotification
            ? new { jsonrpc = "2.0", method, @params = @params ?? new { } }
            : new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = @params ?? new { } };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static List<string> TokenizeCommand(string command)
    {
        var matches = Regex.Matches(command, "\"([^\"]*)\"|'([^']*)'|(\\S+)");
        return matches
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value
                       : m.Groups[2].Success ? m.Groups[2].Value
                       : m.Groups[3].Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
