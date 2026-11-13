// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient.Rpc;

/// <summary>Streams authenticated execution heads and logs over bounded, connection-owned WebSocket subscriptions.</summary>
internal sealed class VerifiedSubscriptions : IAsyncDisposable
{
    internal const int MaxClients = 16;
    internal const int MaxSubscriptions = 16;
    internal const int MaxHistory = 32;
    internal const int MaxMessageBytes = 256 * 1024;
    internal const int MaxQueuedBytes = 1024 * 1024;
    // A JSON string can reserve six escaped bytes per character before committing its actual UTF-8 size.
    private const int JsonReservationSlack = 6 * MaxMessageBytes;
    private const int MaxHistoryBytes = 16 * 1024 * 1024;
    private readonly IExecutionStateSource _source;
    private readonly VerifiedRpc _rpc;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly LightClientMetrics? _metrics;
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
        TimeProvider? clock = null, LightClientMetrics? metrics = null)
    {
        _source = source;
        _rpc = rpc;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _metrics = metrics;
        _publisher = PublishLoopAsync();
    }

    /// <summary>Enqueues the final authenticated latest head without waiting for execution data or consumers.</summary>
    internal void PublishHead(VerifiedHead head)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        if (!_heads.Writer.TryWrite((sequence, head)) && !_stopping.IsCancellationRequested)
        {
            Volatile.Write(ref _overflow, new(sequence, head));
            CloseClients((WebSocketCloseStatus)1013, "Authenticated head queue overflowed; reconnect and resubscribe.");
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
            if (stopping) client.Close(WebSocketCloseStatus.EndpointUnavailable, "Light client is stopping.");
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
            _heads.Writer.TryComplete();
            CloseClients(WebSocketCloseStatus.EndpointUnavailable, "Light client is stopping.");
            await _stopping.CancelAsync();
            await _publisher;
            Task connections = Task.WhenAll(completions);
            try { await connections.WaitAsync(TimeSpan.FromSeconds(3), _clock); }
            catch (TimeoutException)
            {
                _logger.LogDebug("Aborting subscription requests that did not finish during shutdown");
                for (int i = 0; i < contexts.Length; i++)
                    if (!completions[i].IsCompleted) contexts[i].Abort();
                await connections;
            }
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

    private void CloseClients(WebSocketCloseStatus status, string reason)
    {
        Client[] clients = Clients();
        if (clients.Length != 0)
        {
            if (status == WebSocketCloseStatus.EndpointUnavailable) _logger.LogInformation("Closing verified subscriptions: {Reason}", reason);
            else _logger.LogWarning("Closing verified subscriptions: {Reason}", reason);
        }
        foreach (Client client in clients) client.Close(status, reason);
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
                        _history.Add(new(overflow.Head, overflow.Sequence, default, null));
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
                        _history.Add(new(head, sequence, default, null));
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
                    CloseClients((WebSocketCloseStatus)1013, "Verified history or execution data is unavailable; reconnect and resubscribe.");
                    _history.Clear();
                    _history.Add(new(head, sequence, default, null));
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
        for (int i = branch.Count - 1; i >= 0; i--)
        {
            BlockHeader header = branch[i];
            VerifiedHead anchor = new(header.Hash == head.BlockHash ? head.Slot : 0, header.Number, header.Hash!, header.StateRoot!);
            JsonElement rendered = needsHeads && i == 0 ? _rpc.GetHeader(header) : default;
            added.Add(new(anchor, sequence, rendered, needsLogs ? header : null));
        }
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
        HashSet<Hash256> canonical = [];
        foreach (Snapshot snapshot in _history) canonical.Add(snapshot.Head.BlockHash);
        Delivery delivery = new(sequence, removed, added, tip, canonical);
        foreach (Client client in clients) client.Enqueue(delivery);
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

    private sealed record Snapshot(VerifiedHead Head, long Sequence, JsonElement Header, BlockHeader? ExecutionHeader);
    private sealed record RetainedLogs(Snapshot Snapshot, LogData[] Logs, int Bytes);
    private sealed record Publication(long Sequence, VerifiedHead Head);
    private sealed record Delivery(long Sequence, IReadOnlyList<Snapshot> Removed, IReadOnlyList<Snapshot> Added,
        Snapshot? Tip, IReadOnlySet<Hash256> Canonical);
    private readonly record struct LogData(JsonElement Json, Address Address, Hash256[] Topics);
    private static bool Matches(Address address, Hash256[] topics, Subscription subscription)
    {
        if (subscription.Addresses is not null && !subscription.Addresses.Contains(address)) return false;
        if (topics.Length < subscription.Topics.Count) return false;
        for (int i = 0; i < subscription.Topics.Count; i++)
            if (subscription.Topics[i] is { } choices && !choices.Contains(topics[i])) return false;
        return true;
    }
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
        private readonly Dictionary<Hash256, RetainedLogs> _logHistory = [];
        private int _historyBytes;
        private int _filterGeneration;
        private int _retainedFilterGeneration;
        private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Channel<Delivery> _deliveries = Channel.CreateBounded<Delivery>(
            new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationToken _token;
        private readonly CancellationTokenSource _workCancellation;
        private readonly TaskCompletionSource _closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _closeDeadline;
        private TaskCompletionSource? _space;
        private int _closing;
        private int _finished;
        private string? _closeReason;
        private WebSocketCloseStatus _closeStatus;
        private int _queuedBytes;

        internal Client(VerifiedSubscriptions owner, WebSocket socket, CancellationToken requestAborted)
        {
            _owner = owner;
            _socket = socket;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            _token = _cancellation.Token;
            _workCancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
        }

        internal void Terminate() => Finish(abort: true);

        private void Finish(bool abort)
        {
            lock (_lock)
            {
                if (Interlocked.Exchange(ref _finished, 1) != 0) return;
                Interlocked.Exchange(ref _closing, 1);
                if (abort) _socket.Abort();
                _cancellation.Cancel();
            }
        }

        internal void Close(WebSocketCloseStatus status, string reason)
        {
            lock (_lock)
            {
                if (Volatile.Read(ref _closing) != 0) return;
                _closeStatus = status;
                _closeReason = reason;
                _closeDeadline = new(TimeSpan.FromSeconds(2), _owner._clock);
                Volatile.Write(ref _closing, 1);
                _outgoing.Writer.TryComplete();
                _deliveries.Writer.TryComplete();
                _workCancellation.Cancel();
                _closeRequested.TrySetResult();
            }
        }

        internal void Enqueue(Delivery delivery)
        {
            if (Volatile.Read(ref _closing) != 0 || _deliveries.Writer.TryWrite(delivery)) return;
            _owner._logger.LogWarning("Closing this connection because its authenticated head queue overflowed");
            Close((WebSocketCloseStatus)1013, "Authenticated head queue overflowed; reconnect and resubscribe.");
        }

        internal async Task RunAsync()
        {
            Task sender = SendAsync();
            Task delivery = DeliverLoopAsync();
            Task receiver = ReceiveAsync();
            Task workers = Task.WhenAll(sender, delivery, receiver);
            try
            {
                await Task.WhenAny(receiver, _closeRequested.Task);
                if (Volatile.Read(ref _closing) == 0) Terminate();
                if (Volatile.Read(ref _finished) == 0)
                {
                    try { await workers.WaitAsync(_closeDeadline!.Token); }
                    catch (OperationCanceledException) when (_closeDeadline!.IsCancellationRequested)
                    {
                        _owner._logger.LogDebug("Aborting subscription transport after its close handshake timed out");
                        Terminate();
                    }
                    if (Volatile.Read(ref _finished) == 0) Finish(abort: false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
                if (!_cancellation.IsCancellationRequested)
                    _owner._logger.LogDebug(exception, "Subscription connection closed");
                Terminate();
            }
            finally
            {
                if (Volatile.Read(ref _finished) == 0) Terminate();
                _outgoing.Writer.TryComplete();
                _deliveries.Writer.TryComplete();
                try { await workers; }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                {
                    if (!_cancellation.IsCancellationRequested)
                        _owner._logger.LogDebug(exception, "Subscription connection closed");
                }
                finally
                {
                    lock (_lock)
                    {
                        _workCancellation.Dispose();
                        _closeDeadline?.Dispose();
                        _cancellation.Dispose();
                    }
                }
            }
        }

        private async Task SendAsync()
        {
            try
            {
                await foreach (byte[] message in _outgoing.Reader.ReadAllAsync(_token))
                {
                    _token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _closing) != 0) break;
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
                if (Volatile.Read(ref _finished) == 0 && _closeReason is { } reason)
                    await _socket.CloseOutputAsync(_closeStatus, reason, _token);
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
                    if (Volatile.Read(ref _closing) != 0) continue;
                    if (received.MessageType != WebSocketMessageType.Text || input.Length + received.Count > RpcEndpoint.StandardRequestBodySize)
                    {
                        _owner._logger.LogWarning("Closing subscription connection after a non-text or oversized request");
                        return;
                    }
                    input.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                if (Volatile.Read(ref _closing) != 0) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(input.ToArray()); }
                catch (JsonException)
                {
                    if (!await EnqueueResponseAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }))
                        && Volatile.Read(ref _closing) == 0) return;
                    continue;
                }
                try
                {
                    using (document)
                    using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90), _owner._clock))
                    using (CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _workCancellation.Token))
                    {
                        JsonElement request = document.RootElement;
                        byte[]? response;
                        if (request.ValueKind == JsonValueKind.Array)
                        {
                            int count = request.GetArrayLength();
                            if (count == 0)
                                response = JsonSerializer.SerializeToUtf8Bytes(RpcEndpoint.Error(null, -32600, "Batch must contain at least one request"));
                            else
                            {
                                using BoundedJsonBuffer batch = new(MaxMessageBytes, JsonReservationSlack);
                                bool hasResponses = await RpcEndpoint.WriteBatchAsync(batch, MaxMessageBytes, request,
                                    InvokeAsync, _owner._logger, cancellation.Token, _owner._metrics);
                                response = hasResponses ? batch.WrittenMemory.ToArray() : null;
                            }
                        }
                        else
                        {
                            response = null;
                            await RpcEndpoint.InvokeAsync(request, InvokeAsync, _owner._logger, cancellation.Token, (result, _) =>
                            {
                                response = EncodeResponse(request, result, out bool unchanged);
                                if (!unchanged) DiscardPending();
                                return unchanged;
                            }, _owner._metrics);
                        }
                        _token.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref _closing) != 0) continue;
                        if (response is not null && !await EnqueueResponseAsync(response) && Volatile.Read(ref _closing) == 0) return;
                        if (response is null) lock (_lock) ActivatePending();
                    }
                }
                catch (OperationCanceledException) when (_workCancellation.IsCancellationRequested && !_token.IsCancellationRequested) { }
            }
        }

        private static byte[] EncodeResponse(JsonElement request, object response, out bool unchanged)
        {
            unchanged = true;
            using BoundedJsonBuffer output = new(MaxMessageBytes, JsonReservationSlack);
            try
            {
                using (Utf8JsonWriter writer = new(output)) JsonSerializer.Serialize(writer, response);
                return output.WrittenMemory.ToArray();
            }
            catch (RpcException)
            {
                unchanged = false;
                object? id = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out JsonElement value)
                    && value.ValueKind is JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String ? value : null;
                byte[] error = JsonSerializer.SerializeToUtf8Bytes(RpcEndpoint.Error(id, -32005,
                    "WebSocket response exceeds the 256 KiB limit; submit this request over HTTP."));
                return error.Length <= MaxMessageBytes ? error : JsonSerializer.SerializeToUtf8Bytes(RpcEndpoint.Error(null, -32005,
                    "WebSocket response identifier exceeds the 256 KiB limit; submit this request over HTTP."));
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
                    bool removed = _subscriptions.Remove(name, out Subscription? subscription);
                    if (removed && subscription!.Logs) _filterGeneration++;
                    return Task.FromResult<object>(removed);
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

        private int CollectLogFilters(long sequence, List<Subscription> filters)
        {
            lock (_lock)
            {
                foreach (Subscription subscription in _subscriptions.Values)
                    if (subscription.Active && subscription.Logs && sequence > subscription.StartSequence) filters.Add(subscription);
                return _filterGeneration;
            }
        }

        private async Task DeliverLoopAsync()
        {
            try
            {
                await foreach (Delivery delivery in _deliveries.Reader.ReadAllAsync(_workCancellation.Token))
                    await DeliverAsync(delivery);
            }
            catch (OperationCanceledException) when (_workCancellation.IsCancellationRequested) { }
        }

        private async Task DeliverAsync(Delivery delivery)
        {
            if (Volatile.Read(ref _closing) != 0) return;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90), _owner._clock);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _workCancellation.Token);
            try
            {
                List<Subscription> filters = [];
                int generation = CollectLogFilters(delivery.Sequence, filters);
                foreach (Snapshot snapshot in delivery.Removed)
                    if (_logHistory.TryGetValue(snapshot.Head.BlockHash, out RetainedLogs? previous))
                        await LogsAsync(previous.Snapshot, previous.Logs, removed: true, cancellation.Token);
                PruneLogs(filters, generation, delivery.Canonical);
                foreach (Snapshot snapshot in delivery.Added)
                {
                    if (filters.Count == 0 || snapshot.ExecutionHeader is null) continue;
                    JsonElement rendered = await _owner._rpc.GetSubscriptionLogsAsync(snapshot.ExecutionHeader, MatchesAnyLog, cancellation.Token);
                    int bytes = Size(rendered);
                    if (bytes > MaxHistoryBytes - _historyBytes)
                        throw new RpcException(-32005, "Connection log history exceeds its 16 MiB budget.");
                    LogData[] logs = ParseLogs(rendered)!;
                    _logHistory.Add(snapshot.Head.BlockHash, new(snapshot, logs, bytes));
                    _historyBytes += bytes;
                    await LogsAsync(snapshot, logs, removed: false, cancellation.Token);
                }
                if (delivery.Tip is { } tip && tip.Header.ValueKind != JsonValueKind.Undefined)
                    await NotifyAsync(tip.Sequence, tip.Header, null, removed: false, cancellation.Token);

                bool MatchesAnyLog(Address address, Hash256[] topics)
                {
                    foreach (Subscription filter in filters)
                        if (Matches(address, topics, filter)) return true;
                    return false;
                }
            }
            catch (OperationCanceledException) when (_workCancellation.IsCancellationRequested)
            {
                if (Volatile.Read(ref _closing) == 0) Terminate();
            }
            catch (Exception exception)
            {
                _owner._logger.LogWarning(exception, "Closing this connection because its authenticated log stream is unavailable");
                Close((WebSocketCloseStatus)1013, "Authenticated log data is unavailable or this connection exceeded its history budget.");
            }
        }

        private void PruneLogs(List<Subscription> filters, int generation, IReadOnlySet<Hash256> canonical)
        {
            foreach ((Hash256 hash, RetainedLogs previous) in _logHistory)
            {
                if (!canonical.Contains(hash) || filters.Count == 0)
                {
                    _logHistory.Remove(hash);
                    _historyBytes -= previous.Bytes;
                    continue;
                }
                if (generation == _retainedFilterGeneration) continue;
                List<LogData> retained = [];
                foreach (LogData log in previous.Logs)
                    foreach (Subscription filter in filters)
                        if (previous.Snapshot.Sequence > filter.StartSequence && Matches(log.Address, log.Topics, filter))
                        {
                            retained.Add(log);
                            break;
                        }
                if (retained.Count == previous.Logs.Length) continue;
                int bytes = 2;
                for (int i = 0; i < retained.Count; i++)
                {
                    LogData log = retained[i];
                    retained[i] = log with { Json = log.Json.Clone() };
                    bytes += Size(log.Json) + 1;
                }
                _logHistory[hash] = previous with { Logs = [.. retained], Bytes = bytes };
                _historyBytes += bytes - previous.Bytes;
            }
            _retainedFilterGeneration = generation;
        }

        private async Task LogsAsync(Snapshot snapshot, LogData[] logs, bool removed, CancellationToken cancellationToken)
        {
            foreach (LogData log in logs) await NotifyAsync(snapshot.Sequence, log.Json, log, removed, cancellationToken);
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
                if (!eligible || log is { } entry && !Matches(entry.Address, entry.Topics, subscription)) continue;
                byte[] message = Notification(subscription.Id, result, logs, removed);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), _owner._clock);
                using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _token, cancellationToken);
                try { if (!await EnqueueAsync(message, subscription.Id, cancellation.Token)) return; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    if (_token.IsCancellationRequested || cancellationToken.IsCancellationRequested) throw;
                    _owner._logger.LogWarning("Closing subscription connection after its notification queue exceeded the admission deadline");
                    Close((WebSocketCloseStatus)1013, "Notification queue exceeded its admission deadline; reconnect and resubscribe.");
                    return;
                }
            }
        }

        private async Task<bool> EnqueueResponseAsync(byte[] message)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), _owner._clock);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _workCancellation.Token);
            try { return await EnqueueAsync(message, null, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (_workCancellation.IsCancellationRequested)
                {
                    if (Volatile.Read(ref _closing) == 0) Terminate();
                }
                else
                {
                    _owner._logger.LogWarning("Closing subscription connection after its response queue exceeded the admission deadline");
                    Close((WebSocketCloseStatus)1013, "Response queue exceeded its admission deadline; reconnect and resubscribe.");
                }
                return false;
            }
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
                if (subscription.Logs) _filterGeneration++;
            }
        }

        private void DiscardPending()
        {
            lock (_lock)
                foreach ((string id, Subscription subscription) in _subscriptions)
                    if (!subscription.Active) _subscriptions.Remove(id);
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
