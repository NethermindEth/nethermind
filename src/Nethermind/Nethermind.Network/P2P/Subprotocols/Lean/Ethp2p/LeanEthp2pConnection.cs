// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>One authenticated QUIC connection under the EIP-8437 ethp2p application profile: requested retrieval and broadcast.</summary>
/// <remarks>
/// <para>
/// One dispatcher routes the stream selectors <c>0x01</c> BCAST, <c>0x02</c> SESS and <c>0x03</c> CHUNK of the broadcast
/// framework and <c>0x10</c>, <c>0x11</c> of retrieval. Each endpoint opens one retrieval control stream (<c>0x10</c>, Status
/// first) for AnnounceObjects, GetObjects, GetChunks, Cancel and GetTransactions, so requests keep their consecutive IDs and
/// order. Every response travels on its own stream (<c>0x11 || U64(request_id)</c>) finished by FIN after its terminal
/// message, so response streams advance independently. The shared <see cref="LeanObjectTransport"/> keeps object commitments,
/// validation, credit and deadlines; this class only maps its messages onto streams.
/// </para>
/// <para>
/// Bounds: the connection grants <see cref="LeanEthp2pProtocol.MaxInboundStreams"/> inbound streams and receive windows
/// that reserve room for both control streams; partial prefixes, envelopes and control frames expire after
/// <see cref="LeanProtocol.MaxRequestIdle"/>, and identified response streams follow their request's deadlines.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed partial class LeanEthp2pConnection : ILeanLink, ILeanEthp2pStreamHandler, IAsyncDisposable
{
    /// <summary>Responses waiting for or holding a stream; beyond it a response is dropped and its request expires.</summary>
    internal const int MaxOutgoingResponses = 2 * LeanProtocol.MaxRequestsPerPeer;

    /// <summary>Queued control frames the peer has not drained; beyond it the peer is not reading and the binding closes.</summary>
    internal const int MaxControlBacklogBytes = 4 * 1024 * 1024;

    private const int ReadBufferBytes = 16 * 1024;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(250);

    private readonly QuicConnection _quic;
    private readonly LeanObjectTransport _transport;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _control = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<ulong, OutgoingResponse> _responses = [];
    private readonly List<IncomingStream> _incoming = [];
    private readonly long _established;
    private IncomingStream? _controlStream;
    private LeanPeer? _peer;
    private bool _statusReceived;
    private long _controlBacklog;
    private ulong _lastResponseId;
    private int _closeStarted;
    private int _activeStreams;

    public LeanEthp2pConnection(QuicConnection quic, PublicKey remoteKey, bool outbound, LeanObjectTransport transport, LeanBroadcastEngine? engine,
        ILogManager logManager, TimeProvider? timeProvider = null)
    {
        _quic = quic;
        _engine = engine;
        RemoteKey = remoteKey;
        Outbound = outbound;
        _transport = transport;
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logManager.GetClassLogger<LeanEthp2pConnection>();
        _established = _clock.GetTimestamp();
        Description = $"ethp2p {(outbound ? "to" : "from")} {remoteKey.ToShortString()} at {quic.RemoteEndPoint}";
    }

    /// <summary>The authenticated node key, also the peer's RLPx identity.</summary>
    public PublicKey RemoteKey { get; }

    public bool Outbound { get; }

    public IPEndPoint RemoteEndPoint => _quic.RemoteEndPoint;

    public string Description { get; }

    /// <summary>Whether the peer's compatible Status was accepted.</summary>
    public bool IsEstablished => Volatile.Read(ref _peer) is not null;

    /// <summary>Completes once the connection is closed for any reason.</summary>
    public Task Closed => _closed.Task;

    internal LeanPeer? Peer => Volatile.Read(ref _peer);

    /// <summary>Runs the control writer, the stream acceptor and deadline sweeps until the connection closes.</summary>
    public async Task RunAsync()
    {
        if (_transport.CreateStatus() is not { } status)
        {
            Close(LeanEthp2pProtocol.NoError, "genesis is unavailable");
        }
        else
        {
            Task writer = WriteControlAsync(status);
            Task bcast = WriteBcastAsync();
            Task acceptor = AcceptStreamsAsync();
            Task sweeper = SweepAsync();
            await Task.WhenAll(writer, bcast, acceptor, sweeper).ConfigureAwait(false);
        }
        await _closed.Task.ConfigureAwait(false);
    }

    /// <summary>Terminates the binding on this connection, releasing its requests in the shared transport.</summary>
    public void Close(long errorCode, string reason)
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        if (_logger.IsDebug) _logger.Debug($"{Description} closing: {reason}");
        if (Volatile.Read(ref _peer) is { } peer) _transport.Remove(peer);
        _engine?.RemovePeer(this);
        _control.Writer.TryComplete();
        _bcast.Writer.TryComplete();
        _closing.Cancel();
        _ = CloseQuicAsync(errorCode);
    }

    private async Task CloseQuicAsync(long errorCode)
    {
        try
        {
            await _quic.CloseAsync(errorCode).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is QuicException or ObjectDisposedException or OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"{Description} close: {exception.Message}");
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    private void Violation(string reason)
    {
        if (_logger.IsDebug) _logger.Debug($"{Description} lean/1 violation: {reason}");
        LeanMetrics.Record(0, LeanEvent.Violation);
        Close(LeanEthp2pProtocol.ProtocolViolationError, reason);
    }

    void ILeanLink.Penalize(string reason) => Close(LeanEthp2pProtocol.ProtocolViolationError, reason);

    void ILeanLink.Send(LeanMessage message)
    {
        if (_closing.IsCancellationRequested) return;
        switch (message)
        {
            case ObjectsMessage or CompleteMessage or TransactionsMessage:
                _ = SendTerminalAsync(LeanEthp2pProtocol.RequestIdOf(message), message);
                break;
            case AnnounceObjectsMessage or GetObjectsMessage or GetChunksMessage or CancelMessage or GetTransactionsMessage:
                EnqueueControl(message);
                break;
            default:
                throw new ArgumentException($"{message.GetType().Name} is not sent by the transport", nameof(message));
        }
    }

    private void EnqueueControl(LeanMessage message)
    {
        byte[] frame = LeanEthp2pProtocol.Frame(message.PacketType, LeanEthp2pProtocol.Encode(message));
        if (Interlocked.Add(ref _controlBacklog, frame.Length) > MaxControlBacklogBytes)
        {
            Close(LeanEthp2pProtocol.NoError, "control stream is not drained");
            return;
        }
        _control.Writer.TryWrite(frame);
    }

    private async Task WriteControlAsync(LeanStatusMessage status)
    {
        CancellationToken token = _closing.Token;
        try
        {
            // Opened before any response stream, so it always holds one of the peer's stream allowances.
            await using QuicStream stream = await _quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token).ConfigureAwait(false);
            byte[] statusFrame = LeanEthp2pProtocol.Frame(LeanMessageCode.Status, LeanEthp2pProtocol.Encode(status));
            await stream.WriteAsync((byte[])[LeanEthp2pProtocol.ControlStream, .. statusFrame], token).ConfigureAwait(false);
            LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.ControlStream, 1 + statusFrame.Length);
            await foreach (byte[] frame in _control.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await stream.WriteAsync(frame, token).ConfigureAwait(false);
                LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.ControlStream, frame.Length);
                Interlocked.Add(ref _controlBacklog, -frame.Length);
            }
        }
        catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested) Close(LeanEthp2pProtocol.NoError, $"control stream write failed: {exception.Message}");
        }
    }

    private async Task AcceptStreamsAsync()
    {
        CancellationToken token = _closing.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                QuicStream stream = await _quic.AcceptInboundStreamAsync(token).ConfigureAwait(false);
                if (stream.Type != QuicStreamType.Unidirectional)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    Violation("bidirectional stream");
                    return;
                }
                IncomingStream incoming = new(stream, new LeanEthp2pStreamReader(this), _clock.GetTimestamp());
                lock (_lock) _incoming.Add(incoming);
                _ = ReadStreamAsync(incoming);
            }
        }
        catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested) Close(LeanEthp2pProtocol.NoError, $"connection ended: {exception.Message}");
        }
    }

    private async Task ReadStreamAsync(IncomingStream incoming)
    {
        Interlocked.Increment(ref _activeStreams);
        LeanEthp2pStreamReader reader = incoming.Reader;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, incoming.Stop.Token);
        try
        {
            while (true)
            {
                int read = await incoming.Stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    Finish(reader);
                    break;
                }
                LeanMetrics.Ethp2pBytes(outbound: false, reader.Type ?? buffer[0], read);
                try
                {
                    reader.Feed(buffer.AsSpan(0, read));
                }
                catch (LeanEthp2pOtherStreamException other)
                {
                    // Framework streams keep their own deadlines, from this stream's acceptance onwards.
                    lock (_lock) _incoming.Remove(incoming);
                    await ReadFrameworkStreamAsync(incoming.Stream, other.Selector, buffer.AsSpan(1, read - 1).ToArray(), incoming.Accepted)
                        .ConfigureAwait(false);
                    break;
                }
                if (!Track(incoming)) break;
                if (reader.IsDiscarding)
                {
                    // A prefix for a retired request is dropped without payload work.
                    incoming.Stream.Abort(QuicAbortDirection.Read, LeanEthp2pProtocol.RequestCancelledError);
                    break;
                }
            }
        }
        catch (LeanEthp2pViolationException exception)
        {
            Violation(exception.Message);
        }
        catch (QuicException exception) when (exception.QuicError == QuicError.StreamAborted)
        {
            OnReset(reader);
        }
        catch (OperationCanceledException) when (incoming.Stop.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            // Expired: the prefix took too long or the request retired.
            incoming.Stream.Abort(QuicAbortDirection.Read, LeanEthp2pProtocol.RequestCancelledError);
        }
        catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested) Close(LeanEthp2pProtocol.NoError, $"stream read failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"{Description} stream handling failed", exception);
            Close(LeanEthp2pProtocol.NoError, $"stream handling failed: {exception.Message}");
        }
        finally
        {
            reader.Abandon();
            ArrayPool<byte>.Shared.Return(buffer);
            lock (_lock) _incoming.Remove(incoming);
            await DisposeQuietly(incoming.Stream).ConfigureAwait(false);
            Interlocked.Decrement(ref _activeStreams);
        }
    }

    /// <summary>Records the stream's role and partial-unit start; false when it was a second control stream.</summary>
    private bool Track(IncomingStream incoming)
    {
        LeanEthp2pStreamReader reader = incoming.Reader;
        bool secondControl = false;
        lock (_lock)
        {
            if (reader.Type == LeanEthp2pProtocol.ControlStream && _controlStream != incoming)
            {
                if (_controlStream is null) _controlStream = incoming;
                else secondControl = true;
            }
            // Only the first byte of a unit starts its deadline; trickled bytes never refresh it.
            if (!reader.InPartialUnit) incoming.PartialSince = 0;
            else if (incoming.PartialSince == 0) incoming.PartialSince = _clock.GetTimestamp();
        }
        if (secondControl) Violation("second control stream");
        return !secondControl;
    }

    private void Finish(LeanEthp2pStreamReader reader)
    {
        LeanEthp2pStreamEnd end = reader.Finish();
        if (Volatile.Read(ref _peer) is not { } peer || !reader.Identified) return;
        switch (end)
        {
            case LeanEthp2pStreamEnd.Complete:
                switch (reader.Terminal)
                {
                    case ObjectsMessage objects: _transport.OnObjects(peer, objects); break;
                    case CompleteMessage complete: _transport.OnComplete(peer, complete); break;
                    case TransactionsMessage transactions: _transport.OnTransactions(peer, transactions); break;
                }
                break;
            case LeanEthp2pStreamEnd.Incomplete:
                _transport.OnResponseAborted(peer, reader.RequestId);
                break;
        }
    }

    private void OnReset(LeanEthp2pStreamReader reader)
    {
        if (reader.Type == LeanEthp2pProtocol.ControlStream)
        {
            Close(LeanEthp2pProtocol.ProtocolViolationError, "control stream reset");
            return;
        }
        // A reset neither acknowledges admission nor invalidates retained chunks; it releases the request slot.
        if (reader.Identified && Volatile.Read(ref _peer) is { } peer) _transport.OnResponseAborted(peer, reader.RequestId);
    }

    LeanResponseTarget ILeanEthp2pStreamHandler.OpenResponse(ulong requestId) =>
        Volatile.Read(ref _peer) is { } peer ? _transport.OpenResponseStream(peer, requestId) : LeanResponseTarget.Invalid;

    void ILeanEthp2pStreamHandler.OnControl(LeanMessage message)
    {
        if (message is LeanStatusMessage status)
        {
            OnStatus(status);
            return;
        }
        // An incompatible Status closes the connection; anything still in flight is ignored.
        if (Volatile.Read(ref _peer) is not { } peer) return;
        switch (message)
        {
            case AnnounceObjectsMessage announce: _transport.OnAnnounce(peer, announce); break;
            case GetObjectsMessage getObjects: _transport.OnGetObjects(peer, getObjects); break;
            case GetChunksMessage getChunks: _transport.OnGetChunks(peer, getChunks); break;
            case CancelMessage cancel: _transport.OnCancel(peer, cancel); break;
            case GetTransactionsMessage getTransactions: _transport.OnGetTransactions(peer, getTransactions); break;
        }
    }

    void ILeanEthp2pStreamHandler.OnChunk(in LeanChunkView chunk)
    {
        if (Volatile.Read(ref _peer) is { } peer) _transport.OnChunk(peer, chunk);
    }

    private void OnStatus(LeanStatusMessage status)
    {
        lock (_lock)
        {
            if (_statusReceived) throw new LeanEthp2pViolationException("second Status");
            _statusReceived = true;
        }
        LeanPeer? peer = _transport.Accept(this, status, RemoteKey);
        if (peer is null)
        {
            // Incompatibility is not misbehaviour: RLPx remains available.
            Close(LeanEthp2pProtocol.NoError, "incompatible Status");
            return;
        }
        Volatile.Write(ref _peer, peer);
        if (_closing.IsCancellationRequested) _transport.Remove(peer);
        else
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 established over {Description}");
            TryBecomeReady();
        }
    }

    async ValueTask<bool> ILeanLink.SendChunkAsync(ChunkMessage message, CancellationToken cancellationToken)
    {
        if (GetResponse(message.RequestId) is not { } response) return false;
        // Waiting for the stream is cancellable; a write once started finishes, as a Chunk being written may after Cancel.
        await response.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.Finished) return false;
            return await WriteAsync(response, LeanMessageCode.Chunk, LeanEthp2pProtocol.Encode(message), finish: false).ConfigureAwait(false);
        }
        finally
        {
            response.Lock.Release();
        }
    }

    private async Task SendTerminalAsync(ulong requestId, LeanMessage message)
    {
        if (GetResponse(requestId) is not { } response)
        {
            if (_logger.IsDebug) _logger.Debug($"{Description} dropped the response to request {requestId}: too many pending responses");
            return;
        }
        try
        {
            await response.Lock.WaitAsync(_closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (!response.Finished) await WriteAsync(response, message.PacketType, LeanEthp2pProtocol.Encode(message), finish: true).ConfigureAwait(false);
        }
        finally
        {
            response.Lock.Release();
        }
    }

    /// <summary>The response to an incoming request, created by its first frame; null once it retired or was dropped.</summary>
    /// <remarks>
    /// The transport writes the first frame of each response while it handles the request, and requests arrive in consecutive
    /// ID order on the control stream; so an ID at or below the last one seen without an entry has retired, and a new stream
    /// for it would be a duplicate response.
    /// </remarks>
    private OutgoingResponse? GetResponse(ulong requestId)
    {
        lock (_lock)
        {
            if (_responses.TryGetValue(requestId, out OutgoingResponse? response)) return response;
            if (_closing.IsCancellationRequested || requestId <= _lastResponseId) return null;
            _lastResponseId = requestId;
            if (_responses.Count >= MaxOutgoingResponses) return null;
            long now = _clock.GetTimestamp();
            response = new OutgoingResponse(requestId, now);
            _responses.Add(requestId, response);
            return response;
        }
    }

    /// <summary>Writes one frame, opening the stream with its prefix first; on failure the stream is reset.</summary>
    /// <remarks>Called with the response's lock held. A terminal frame carries FIN, which retires the stream.</remarks>
    private async Task<bool> WriteAsync(OutgoingResponse response, int messageId, byte[] payload, bool finish)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        deadline.CancelAfter(LeanProtocol.MaxRequestIdle);
        try
        {
            // Opening waits for the peer's stream allowance, which it replenishes only as its bounded state is released.
            response.Stream ??= await _quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token).ConfigureAwait(false);
            byte[] frame = LeanEthp2pProtocol.Frame(messageId, payload, !response.Opened, response.RequestId);
            await response.Stream.WriteAsync(frame, finish, deadline.Token).ConfigureAwait(false);
            LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.ResponseStream, frame.Length);
            response.Opened = true;
            response.LastActivity = _clock.GetTimestamp();
            if (finish) await Retire(response, abort: false).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
        {
            if (_logger.IsTrace) _logger.Trace($"{Description} response {response.RequestId} stopped: {exception.Message}");
            await Retire(response, abort: true).ConfigureAwait(false);
            return false;
        }
    }

    private async Task Retire(OutgoingResponse response, bool abort)
    {
        response.Finished = true;
        lock (_lock)
        {
            if (_responses.TryGetValue(response.RequestId, out OutgoingResponse? current) && current == response) _responses.Remove(response.RequestId);
        }
        if (response.Stream is not { } stream) return;
        if (abort)
        {
            try { stream.Abort(QuicAbortDirection.Write, LeanEthp2pProtocol.RequestCancelledError); }
            catch (ObjectDisposedException) { }
        }
        await DisposeQuietly(stream).ConfigureAwait(false);
    }

    /// <summary>Enforces Status, prefix, control-frame and response deadlines on a timer, independent of incoming bytes.</summary>
    private async Task SweepAsync()
    {
        using PeriodicTimer timer = new(SweepInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_closing.Token).ConfigureAwait(false)) Sweep();
        }
        catch (OperationCanceledException) { }
    }

    internal void Sweep()
    {
        long now = _clock.GetTimestamp();
        if ((!_statusReceived || !_handshakeReceived) && _clock.GetElapsedTime(_established, now) >= LeanProtocol.MaxRequestIdle)
        {
            Close(LeanEthp2pProtocol.NoError, "no Status and BCAST handshake within MAX_REQUEST_IDLE_SECONDS");
            return;
        }
        LeanPeer? peer = Volatile.Read(ref _peer);
        List<IncomingStream>? expired = null;
        List<OutgoingResponse>? stale = null;
        string? violation = null;
        lock (_lock)
        {
            foreach (IncomingStream incoming in _incoming)
            {
                LeanEthp2pStreamReader reader = incoming.Reader;
                if (incoming == _controlStream)
                {
                    if (incoming.PartialSince != 0 && _clock.GetElapsedTime(incoming.PartialSince, now) >= LeanProtocol.MaxRequestIdle)
                        violation = "control frame incomplete for MAX_REQUEST_IDLE_SECONDS";
                }
                else if (!reader.Identified)
                {
                    if (!reader.IsDiscarding && _clock.GetElapsedTime(incoming.Accepted, now) >= LeanProtocol.MaxRequestIdle) (expired ??= []).Add(incoming);
                }
                else if (peer is null || !_transport.IsLive(peer, reader.RequestId))
                {
                    // The request retired at its own deadline, so its stream is cancelled.
                    (expired ??= []).Add(incoming);
                }
            }
            foreach (OutgoingResponse response in _responses.Values)
                if (response.Lock.CurrentCount == 1 && (_clock.GetElapsedTime(response.Created, now) >= LeanProtocol.MaxRequestAge
                    || _clock.GetElapsedTime(response.LastActivity, now) >= LeanProtocol.MaxRequestIdle))
                    (stale ??= []).Add(response);
        }
        if (violation is not null)
        {
            Violation(violation);
            return;
        }
        if (expired is not null)
            foreach (IncomingStream incoming in expired) incoming.Stop.Cancel();
        if (stale is not null)
            foreach (OutgoingResponse response in stale) _ = AbortStale(response);
    }

    /// <summary>Resets a response the responder stopped serving without a terminal message, such as at its request deadline.</summary>
    private async Task AbortStale(OutgoingResponse response)
    {
        if (!response.Lock.Wait(0)) return;
        try
        {
            if (!response.Finished) await Retire(response, abort: true).ConfigureAwait(false);
        }
        finally
        {
            response.Lock.Release();
        }
    }

    private static async ValueTask DisposeQuietly(QuicStream stream)
    {
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is QuicException or ObjectDisposedException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Close(LeanEthp2pProtocol.NoError, "disposed");
        await _closed.Task.ConfigureAwait(false);
        // Disposing the connection aborts its streams, so their readers unwind before the shared token goes away.
        await _quic.DisposeAsync().ConfigureAwait(false);
        using CancellationTokenSource drain = new(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref _activeStreams) > 0 && !drain.IsCancellationRequested) await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        if (Volatile.Read(ref _activeStreams) == 0) _closing.Dispose();
    }

    public override string ToString() => Description;

    private sealed class IncomingStream(QuicStream stream, LeanEthp2pStreamReader reader, long accepted)
    {
        public QuicStream Stream { get; } = stream;
        public LeanEthp2pStreamReader Reader { get; } = reader;
        public long Accepted { get; } = accepted;
        public long PartialSince { get; set; }
        /// <summary>Cancels the read at an expired deadline; never disposed, so a late sweep can always cancel it.</summary>
        public CancellationTokenSource Stop { get; } = new();
    }

    private sealed class OutgoingResponse(ulong requestId, long created)
    {
        public ulong RequestId { get; } = requestId;
        public long Created { get; } = created;
        public long LastActivity { get; set; } = created;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public QuicStream? Stream { get; set; }
        public bool Opened { get; set; }
        public bool Finished { get; set; }
    }
}
