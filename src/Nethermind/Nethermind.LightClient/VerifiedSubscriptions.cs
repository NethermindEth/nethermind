// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient;

/// <summary>Streams authenticated execution heads and logs over bounded, connection-owned WebSocket subscriptions.</summary>
internal sealed class VerifiedSubscriptions : IAsyncDisposable
{
    internal const int MaxClients = 16;
    internal const int MaxSubscriptions = 16;
    internal const int MaxHistory = 32;
    internal const int MaxMessageBytes = 256 * 1024;
    internal const int MaxQueuedBytes = 1024 * 1024;
    private const int MaxHistoryBytes = 16 * 1024 * 1024;
    private readonly IExecutionStateSource _source;
    private readonly VerifiedRpc _rpc;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _clientsLock = new();
    private readonly Dictionary<HttpContext, TaskCompletionSource> _connections = [];
    private readonly HashSet<Client> _clients = [];
    private readonly Channel<(long Sequence, VerifiedHead Head)> _heads = Channel.CreateBounded<(long, VerifiedHead)>(
        new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _publisher;
    private readonly List<Snapshot> _history = [];
    private Publication? _overflow;
    private long _sequence;
    private bool _disposing;

    internal VerifiedSubscriptions(IExecutionStateSource source, VerifiedRpc rpc, ILogger logger,
        TimeProvider? clock = null)
    {
        _source = source;
        _rpc = rpc;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _publisher = PublishLoopAsync();
    }

    /// <summary>Enqueues the final authenticated latest head without waiting for execution data or consumers.</summary>
    internal void PublishHead(VerifiedHead head)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        if (!_heads.Writer.TryWrite((sequence, head)) && !_stopping.IsCancellationRequested)
        {
            Volatile.Write(ref _overflow, new(sequence, head));
            CloseClients("Authenticated head queue overflowed; reconnect and resubscribe.");
        }
    }

