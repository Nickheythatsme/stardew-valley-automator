using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StardewAgent.Protocol;
using StardewModdingAPI;

namespace StardewAgent.Mod.Protocol;

internal sealed class ProtocolServer : IDisposable
{
    private readonly IMonitor monitor;
    private readonly string endpointPath;
    private readonly string token;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentQueue<GameCommand> incoming = new();
    private readonly Channel<object> outgoing = Channel.CreateBounded<object>(
        new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<string, ResponseEnvelope> responseCache = new(StringComparer.Ordinal);
    private readonly Queue<string> responseOrder = new();
    private readonly object cacheLock = new();
    private readonly object connectionLock = new();
    private TcpListener? listener;
    private TcpClient? client;
    private Task? acceptTask;
    private bool authenticated;
    private long eventSequence;

    public ProtocolServer(IMonitor monitor, string endpointPath)
    {
        this.monitor = monitor;
        this.endpointPath = endpointPath;
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public bool IsClientConnected
    {
        get
        {
            lock (connectionLock)
                return client?.Connected == true && authenticated;
        }
    }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(2);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Directory.CreateDirectory(Path.GetDirectoryName(endpointPath)!);
        var endpoint = new
        {
            protocol_version = ProtocolLimits.ProtocolVersion,
            host = "127.0.0.1",
            port,
            token,
            pid = Environment.ProcessId,
            started_at_utc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(endpointPath, JsonSerializer.Serialize(endpoint, JsonDefaults.Options));
        if (!OperatingSystem.IsWindows() && UnixPermissions.Chmod(endpointPath, 0x180) != 0)
            monitor.Log("Could not restrict endpoint.json to the current user.", LogLevel.Warn);
        acceptTask = Task.Run(() => AcceptLoopAsync(shutdown.Token));
        monitor.Log($"Stardew Agent protocol server listening on 127.0.0.1:{port}.", LogLevel.Info);
    }

    public bool TryDequeue(out GameCommand? command) => incoming.TryDequeue(out command);

    public void SendResponse(ResponseEnvelope response)
    {
        lock (cacheLock)
        {
            if (!responseCache.ContainsKey(response.RequestId))
                responseOrder.Enqueue(response.RequestId);
            responseCache[response.RequestId] = response;
            while (responseOrder.Count > 256)
                responseCache.Remove(responseOrder.Dequeue());
        }
        outgoing.Writer.TryWrite(response);
    }

    public void Publish(string eventName, string? executionId, object payload) =>
        outgoing.Writer.TryWrite(new EventEnvelope(
            ProtocolLimits.ProtocolVersion,
            "event",
            Interlocked.Increment(ref eventSequence),
            eventName,
            executionId,
            payload));

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener is not null)
        {
            TcpClient accepted;
            try
            {
                accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (connectionLock)
            {
                if (client is not null)
                {
                    _ = RejectAdditionalClientAsync(accepted);
                    continue;
                }
                client = accepted;
                authenticated = false;
            }

            try
            {
                await HandleClientAsync(accepted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                monitor.Log($"Protocol client disconnected: {error.Message}", LogLevel.Trace);
            }
            finally
            {
                lock (connectionLock)
                {
                    accepted.Dispose();
                    if (ReferenceEquals(client, accepted))
                    {
                        client = null;
                        authenticated = false;
                    }
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient accepted, CancellationToken cancellationToken)
    {
        accepted.NoDelay = true;
        using var stream = accepted.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
        using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        var firstLine = await reader.ReadLineAsync().WaitAsync(authTimeout.Token).ConfigureAwait(false);
        if (firstLine is null || Encoding.UTF8.GetByteCount(firstLine) > ProtocolLimits.MaxLineBytes)
            return;
        var firstRequest = DeserializeRequest(firstLine);
        if (firstRequest is null || firstRequest.Method != "hello")
        {
            await WriteAsync(writer, ResponseEnvelope.Failure(firstRequest?.RequestId ?? "unknown", "AUTHENTICATION_REQUIRED", "hello must be the first request.")).ConfigureAwait(false);
            return;
        }
        var hello = firstRequest.Params.Deserialize<HelloParams>(JsonDefaults.Options);
        if (hello is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), Encoding.UTF8.GetBytes(token)))
        {
            await WriteAsync(writer, ResponseEnvelope.Failure(firstRequest.RequestId, "AUTHENTICATION_FAILED", "The session token is invalid.")).ConfigureAwait(false);
            return;
        }

        authenticated = true;
        incoming.Enqueue(new GameCommand(firstRequest));
        using var clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writerTask = Task.Run(() => WriterLoopAsync(writer, clientLifetime.Token), clientLifetime.Token);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (Encoding.UTF8.GetByteCount(line) > ProtocolLimits.MaxLineBytes)
            {
                await WriteAsync(writer, ResponseEnvelope.Failure("unknown", "MESSAGE_TOO_LARGE", "Protocol lines are limited to 1 MiB.")).ConfigureAwait(false);
                break;
            }
            var request = DeserializeRequest(line);
            if (request is null)
            {
                await WriteAsync(writer, ResponseEnvelope.Failure("unknown", "INVALID_REQUEST", "The request envelope is invalid.")).ConfigureAwait(false);
                continue;
            }
            ResponseEnvelope? cached;
            lock (cacheLock)
                responseCache.TryGetValue(request.RequestId, out cached);
            if (cached is not null)
            {
                outgoing.Writer.TryWrite(cached);
                continue;
            }
            incoming.Enqueue(new GameCommand(request));
        }

        clientLifetime.Cancel();
        try { await writerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static RequestEnvelope? DeserializeRequest(string line)
    {
        try
        {
            var request = JsonSerializer.Deserialize<RequestEnvelope>(line, JsonDefaults.Options);
            return request is { ProtocolVersion: ProtocolLimits.ProtocolVersion, Type: "request" }
                && !string.IsNullOrWhiteSpace(request.RequestId)
                ? request
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriterLoopAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        await foreach (var message in outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await WriteAsync(writer, message).ConfigureAwait(false);
    }

    private static Task WriteAsync(StreamWriter writer, object message) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonDefaults.Options));

    private static async Task RejectAdditionalClientAsync(TcpClient extra)
    {
        using (extra)
        using (var writer = new StreamWriter(extra.GetStream(), new UTF8Encoding(false)) { AutoFlush = true })
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                ResponseEnvelope.Failure("unknown", "CLIENT_ALREADY_CONNECTED", "Only one agent client may connect at a time."),
                JsonDefaults.Options)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        shutdown.Cancel();
        listener?.Stop();
        lock (connectionLock)
            client?.Dispose();
        try { acceptTask?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        if (File.Exists(endpointPath))
            File.Delete(endpointPath);
        shutdown.Dispose();
    }
}

internal static class UnixPermissions
{
    // 0600: owner read/write only. The numeric value is 0x180 in the native mode_t bitmask.
    [DllImport("libc", EntryPoint = "chmod", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern int Chmod(string path, uint mode);
}
