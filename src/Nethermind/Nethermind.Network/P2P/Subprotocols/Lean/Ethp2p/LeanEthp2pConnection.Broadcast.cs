// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Quic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

internal sealed partial class LeanEthp2pConnection : ILeanBroadcastPeer
{
    private static readonly TimeSpan UnknownShardDeferral = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeferralPoll = TimeSpan.FromMilliseconds(50);

    private readonly LeanBroadcastEngine? _engine;
    private readonly Channel<byte[]> _bcast = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _remoteChannels = new(StringComparer.Ordinal);
    private bool _bcastStreamSeen;
    private bool _handshakeReceived;
    private bool _ready;

    /// <summary>Opens the BCAST stream and sends the version-1 handshake with no channels, without waiting for the peer.</summary>
    /// <remarks>Subscriptions follow only once the peer's handshake and Status are both accepted.</remarks>
    private async Task WriteBcastAsync()
    {
        CancellationToken token = _closing.Token;
        try
        {
            await using QuicStream stream = await _quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token).ConfigureAwait(false);
            byte[] handshake = [LeanEthp2pProtocol.BcastStream, .. LeanBroadcastWire.Envelope(LeanBroadcastWire.Handshake())];
            await stream.WriteAsync(handshake, token).ConfigureAwait(false);
            LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.BcastStream, handshake.Length);
            await foreach (byte[] envelope in _bcast.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await stream.WriteAsync(envelope, token).ConfigureAwait(false);
                LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.BcastStream, envelope.Length);
            }
        }
        catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested) Close(LeanEthp2pProtocol.NoError, $"BCAST stream write failed: {exception.Message}");
        }
    }

    /// <summary>Routes a stream whose selector is not retrieval: BCAST, SESS or CHUNK.</summary>
    private async Task ReadFrameworkStreamAsync(QuicStream stream, byte selector, byte[] leftover, long accepted)
    {
        StreamInput input = new(stream, selector, leftover);
        // The first framework envelope expires within MAX_REQUEST_IDLE_SECONDS of stream acceptance.
        TimeSpan firstEnvelope = LeanProtocol.MaxRequestIdle - _clock.GetElapsedTime(accepted);
        try
        {
            switch (selector)
            {
                case LeanEthp2pProtocol.BcastStream:
                    await ReadBcastAsync(input, firstEnvelope).ConfigureAwait(false);
                    break;
                case LeanEthp2pProtocol.SessStream or LeanEthp2pProtocol.ChunkStream:
                    // Cross-stream order is not guaranteed: early streams are refused without a timing-only penalty.
                    if (_engine is null || !Volatile.Read(ref _ready))
                    {
                        Refuse(stream);
                        return;
                    }
                    if (selector == LeanEthp2pProtocol.SessStream) await ReadSessAsync(input, firstEnvelope).ConfigureAwait(false);
                    else await ReadChunkAsync(input, firstEnvelope).ConfigureAwait(false);
                    break;
                default:
                    Violation($"unknown stream type {selector}");
                    break;
            }
        }
        catch (LeanBroadcastFormatException exception)
        {
            Violation($"malformed broadcast frame: {exception.Message}");
        }
        catch (OperationCanceledException) when (!_closing.IsCancellationRequested)
        {
            // A framework envelope or shard that stalls past its deadline is dropped; the BCAST stream is a control stream.
            if (selector == LeanEthp2pProtocol.BcastStream) Violation("BCAST envelope incomplete for MAX_REQUEST_IDLE_SECONDS");
            else Refuse(stream);
        }
        catch (QuicException exception) when (exception.QuicError == QuicError.StreamAborted)
        {
            // A sender may cancel a shard it no longer needs to send; the BCAST stream must stay open.
            if (selector == LeanEthp2pProtocol.BcastStream) Close(LeanEthp2pProtocol.ProtocolViolationError, "BCAST stream reset");
        }
    }

    private static void Refuse(QuicStream stream)
    {
        try { stream.Abort(QuicAbortDirection.Read, LeanEthp2pProtocol.RefusedError); }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadBcastAsync(StreamInput input, TimeSpan firstEnvelope)
    {
        bool second;
        lock (_lock)
        {
            second = _bcastStreamSeen;
            _bcastStreamSeen = true;
        }
        if (second)
        {
            Violation("second BCAST stream");
            return;
        }
        TimeSpan? first = firstEnvelope;
        while (await input.ReadEnvelopeAsync(LeanProtocol.MaxMessageBytes, first, _closing.Token).ConfigureAwait(false) is { } envelope)
        {
            first = null;
            LeanBcastFrame frame = LeanBroadcastWire.DecodeBcast(envelope);
            bool handshake;
            lock (_lock) handshake = _handshakeReceived;
            switch (frame)
            {
                case LeanBcastHandshake hello when !handshake:
                    // EIP-8437 exchanges version-1 handshakes with empty initial channel lists; subscriptions come later.
                    if (hello.Version != LeanBroadcastWire.FrameworkVersion)
                    {
                        Close(LeanEthp2pProtocol.NoError, $"BCAST version {hello.Version} is incompatible");
                        return;
                    }
                    if (hello.Channels.Length != 0)
                    {
                        Violation("BCAST handshake carries channels");
                        return;
                    }
                    lock (_lock) _handshakeReceived = true;
                    TryBecomeReady();
                    break;
                case LeanBcastHandshake:
                    Violation("second BCAST handshake");
                    return;
                case LeanBcastSubscribe subscribe when handshake:
                    OnRemoteSubscribe(subscribe.Channel);
                    break;
                case LeanBcastUnsubscribe unsubscribe when handshake:
                    lock (_lock) _remoteChannels.Remove(unsubscribe.Channel);
                    break;
                default:
                    Violation("BCAST frame before the handshake");
                    return;
            }
        }
        Close(LeanEthp2pProtocol.ProtocolViolationError, "BCAST stream closed");
    }

    private void OnRemoteSubscribe(string channel)
    {
        bool ready;
        lock (_lock)
        {
            if (!_remoteChannels.Contains(channel) && _remoteChannels.Count >= 2 * LeanProtocol.MaxProfiles)
            {
                ready = false;
                channel = "";
            }
            else
            {
                _remoteChannels.Add(channel);
                ready = _ready;
            }
        }
        if (channel.Length == 0) Violation("more than 2 * MAX_PROFILES channel subscriptions");
        else if (ready) _engine?.OnSubscribed(this, channel);
    }

    /// <summary>Once the peer's handshake and Status are both accepted, subscribes and joins the broadcast engine.</summary>
    private void TryBecomeReady()
    {
        string[] subscribed;
        lock (_lock)
        {
            if (_ready || !_handshakeReceived || Volatile.Read(ref _peer) is null || _closing.IsCancellationRequested) return;
            _ready = true;
            subscribed = [.. _remoteChannels];
        }
        if (_engine is null) return;
        foreach (string channel in _engine.Channels) _bcast.Writer.TryWrite(LeanBroadcastWire.Envelope(LeanBroadcastWire.Subscribe(channel)));
        _engine.AddPeer(this);
        foreach (string channel in subscribed) _engine.OnSubscribed(this, channel);
    }

    private async Task ReadSessAsync(StreamInput input, TimeSpan firstEnvelope)
    {
        if (await input.ReadEnvelopeAsync(LeanProtocol.MaxMessageBytes, firstEnvelope, _closing.Token).ConfigureAwait(false) is not { } first) return;
        if (LeanBroadcastWire.DecodeSess(first) is not LeanSessOpen open)
        {
            Violation("SESS stream does not start with Open");
            return;
        }
        switch (_engine!.OnSessionOpen(this, open, out string? reason))
        {
            case LeanBroadcastVerdict.Invalid:
                Violation($"SESS Open rejected: {reason}");
                return;
            case LeanBroadcastVerdict.Refused:
                if (_logger.IsTrace) _logger.Trace($"{Description} SESS Open refused: {reason}");
                Refuse(input.Stream);
                return;
        }
        try
        {
            while (await input.ReadEnvelopeAsync(LeanProtocol.MaxMessageBytes, null, _closing.Token).ConfigureAwait(false) is { } envelope)
            {
                if (LeanBroadcastWire.DecodeSess(envelope) is not LeanSessUpdate update)
                {
                    Violation("second SESS Open");
                    return;
                }
                if (_engine.OnRoutingUpdate(this, open.MessageId, update.Data) == LeanBroadcastVerdict.Invalid)
                {
                    Violation("routing update is not a 4-byte bitmap");
                    return;
                }
            }
            _engine.OnPeerSessionEnded(this, open.MessageId, reconstructed: false);
        }
        catch (QuicException exception) when (exception.QuicError == QuicError.StreamAborted)
        {
            // RESET_STREAM(0x01) on the peer's outbound SESS stream signals its reconstruction; it does not assert validity.
            _engine.OnPeerSessionEnded(this, open.MessageId, exception.ApplicationErrorCode == LeanEthp2pProtocol.ReconstructedError);
        }
    }

    private async Task ReadChunkAsync(StreamInput input, TimeSpan firstEnvelope)
    {
        if (await input.ReadEnvelopeAsync(LeanProtocol.MaxMessageBytes, firstEnvelope, _closing.Token).ConfigureAwait(false) is not { } envelope) return;
        LeanChunkHeader header = LeanBroadcastWire.DecodeChunkHeader(envelope);
        LeanBroadcastVerdict verdict = _engine!.ResolveShard(header, out int index, out bool unknown, out string? reason);
        // A shard may overtake its session's SESS Open on another stream: defer briefly, within the stream budget.
        for (long started = _clock.GetTimestamp(); verdict == LeanBroadcastVerdict.Refused && unknown
             && _clock.GetElapsedTime(started) < UnknownShardDeferral;)
        {
            await Task.Delay(DeferralPoll, _clock, _closing.Token).ConfigureAwait(false);
            verdict = _engine.ResolveShard(header, out index, out unknown, out reason);
        }
        switch (verdict)
        {
            case LeanBroadcastVerdict.Invalid:
                Violation($"CHUNK rejected: {reason}");
                return;
            case LeanBroadcastVerdict.Refused:
                Refuse(input.Stream);
                return;
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        deadline.CancelAfter(LeanProtocol.MaxRequestIdle);
        byte[] shard = await input.ReadExactAsync((int)header.DataLength, deadline.Token).ConfigureAwait(false);
        if (!await input.AtEndAsync(deadline.Token).ConfigureAwait(false))
        {
            Violation("bytes after a shard");
            return;
        }
        if (_engine.OnShard(this, header.MessageId, index, shard) == LeanBroadcastVerdict.Invalid) Violation($"shard {index} does not match its manifest");
    }

    ulong ILeanBroadcastPeer.MaxObjectBytes => Volatile.Read(ref _peer)?.Status.MaxObjectBytes ?? 0;

    bool ILeanBroadcastPeer.IsClosed => Volatile.Read(ref _closeStarted) != 0;

    bool ILeanBroadcastPeer.IsSubscribed(string channel)
    {
        lock (_lock) return _remoteChannels.Contains(channel);
    }

    void ILeanBroadcastPeer.Penalize(string reason) => Violation(reason);

    ILeanBroadcastSession ILeanBroadcastPeer.OpenSession(string channel, string messageId, byte[] preamble, byte[] initialUpdate)
    {
        OutgoingSession session = new(this, channel, messageId,
            LeanBroadcastWire.Envelope(LeanBroadcastWire.SessOpen(channel, messageId, preamble, initialUpdate)));
        _ = session.RunAsync();
        return session;
    }

    async ValueTask<bool> ILeanBroadcastPeer.SendShardAsync(ILeanBroadcastSession session, int index, ReadOnlyMemory<byte> shard)
    {
        if (_closing.IsCancellationRequested || session is not OutgoingSession outgoing) return false;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        deadline.CancelAfter(LeanProtocol.MaxRequestIdle);
        try
        {
            // The SESS Open goes first, so the receiver normally knows the session before its shards.
            if (!await outgoing.Opened.WaitAsync(deadline.Token).ConfigureAwait(false)) return false;
            await using QuicStream stream = await _quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token).ConfigureAwait(false);
            byte[] header = LeanBroadcastWire.Envelope(LeanBroadcastWire.ChunkHeader(outgoing.Channel, outgoing.MessageId,
                LeanBroadcastWire.ShardId(index), (uint)shard.Length));
            byte[] chunk = [LeanEthp2pProtocol.ChunkStream, .. header, .. shard.Span];
            await stream.WriteAsync(chunk, completeWrites: true, deadline.Token).ConfigureAwait(false);
            LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.ChunkStream, chunk.Length);
            return true;
        }
        catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
        {
            // Refused as unneeded or a duplicate, or the peer left: not a fault on either side.
            return false;
        }
    }

    /// <summary>This node's SESS stream toward the peer: Open, routing updates, then FIN or the completion reset.</summary>
    /// <remarks>Open is always written first. Only routing updates queue behind it; each carries the cumulative inventory, so
    /// the oldest can be dropped when the stream falls behind.</remarks>
    private sealed class OutgoingSession(LeanEthp2pConnection connection, string channel, string messageId, byte[] open) : ILeanBroadcastSession
    {
        private readonly Channel<byte[]?> _writes = System.Threading.Channels.Channel.CreateBounded<byte[]?>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public string Channel { get; } = channel;
        public string MessageId { get; } = messageId;
        public Task<bool> Opened => _opened.Task;

        public void Update(byte[] bitmap)
        {
            if (Volatile.Read(ref _completed) == 0) _writes.Writer.TryWrite(LeanBroadcastWire.Envelope(LeanBroadcastWire.SessUpdate(bitmap)));
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _writes.Writer.TryWrite(null);
        }

        public void Close()
        {
            Interlocked.Exchange(ref _completed, 1);
            _writes.Writer.TryComplete();
        }

        public async Task RunAsync()
        {
            // Started under the broadcast engine's lock: return before touching the connection.
            await Task.Yield();
            CancellationToken token = connection._closing.Token;
            QuicStream? stream = null;
            try
            {
                stream = await connection._quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token).ConfigureAwait(false);
                byte[] first = [LeanEthp2pProtocol.SessStream, .. open];
                await stream.WriteAsync(first, token).ConfigureAwait(false);
                LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.SessStream, first.Length);
                _opened.TrySetResult(true);
                await foreach (byte[]? envelope in _writes.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (envelope is null)
                    {
                        stream.Abort(QuicAbortDirection.Write, LeanEthp2pProtocol.ReconstructedError);
                        return;
                    }
                    await stream.WriteAsync(envelope, token).ConfigureAwait(false);
                    LeanMetrics.Ethp2pBytes(outbound: true, LeanEthp2pProtocol.SessStream, envelope.Length);
                }
                stream.CompleteWrites();
            }
            catch (Exception exception) when (exception is QuicException or OperationCanceledException or ObjectDisposedException)
            {
                _opened.TrySetResult(false);
            }
            finally
            {
                _opened.TrySetResult(false);
                if (stream is not null) await DisposeQuietly(stream).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Buffered reads of a framework stream: length-prefixed envelopes, exact shard bodies and FIN.</summary>
    private sealed class StreamInput(QuicStream stream, byte selector, byte[] leftover)
    {
        private byte[] _buffer = leftover;
        private int _offset;

        public QuicStream Stream { get; } = stream;

        private int Buffered => _buffer.Length - _offset;

        private async ValueTask<bool> FillAsync(CancellationToken token)
        {
            if (Buffered > 0) return true;
            byte[] buffer = new byte[16 * 1024];
            int read = await Stream.ReadAsync(buffer, token).ConfigureAwait(false);
            LeanMetrics.Ethp2pBytes(outbound: false, selector, read);
            _buffer = buffer[..read];
            _offset = 0;
            return read > 0;
        }

        /// <summary>Reads one <c>U32</c>-prefixed envelope, or null at FIN before its first byte.</summary>
        /// <remarks>The length is checked before the envelope is allocated; once its first byte arrives, the whole envelope
        /// must follow within MAX_REQUEST_IDLE_SECONDS, and trickled bytes do not extend that.</remarks>
        public async Task<byte[]?> ReadEnvelopeAsync(int maxLength, TimeSpan? firstByte, CancellationToken closing)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(closing);
            if (firstByte is { } first) deadline.CancelAfter(first > TimeSpan.Zero ? first : TimeSpan.Zero);
            if (!await FillAsync(deadline.Token).ConfigureAwait(false)) return null;
            deadline.CancelAfter(LeanProtocol.MaxRequestIdle);
            byte[] prefix = await ReadExactAsync(LeanBroadcastWire.EnvelopeLengthBytes, deadline.Token).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length > maxLength) throw new LeanBroadcastFormatException($"envelope of {length} bytes exceeds MAX_MESSAGE_BYTES");
            return await ReadExactAsync((int)length, deadline.Token).ConfigureAwait(false);
        }

        public async Task<byte[]> ReadExactAsync(int length, CancellationToken token)
        {
            byte[] result = new byte[length];
            int filled = 0;
            while (filled < length)
            {
                if (!await FillAsync(token).ConfigureAwait(false)) throw new LeanBroadcastFormatException("stream ended inside a frame");
                int count = Math.Min(length - filled, Buffered);
                _buffer.AsSpan(_offset, count).CopyTo(result.AsSpan(filled));
                _offset += count;
                filled += count;
            }
            return result;
        }

        public async Task<bool> AtEndAsync(CancellationToken token) => !await FillAsync(token).ConfigureAwait(false);
    }
}