    internal async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_clientsLock)
        {
            if (_disposing || _connections.Count >= MaxClients)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            _connections.Add(context, completed);
        }
        Client? client = null;
        try
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            client = new(this, socket, context.RequestAborted);
            bool stopping;
            lock (_clientsLock)
            {
                stopping = _disposing;
                if (!stopping) _clients.Add(client);
            }
            if (stopping) client.Terminate();
            await client.RunAsync();
        }
        finally
        {
            lock (_clientsLock)
            {
                if (client is not null) _clients.Remove(client);
                _connections.Remove(context);
            }
            completed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        HttpContext[] contexts;
        Task[] completions;
        bool first;
        lock (_clientsLock)
        {
            first = !_disposing;
            _disposing = true;
            contexts = new HttpContext[_connections.Count];
            completions = new Task[contexts.Length];
            int i = 0;
            foreach ((HttpContext context, TaskCompletionSource completion) in _connections)
            {
                contexts[i] = context;
                completions[i++] = completion.Task;
            }
        }
        if (!first) { await _disposed.Task; return; }
        try
        {
            await _stopping.CancelAsync();
            _heads.Writer.TryComplete();
            CloseClients("Light client is stopping.");
            foreach (HttpContext context in contexts) context.Abort();
            await _publisher;
            await Task.WhenAll(completions);
            _stopping.Dispose();
        }
        finally { _disposed.TrySetResult(); }
    }

    private Client[] Clients()
    {
        lock (_clientsLock)
        {
            Client[] result = new Client[_clients.Count];
            _clients.CopyTo(result);
            return result;
        }
    }

    private void CloseClients(string reason)
    {
        Client[] clients = Clients();
        if (clients.Length != 0) _logger.LogWarning("Closing verified subscriptions: {Reason}", reason);
        foreach (Client client in clients) client.Terminate();
    }

    private async Task PublishLoopAsync()
    {
        try
        {
            await foreach ((long sequence, VerifiedHead head) in _heads.Reader.ReadAllAsync(_stopping.Token))
            {
                Publication? overflow = Volatile.Read(ref _overflow);
                if (overflow is not null)
                {
                    if (_history.Count == 0 || _history[^1].Sequence < overflow.Sequence)
                    {
                        _history.Clear();
                        _history.Add(new(overflow.Head, overflow.Sequence, default, default, 0));
                    }
                    if (sequence <= overflow.Sequence) continue;
                }
                try
                {
                    Client[] clients = Clients();
                    bool heads = false;
                    bool logs = false;
                    foreach (Client client in clients) client.Interests(sequence, ref heads, ref logs);
                    if (!heads && !logs)
                    {
                        if (_history.Count > 0 && _history[^1].Head.BlockHash == head.BlockHash) continue;
                        if (_history.Count > 0 && head.Number <= _history[^1].Head.Number) _history.Clear();
                        _history.Add(new(head, sequence, default, default, 0));
                        if (_history.Count > MaxHistory) _history.RemoveAt(0);
                        continue;
                    }
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90), _clock);
                    using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stopping.Token);
                    await ProcessHeadAsync(head, sequence, clients, heads, logs, cancellation.Token);
                }
                catch (Exception exception) when (!_stopping.IsCancellationRequested)
                {
                    _logger.LogWarning(exception, "Unable to maintain an authenticated subscription stream");
                    CloseClients("Verified history or execution data is unavailable; reconnect and resubscribe.");
                    _history.Clear();
                    _history.Add(new(head, sequence, default, default, 0));
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task ProcessHeadAsync(VerifiedHead head, long sequence, Client[] clients, bool needsHeads, bool needsLogs,
        CancellationToken cancellationToken)
    {
        if (_history.Count > 0 && _history[^1].Head.BlockHash == head.BlockHash) return;
        BlockHeader current = await _source.GetHeaderAsync(head, cancellationToken);
        ValidateHeader(current, head.BlockHash, head.Number);
        if (current.StateRoot != head.StateRoot) throw new InvalidDataException("Subscription head state root does not match authenticated consensus.");
        List<BlockHeader> branch = [];
        int common = -1;
        while (true)
        {
            for (int i = _history.Count - 1; i >= 0; i--)
                if (_history[i].Head.BlockHash == current.Hash) { common = i; break; }
            if (common >= 0) break;
            if (branch.Count >= MaxHistory)
                throw new InvalidDataException("Subscription gap or reorganization exceeds the 32-block authenticated history.");
            branch.Add(current);
            if (_history.Count == 0) break;
            if (current.Number == 0)
                throw new InvalidDataException("Subscription gap or reorganization exceeds the 32-block authenticated history.");
            Hash256 parent = current.ParentHash!;
            ulong number = current.Number - 1;
            current = await _source.GetHeaderByHashAsync(parent, cancellationToken);
            ValidateHeader(current, parent, number);
        }
        List<Snapshot> added = new(branch.Count);
        int encodedBytes = 0;
        for (int i = branch.Count - 1; i >= 0; i--)
        {
            BlockHeader header = branch[i];
            VerifiedHead anchor = new(header.Hash == head.BlockHash ? head.Slot : 0, header.Number, header.Hash!, header.StateRoot!);
            JsonElement rendered = default;
            if (needsHeads && i == 0)
                rendered = _rpc.GetHeader(header);
            JsonElement logs = needsLogs
                ? await _rpc.GetSubscriptionLogsAsync(header, cancellationToken)
                : default;
            int bytes = Size(rendered) + Size(logs);
            encodedBytes = checked(encodedBytes + bytes);
            if (encodedBytes > MaxHistoryBytes) throw new InvalidDataException("Subscription history exceeds its 16 MiB encoded-data budget.");
            added.Add(new(anchor, sequence, rendered, ParseLogs(logs), bytes));
        }
        int retained = 0;
        for (int i = Math.Max(0, common + 1 + added.Count - MaxHistory); i <= common; i++) retained += _history[i].Bytes;
        if (retained + encodedBytes > MaxHistoryBytes) throw new InvalidDataException("Subscription history exceeds its 16 MiB encoded-data budget.");
        List<Snapshot> removed = [];
        for (int i = _history.Count - 1; i > common; i--) removed.Add(_history[i]);
        Snapshot? tip = needsHeads && added.Count != 0 ? added[^1] : null;
        if (needsHeads && added.Count == 0 && common >= 0)
        {
            JsonElement rendered = _history[common].Header.ValueKind == JsonValueKind.Undefined
                ? _rpc.GetHeader(current)
                : _history[common].Header;
            tip = _history[common] with { Sequence = sequence, Header = rendered };
        }
        if (common + 1 < _history.Count) _history.RemoveRange(common + 1, _history.Count - common - 1);
        _history.AddRange(added);
        if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
        Task[] deliveries = new Task[clients.Length];
        for (int i = 0; i < clients.Length; i++) deliveries[i] = clients[i].DeliverAsync(removed, added, tip, cancellationToken);
        await Task.WhenAll(deliveries);
    }

    private static void ValidateHeader(BlockHeader header, Hash256 hash, ulong number)
    {
        if (header.Number != number || header.Hash != hash || Keccak.Compute(Rlp.Encode(header).Bytes) != hash)
            throw new InvalidDataException("Execution peer returned an invalid subscription header or ancestor.");
    }

    private static int Size(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? 0 : Encoding.UTF8.GetByteCount(value.GetRawText());

    private static LogData[]? ParseLogs(JsonElement logs)
    {
        if (logs.ValueKind == JsonValueKind.Undefined) return null;
        LogData[] result = new LogData[logs.GetArrayLength()];
        int index = 0;
        foreach (JsonElement value in logs.EnumerateArray())
        {
            JsonElement jsonTopics = value.GetProperty("topics");
            Hash256[] topics = new Hash256[jsonTopics.GetArrayLength()];
            for (int i = 0; i < topics.Length; i++) topics[i] = new(jsonTopics[i].GetString()!);
            result[index++] = new(value, new(value.GetProperty("address").GetString()!), topics);
        }
        return result;
    }

    private sealed record Snapshot(VerifiedHead Head, long Sequence, JsonElement Header, LogData[]? Logs, int Bytes);
    private sealed record Publication(long Sequence, VerifiedHead Head);
    private readonly record struct LogData(JsonElement Json, Address Address, Hash256[] Topics);
    private sealed record Subscription(string Id, bool Logs, HashSet<Address>? Addresses, List<HashSet<Hash256>?> Topics)
    {
        internal long StartSequence { get; set; }
        internal bool Active { get; set; }
    }

    private sealed class Client
    {
        private readonly VerifiedSubscriptions _owner;
        private readonly WebSocket _socket;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Subscription> _subscriptions = [];
        private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _space;
        private int _closing;
        private int _queuedBytes;

        internal Client(VerifiedSubscriptions owner, WebSocket socket, CancellationToken requestAborted)
        {
            _owner = owner;
            _socket = socket;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            _token = _cancellation.Token;
        }

        internal void Terminate()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            try
            {
                _socket.Abort();
                _cancellation.Cancel();
            }
            finally { _terminated.TrySetResult(); }
        }

        internal async Task RunAsync()
        {
            Task sender = SendAsync();
            try { await ReceiveAsync(); }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
                if (Volatile.Read(ref _closing) == 0 && !_cancellation.IsCancellationRequested)
                    _owner._logger.LogDebug(exception, "Subscription connection closed");
            }
            finally
            {
                Terminate();
                _outgoing.Writer.TryComplete();
                await _terminated.Task;
                await sender;
                _cancellation.Dispose();
            }
        }

        private async Task SendAsync()
        {
            try
            {
                await foreach (byte[] message in _outgoing.Reader.ReadAllAsync(_token))
                {
                    TaskCompletionSource? space;
                    lock (_lock)
                    {
                        _queuedBytes -= message.Length;
                        space = _space;
                        _space = null;
                    }
                    space?.TrySetResult();
                    await _socket.SendAsync(message, WebSocketMessageType.Text, true, _token);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
                if (Volatile.Read(ref _closing) == 0 && !_cancellation.IsCancellationRequested)
                    _owner._logger.LogDebug(exception, "Subscription sender closed");
                Terminate();
            }
            catch (Exception exception)
            {
                _owner._logger.LogWarning(exception, "Subscription sender failed");
                Terminate();
            }
        }

        private async Task ReceiveAsync()
        {
            byte[] buffer = new byte[4096];
            using MemoryStream input = new();
            while (!_cancellation.IsCancellationRequested)
            {
                input.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text || input.Length + received.Count > RpcEndpoint.StandardRequestBodySize)
                    {
                        _owner._logger.LogWarning("Closing subscription connection after a non-text or oversized request");
                        return;
                    }
                    input.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                JsonDocument document;
                try { document = JsonDocument.Parse(input.ToArray()); }
                catch (JsonException)
                {
                    if (!await EnqueueResponseAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }))) return;
                    continue;
                }
                using (document)
                using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90), _owner._clock))
                using (CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _token))
                {
                    JsonElement request = document.RootElement;
                    byte[]? response;
                    if (request.ValueKind == JsonValueKind.Array)
                    {
                        int count = request.GetArrayLength();
                        if (count is 0 or > 32)
                            response = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32600, message = "Batch must contain between 1 and 32 requests" } });
                        else
                        {
                            using MemoryStream batch = new();
                            using Utf8JsonWriter writer = new(batch);
                            writer.WriteStartArray();
                            bool hasResponses = false;
                            foreach (JsonElement item in request.EnumerateArray())
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                object? result = await RpcEndpoint.InvokeAsync(item, InvokeAsync, _owner._logger, cancellation.Token);
                                if (result is null) continue;
                                JsonSerializer.Serialize(writer, result);
                                writer.Flush();
                                if (batch.Length >= MaxMessageBytes) { Terminate(); return; }
                                hasResponses = true;
                            }
                            writer.WriteEndArray();
                            writer.Flush();
                            response = hasResponses ? batch.ToArray() : null;
                        }
                    }
                    else
                    {
                        object? result = await RpcEndpoint.InvokeAsync(request, InvokeAsync, _owner._logger, cancellation.Token);
                        response = result is null ? null : JsonSerializer.SerializeToUtf8Bytes(result);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (response is not null && !await EnqueueResponseAsync(response)) return;
                    if (response is null) lock (_lock) ActivatePending();
                }
            }
        }

        private Task<object> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
        {
            if (method is not ("eth_subscribe" or "eth_unsubscribe")) return _owner._rpc.InvokeAsync(method, parameters, cancellationToken);
            if (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() is < 1 or > 2 || parameters[0].ValueKind != JsonValueKind.String)
                throw new RpcException(-32602, "Invalid subscription parameters.");
            string name = parameters[0].GetString()!;
            lock (_lock)
            {
                if (method == "eth_unsubscribe")
                {
                    if (parameters.GetArrayLength() != 1) throw new RpcException(-32602, "Invalid unsubscribe parameters.");
                    return Task.FromResult<object>(_subscriptions.Remove(name));
                }
                if (name is not ("newHeads" or "logs")) throw new RpcException(-32602, "Only authenticated newHeads and logs subscriptions are available.");
                HashSet<Address>? addresses = null;
                List<HashSet<Hash256>?> topics = [];
                if (parameters.GetArrayLength() == 2)
                {
                    if (name != "logs" || parameters[1].ValueKind != JsonValueKind.Object) throw new RpcException(-32602, "Invalid subscription filter.");
                    foreach (JsonProperty property in parameters[1].EnumerateObject())
                    {
                        switch (property.Name)
                        {
                            case "address": addresses = property.Value.ValueKind == JsonValueKind.Null ? null : VerifiedRpc.ParseAddresses(property.Value); break;
                            case "topics": topics = property.Value.ValueKind == JsonValueKind.Null ? [] : VerifiedRpc.ParseTopics(property.Value); break;
                            default: throw new RpcException(-32602, "Subscription filters accept only address and topics.");
                        }
                    }
                }
                if (_subscriptions.Count >= MaxSubscriptions) throw new RpcException(-32000, "Connection reached its 16-subscription limit.");
                string id = "0x" + Guid.NewGuid().ToString("N");
                _subscriptions.Add(id, new(id, name == "logs", addresses, topics));
                return Task.FromResult<object>(id);
            }
        }

        internal void Interests(long sequence, ref bool heads, ref bool logs)
        {
            if (Volatile.Read(ref _closing) != 0) return;
            lock (_lock)
            {
                foreach (Subscription subscription in _subscriptions.Values)
                {
                    if (!subscription.Active || sequence <= subscription.StartSequence) continue;
                    if (subscription.Logs) logs = true;
                    else heads = true;
                }
            }
        }

        internal async Task DeliverAsync(IReadOnlyList<Snapshot> removed, IReadOnlyList<Snapshot> added,
            Snapshot? tip, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), _owner._clock);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _token, cancellationToken);
            try
            {
                foreach (Snapshot snapshot in removed) await LogsAsync(snapshot, removed: true, cancellation.Token);
                foreach (Snapshot snapshot in added) await LogsAsync(snapshot, removed: false, cancellation.Token);
                if (tip is not null && tip.Header.ValueKind != JsonValueKind.Undefined)
                    await NotifyAsync(tip.Sequence, tip.Header, null, removed: false, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Terminate(); }
        }

        private async Task LogsAsync(Snapshot snapshot, bool removed, CancellationToken cancellationToken)
        {
            if (snapshot.Logs is null) return;
            foreach (LogData log in snapshot.Logs) await NotifyAsync(snapshot.Sequence, log.Json, log, removed, cancellationToken);
        }

        private async Task NotifyAsync(long sequence, JsonElement result, LogData? log, bool removed, CancellationToken cancellationToken)
        {
            bool logs = log is not null;
            Subscription[] subscriptions;
            lock (_lock) subscriptions = [.. _subscriptions.Values];
            foreach (Subscription subscription in subscriptions)
            {
                bool eligible;
                lock (_lock) eligible = subscription.Active && subscription.Logs == logs && sequence > subscription.StartSequence;
                if (!eligible || log is { } entry && !Matches(entry, subscription)) continue;
                byte[] message = Notification(subscription.Id, result, logs, removed);
                if (!await EnqueueAsync(message, subscription.Id, cancellationToken)) return;
            }
        }

        private static bool Matches(LogData log, Subscription subscription)
        {
            if (subscription.Addresses is not null && !subscription.Addresses.Contains(log.Address)) return false;
            if (log.Topics.Length < subscription.Topics.Count) return false;
            for (int i = 0; i < subscription.Topics.Count; i++)
                if (subscription.Topics[i] is { } choices && !choices.Contains(log.Topics[i])) return false;
            return true;
        }

        private async Task<bool> EnqueueResponseAsync(byte[] message)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), _owner._clock);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _token);
            try { return await EnqueueAsync(message, null, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Terminate(); return false; }
        }

        private async Task<bool> EnqueueAsync(byte[] message, string? subscriptionId, CancellationToken cancellationToken)
        {
            if (message.Length > MaxMessageBytes) { Terminate(); return false; }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task space;
                lock (_lock)
                {
                    if (Volatile.Read(ref _closing) != 0) return false;
                    if (subscriptionId is not null && !_subscriptions.ContainsKey(subscriptionId)) return true;
                    if (_queuedBytes + message.Length <= MaxQueuedBytes && _outgoing.Writer.TryWrite(message))
                    {
                        _queuedBytes += message.Length;
                        if (subscriptionId is null) ActivatePending();
                        return true;
                    }
                    _space ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    space = _space.Task;
                }
                await space.WaitAsync(cancellationToken);
            }
        }

        private void ActivatePending()
        {
            foreach (Subscription subscription in _subscriptions.Values)
            {
                if (subscription.Active) continue;
                subscription.StartSequence = Volatile.Read(ref _owner._sequence);
                subscription.Active = true;
            }
        }

        private static byte[] Notification(string id, JsonElement result, bool logs, bool removed)
        {
            using MemoryStream output = new();
            using (Utf8JsonWriter writer = new(output))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", "eth_subscription");
                writer.WriteStartObject("params");
                writer.WriteString("subscription", id);
                writer.WritePropertyName("result");
                if (!logs) result.WriteTo(writer);
                else
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in result.EnumerateObject())
                        if (property.Name != "removed") property.WriteTo(writer);
                    writer.WriteBoolean("removed", removed);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return output.ToArray();
        }
    }
}
