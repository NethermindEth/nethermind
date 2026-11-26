// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// An RLPx connection on a socket: the handshake, the frames in both directions, and the session's packets.
/// </summary>
/// <remarks>
/// <para>
/// Three loops run per connection. The receive loop fills a pipe from the socket. The frame loop reads the pipe: the
/// handshake, then frames, which it authenticates, decrypts and merges into packets for the connection's processing
/// thread. The send loop encodes the queued packets and writes them to the socket. Each direction has its own cipher
/// and MAC instances, so the two loops never share crypto state.
/// </para>
/// <para>
/// The processing thread runs what <c>ZeroNettyP2PHandler</c> runs on DotNetty's executor: the size limit, Snappy and
/// <see cref="ISession.ReceiveMessage"/>, and the error handling, in the order the frame loop queued them.
/// </para>
/// </remarks>
/// <param name="socket">The connected socket.</param>
/// <param name="role">Whether this side dialed the peer.</param>
/// <param name="handshakeService">Builds and reads the handshake messages.</param>
/// <param name="serializationService">Serializes the session's messages.</param>
/// <param name="processingThread">The thread that handles what the connection receives.</param>
/// <param name="logManager">The log manager.</param>
/// <param name="sendLatency">The simulated latency of every packet sent, for tests and benchmarks.</param>
/// <param name="maxInboundFrameSize">The largest frame payload accepted.</param>
/// <param name="pool">The pool of the connection's buffers; the shared one unless a test counts rentals.</param>
internal sealed class SocketRlpxConnection(
    IRlpxSocket socket,
    HandshakeRole role,
    IHandshakeService handshakeService,
    IMessageSerializationService serializationService,
    RlpxProcessingThreads.ProcessingThread processingThread,
    ILogManager logManager,
    TimeSpan sendLatency,
    int maxInboundFrameSize,
    ArrayPool<byte>? pool = null) : IRlpxConnection, IPacketSender
{
    /// <summary>The largest handshake message, with its 2-byte size prefix, as DotNetty's length field decoder allows.</summary>
    internal const int MaxHandshakeLength = ushort.MaxValue;

    /// <summary>The time without receiving anything after which the connection closes, as DotNetty's <c>ReadTimeoutHandler</c>.</summary>
    internal static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The largest packet encoded into the shared send batch; a larger one gets its own buffer.</summary>
    internal const int SendBatchSize = 64 * 1024;

    private const int ReceiveSegmentSize = 16 * 1024;
    private const int MaxRetainedSnappyOutputCapacity = 64 * 1024;

    private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
    private readonly IRlpxSocket _socket = socket;
    private readonly HandshakeRole _role = role;
    private readonly IHandshakeService _handshakeService = handshakeService;
    private readonly IMessageSerializationService _serializationService = serializationService;
    private readonly RlpxProcessingThreads.ProcessingThread _processingThread = processingThread;
    private readonly ILogger _logger = logManager.GetClassLogger<SocketRlpxConnection>();
    private readonly long _sendLatencyMs = (long)sendLatency.TotalMilliseconds;
    private readonly int _maxInboundFrameSize = maxInboundFrameSize;
    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: 1_000_000,
        resumeWriterThreshold: 500_000,
        minimumSegmentSize: ReceiveSegmentSize,
        useSynchronizationContext: false));
    private readonly CancellationTokenSource _receiveCancellation = new();
    private readonly CancellationTokenSource _sendCancellation = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ISession _session = null!;
    private Task _completion = Task.CompletedTask;
    private volatile bool _isClosed;
    private FrameMacProcessor? _ingressMac;
    private FrameMacProcessor? _egressMac;

    // Receive loop
    private Exception? _terminalCause;
    private long _lastReceive;
    private volatile bool _readTimedOut;
    private Timer? _idleTimer;

    // Frame loop
    private FrameDecoder? _decoder;
    private readonly FrameMerger _merger = new();
    private FrameState _frameState;
    private byte[]? _frame;
    private int _frameSize;
    private int _frameFilled;
    private PooledPacket? _chunkedPacket;
    private int _chunkedPacketTotal;
    private bool _handshakeCompleted;
    private bool _discarding;

    // Processing thread
    private volatile bool _inboundSnappy;
    private PooledPacket? _snappyOutput;
    private bool _processingStopped;
    private readonly SnappyOutputWriter _snappyOutputWriter = new();

    // Send queue, under _sendLock
    private readonly Lock _sendLock = new();
    private readonly Queue<OutboundItem> _sendQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0);
    private bool _sendLoopWaiting;
    private FrameEncoder? _encoder;
    private FrameEncoder.EncodingMode _encodingMode = FrameEncoder.EncodingMode.Framed;
    private readonly OutboundBytes _outboundBytes = new();

    /// <summary>Completes when the connection has closed, before the frames already received are handled.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Completes when the connection's loops have finished.</summary>
    public Task Completion => _completion;

    public bool IsWritable => _outboundBytes.IsWritable;

    /// <summary>Gets or sets the time without receiving anything after which the connection closes.</summary>
    internal TimeSpan ReadTimeout { get; init; } = DefaultReadTimeout;

    /// <summary>Runs before each batch of received bytes is decoded; a test holds it to hold the frame loop.</summary>
    internal Func<ValueTask>? BeforeDecoding { get; init; }

    /// <summary>Starts the handshake and the loops of a connection whose session has been created.</summary>
    public void Start(ISession session)
    {
        _session = session;
        _completion = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.WhenAll(
                Task.Run(SendLoopAsync),
                Task.Run(ReceiveLoopAsync),
                Task.Run(FrameLoopAsync));
        }
        finally
        {
            // Returns the MAC processors' hash copies to their pool, as DotNetty's handshake handler does.
            _ingressMac?.Dispose();
            _egressMac?.Dispose();
        }
    }

    public void EnableSnappy()
    {
        lock (_sendLock)
        {
            _encoder?.EnableSnappy(_logger);
            _encodingMode = FrameEncoder.EncodingMode.Snappy;
        }

        _inboundSnappy = true;
    }

    public Task DisconnectAsync()
    {
        Close();
        return _closed.Task;
    }

    private void Close()
    {
        lock (_sendLock)
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            _outboundBytes.Close();
            while (_sendQueue.TryDequeue(out OutboundItem item))
            {
                _pool.Return(item.Buffer);
            }
        }

        _sendSignal.Release();
        _idleTimer?.Dispose();
        _receiveCancellation.Cancel();
        _sendCancellation.Cancel();
        _socket.Close();
        _closed.TrySetResult();
    }

    #region Receive

    private async Task ReceiveLoopAsync()
    {
        PipeWriter writer = _pipe.Writer;
        Exception? cause = null;
        try
        {
            CancellationToken cancellation = _receiveCancellation.Token;
            while (true)
            {
                // Waits for data without holding a buffer, so idle peers hold none.
                await _socket.ReceiveAsync(Memory<byte>.Empty, cancellation);
                int received = await _socket.ReceiveAsync(writer.GetMemory(ReceiveSegmentSize), cancellation);
                if (received == 0)
                {
                    break;
                }

                Volatile.Write(ref _lastReceive, Environment.TickCount64);
                writer.Advance(received);
                FlushResult flush = await writer.FlushAsync(CancellationToken.None);
                if (flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception e)
        {
            // An error from or after a local close is not the peer's; the idle timer cancels the receive on a read timeout.
            cause = _isClosed || e is ObjectDisposedException ? null
                : _readTimedOut && e is OperationCanceledException ? new RlpxReadTimeoutException()
                : e;
        }
        finally
        {
            // The cause goes after the frames already received, and the pipe completes normally, since a pipe completed
            // with an exception hides the data it still holds. DotNetty also passes on what it read before the error.
            _terminalCause = cause;
            Close();
            await writer.CompleteAsync();
        }
    }

    private void StartIdleTimer()
    {
        Volatile.Write(ref _lastReceive, Environment.TickCount64);
        Timer timer = new(OnIdleTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _idleTimer = timer;
        if (_isClosed)
        {
            timer.Dispose();
            return;
        }

        try
        {
            timer.Change(ReadTimeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile.
        }
    }

    private void OnIdleTimer(object? _)
    {
        long idle = Environment.TickCount64 - Volatile.Read(ref _lastReceive);
        long remaining = (long)ReadTimeout.TotalMilliseconds - idle;
        if (remaining > 0)
        {
            try
            {
                _idleTimer?.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile.
            }

            return;
        }

        _readTimedOut = true;
        _receiveCancellation.Cancel();
    }

    private async Task FrameLoopAsync()
    {
        PipeReader reader = _pipe.Reader;
        try
        {
            if (await HandshakeAsync(reader))
            {
                await ReadFramesAsync(reader);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Unexpected failure while reading from {_session:hide}", e);
            Close();
        }
        finally
        {
            ReleaseFrame();
            ReleaseChunkedPacket();
            if (_handshakeCompleted)
            {
                if (_terminalCause is { } cause)
                {
                    QueueError(cause);
                }

                if (!_processingThread.TryEnqueue(new ProcessingItem(this, null, null)))
                {
                    StopProcessing();
                }
            }

            await reader.CompleteAsync();
        }
    }

    private async Task<bool> HandshakeAsync(PipeReader reader)
    {
        EncryptionHandshake handshake = new();
        using CancellationTokenSource timeout = new(Timeouts.Handshake);
        try
        {
            if (_role == HandshakeRole.Initiator)
            {
                Packet auth = _handshakeService.Auth(_session.RemoteNodeId, handshake);
                if (_logger.IsTrace) _logger.Trace($"Sending AUTH to {_session.RemoteNodeId:hide} @ {_session.RemoteHost:hide}");
                QueueRaw(auth.Data);
                Interlocked.Add(ref Metrics.P2PBytesSent, auth.Data.Length);
            }

            byte[]? message = await ReadHandshakeMessageAsync(reader, timeout.Token);
            if (message is null)
            {
                if (_terminalCause is { } cause)
                {
                    LogHandshakeFailure(cause);
                }

                return false;
            }

            Interlocked.Add(ref Metrics.P2PBytesReceived, message.Length);
            if (_role == HandshakeRole.Recipient)
            {
                if (_logger.IsTrace) _logger.Trace($"AUTH received from {_session.RemoteHost:hide}");
                Packet ack = _handshakeService.Ack(handshake, new Packet(message));
                if (_logger.IsTrace) _logger.Trace($"Sending ACK to {handshake.RemoteNodeId:hide} @ {_session.RemoteHost:hide}");
                QueueRaw(ack.Data);
                Interlocked.Add(ref Metrics.P2PBytesSent, ack.Data.Length);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Received ACK from {_session.RemoteNodeId:hide} @ {_session.RemoteHost:hide}");
                _handshakeService.Agree(handshake, new Packet(message));
            }

            _session.Handshake(handshake.RemoteNodeId);

            // Each direction gets its own instances, so the frame and send loops share no crypto state. Each MAC processor
            // copies both directions' hashes when constructed, so both are built before either loop uses them.
            EncryptionSecrets secrets = handshake.Secrets;
            FrameMacProcessor ingressMac = _ingressMac = new(secrets);
            FrameMacProcessor egressMac = _egressMac = new(secrets);
            _decoder = new FrameDecoder(new FrameCipher(secrets.AesSecret), ingressMac, _maxInboundFrameSize);
            lock (_sendLock)
            {
                _encoder = new FrameEncoder(new FrameCipher(secrets.AesSecret), egressMac);
            }

            _handshakeCompleted = true;
            _session.Init(5, this);
            StartIdleTimer();
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_isClosed)
        {
            Metrics.HandshakeTimeouts++;
            if (_logger.IsTrace) _logger.Trace($"Disconnecting due to timeout for handshake: {_session.RemoteNodeId:hide}@{_session.RemoteHost:hide}:{_session.RemotePort:hide}");
            Close();
            return false;
        }
        catch (Exception e)
        {
            LogHandshakeFailure(e);
            Close();
            return false;
        }
    }

    /// <summary>Reads an AUTH or ACK with its 2-byte size prefix, which the handshake service takes with the message.</summary>
    /// <returns>The message, or <see langword="null"/> if the stream ended first.</returns>
    private static async Task<byte[]?> ReadHandshakeMessageAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= sizeof(ushort))
            {
                int length = ReadHandshakeLength(buffer);
                if (length > MaxHandshakeLength)
                {
                    reader.AdvanceTo(buffer.Start);
                    throw new InvalidFrameException($"Handshake message of {length} bytes exceeds {MaxHandshakeLength}");
                }

                if (buffer.Length >= length)
                {
                    byte[] message = buffer.Slice(0, length).ToArray();
                    reader.AdvanceTo(buffer.GetPosition(length));
                    return message;
                }
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return null;
            }
        }
    }

    /// <summary>Reads a handshake message's length, with its size prefix, from the prefix.</summary>
    private static int ReadHandshakeLength(ReadOnlySequence<byte> buffer)
    {
        Span<byte> prefix = stackalloc byte[sizeof(ushort)];
        buffer.Slice(0, sizeof(ushort)).CopyTo(prefix);
        return BinaryPrimitives.ReadUInt16BigEndian(prefix) + sizeof(ushort);
    }

    private void LogHandshakeFailure(Exception exception)
    {
        string clientId = _session.RemoteNodeId is null ? $"unknown {_session.RemoteHost}" : _session.Node.ToString(Node.Format.Console);
        if (exception is SocketException)
        {
            if (_logger.IsTrace) _logger.Trace($"Handshake failure in communication with {clientId:hide} (SocketException): {exception:hide}");
        }
        else if (_logger.IsDebug)
        {
            _logger.Debug($"Handshake failure in communication with {clientId:hide}: {exception:hide}");
        }
    }

    private async Task ReadFramesAsync(PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            if (BeforeDecoding is { } beforeDecoding)
            {
                await beforeDecoding();
            }

            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = _discarding ? buffer.End : DecodeFrames(buffer);
            reader.AdvanceTo(consumed, buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }
    }

    /// <summary>Decodes every whole block available, as <see cref="ZeroFrameDecoder"/> does.</summary>
    /// <returns>Where the bytes not yet consumed start.</returns>
    private SequencePosition DecodeFrames(ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> input = new(buffer);
        Span<byte> block = stackalloc byte[Frame.HeaderSize + Frame.MacSize];
        Span<byte> plaintext = stackalloc byte[Frame.HeaderSize];
        while (input.Remaining >= Frame.BlockSize)
        {
            switch (_frameState)
            {
                case FrameState.Header:
                    {
                        if (input.Remaining < Frame.HeaderSize + Frame.MacSize)
                        {
                            return input.Position;
                        }

                        input.TryCopyTo(block);
                        input.Advance(block.Length);
                        if (!TryReadHeader(block[..Frame.HeaderSize], block[Frame.HeaderSize..], plaintext, out int frameSize))
                        {
                            return buffer.End;
                        }

                        _frameSize = frameSize;
                        _frame = _pool.Rent(Frame.HeaderSize + frameSize);
                        plaintext.CopyTo(_frame);
                        _frameFilled = Frame.HeaderSize;

                        ReadOnlySpan<byte> whole = input.UnreadSpan;
                        if (whole.Length >= frameSize + Frame.MacSize)
                        {
                            // The frame is whole in one segment: authenticate before decrypting.
                            ReadOnlySpan<byte> payload = whole[..frameSize];
                            _decoder!.AbsorbPayload(payload);
                            if (!TryCheckPayloadMac(whole.Slice(frameSize, Frame.MacSize)))
                            {
                                return buffer.End;
                            }

                            _decoder.DecryptPayload(payload, _frame.AsSpan(Frame.HeaderSize, frameSize));
                            input.Advance(frameSize + Frame.MacSize);
                            CompleteFrame();
                            break;
                        }

                        _frameState = FrameState.Payload;
                        break;
                    }
                case FrameState.Payload:
                    {
                        // Plaintext stays in the frame buffer until the payload MAC passes.
                        int remaining = Frame.HeaderSize + _frameSize - _frameFilled;
                        ReadOnlySpan<byte> span = input.UnreadSpan;
                        int length = Math.Min(remaining, span.Length - span.Length % Frame.BlockSize);
                        scoped ReadOnlySpan<byte> ciphertext;
                        if (length > 0)
                        {
                            ciphertext = span[..length];
                        }
                        else
                        {
                            // A block split across segments.
                            length = Frame.BlockSize;
                            input.TryCopyTo(block[..length]);
                            ciphertext = block[..length];
                        }

                        _decoder!.AbsorbPayload(ciphertext);
                        _decoder.DecryptPayload(ciphertext, _frame.AsSpan(_frameFilled, length));
                        input.Advance(length);
                        _frameFilled += length;
                        if (_frameFilled == Frame.HeaderSize + _frameSize)
                        {
                            _frameState = FrameState.PayloadMac;
                        }

                        break;
                    }
                case FrameState.PayloadMac:
                    {
                        Span<byte> mac = block[..Frame.MacSize];
                        input.TryCopyTo(mac);
                        input.Advance(Frame.MacSize);
                        if (!TryCheckPayloadMac(mac))
                        {
                            return buffer.End;
                        }

                        CompleteFrame();
                        _frameState = FrameState.Header;
                        break;
                    }
            }
        }

        return input.Position;
    }

    private bool TryReadHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> mac, Span<byte> plaintext, out int frameSize)
    {
        try
        {
            frameSize = _decoder!.ReadHeader(header, mac, plaintext);
            return true;
        }
        catch (InvalidFrameException e)
        {
            StopDecoding(e);
            frameSize = 0;
            return false;
        }
    }

    private bool TryCheckPayloadMac(ReadOnlySpan<byte> mac)
    {
        try
        {
            _decoder!.CheckPayloadMac(mac);
            return true;
        }
        catch (InvalidFrameException e)
        {
            StopDecoding(e);
            return false;
        }
    }

    /// <summary>
    /// Stops decoding after an error that leaves the ingress cipher and MAC out of step, so that no later frame can decode.
    /// </summary>
    /// <remarks>
    /// The error is handled as DotNetty's would be: other peers are disconnected, while a static or trusted peer keeps the
    /// connection, which the peer, the read timeout or the ping timeout ends. Unlike DotNetty, nothing more is decoded.
    /// </remarks>
    private void StopDecoding(Exception exception)
    {
        _discarding = true;
        ReleaseFrame();
        ReleaseChunkedPacket();
        QueueError(exception);
    }

    private void CompleteFrame()
    {
        byte[] frame = _frame!;
        _frame = null;
        _frameState = FrameState.Header;

        MergedFrame merged;
        try
        {
            merged = _merger.Read(frame.AsSpan(0, Frame.HeaderSize + _frameSize));
        }
        catch (InvalidFrameException e)
        {
            // Authenticated frames stay in step, so decoding continues, as it does on DotNetty.
            _pool.Return(frame);
            ReleaseChunkedPacket();
            QueueError(e);
            return;
        }

        switch (merged.Kind)
        {
            case MergedFrameKind.SinglePacket:
                Dispatch(new PooledPacket(_pool, frame, merged.ContentOffset, merged.ContentLength) { PacketType = merged.PacketType });
                return;
            case MergedFrameKind.FirstChunk:
                {
                    // Grows from the first chunk up to the declared total, as ZeroFrameMerger's buffer does.
                    byte[] buffer = _pool.Rent(merged.ContentLength);
                    frame.AsSpan(merged.ContentOffset, merged.ContentLength).CopyTo(buffer);
                    _chunkedPacket = new PooledPacket(_pool, buffer, 0, merged.ContentLength) { PacketType = merged.PacketType };
                    _chunkedPacketTotal = merged.TotalContentLength;
                    break;
                }
            default:
                {
                    PooledPacket packet = _chunkedPacket!;
                    int length = packet.Content.Length;
                    int needed = length + merged.ContentLength;
                    if (needed > packet.Buffer.Length)
                    {
                        packet.Grow(Math.Max(needed, Math.Min(packet.Buffer.Length * 2, _chunkedPacketTotal)));
                    }

                    frame.AsSpan(merged.ContentOffset, merged.ContentLength).CopyTo(packet.Buffer.AsSpan(length));
                    packet.SetContent(0, needed);
                    break;
                }
        }

        _pool.Return(frame);
        if (merged.IsComplete)
        {
            PooledPacket packet = _chunkedPacket!;
            _chunkedPacket = null;
            Dispatch(packet);
        }
    }

    private void Dispatch(PooledPacket packet)
    {
        if (!_processingThread.TryEnqueue(new ProcessingItem(this, packet, null)))
        {
            packet.Release();
        }
    }

    private void QueueError(Exception exception) => _processingThread.TryEnqueue(new ProcessingItem(this, null, exception));

    private void ReleaseFrame()
    {
        if (_frame is { } frame)
        {
            _frame = null;
            _pool.Return(frame);
        }

        _frameState = FrameState.Header;
    }

    private void ReleaseChunkedPacket()
    {
        _merger.Reset();
        PooledPacket? packet = _chunkedPacket;
        _chunkedPacket = null;
        packet?.Release();
    }

    #endregion

    #region Processing thread

    /// <summary>Handles an item on the connection's processing thread.</summary>
    internal void Process(in ProcessingItem item)
    {
        if (item.Packet is { } packet)
        {
            ProcessPacket(packet);
        }
        else if (item.Error is { } error)
        {
            HandleError(error);
        }
        else
        {
            StopProcessing();
        }
    }

    private void ProcessPacket(PooledPacket input)
    {
        try
        {
            ReadOnlyMemory<byte> content = input.Content;
            if (!SnappyDecoder.IsWithinSizeLimit(content.Length))
            {
                _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
                return;
            }

            if (!_inboundSnappy)
            {
                ReceiveMessage(input);
                return;
            }

            if (!SnappyDecoder.TryGetUncompressedLength(content, out int uncompressedLength, _logger))
            {
                _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
                return;
            }

            if (_logger.IsTrace) TraceSnappyMessage(content.Length);

            PooledPacket output = TakeSnappyOutput(uncompressedLength);
            try
            {
                _snappyOutputWriter.Packet = output;
                SnappyDecoder.Decompress(content, _snappyOutputWriter, _logger);
                Debug.Assert(output.Content.Length == uncompressedLength, "Validated Snappy output must match its declared length.");
            }
            catch
            {
                output.Release();
                throw;
            }
            finally
            {
                _snappyOutputWriter.Packet = null;
            }

            try
            {
                output.Protocol = null;
                output.PacketType = input.PacketType;
                ReceiveMessage(output);
            }
            finally
            {
                // A consumer that kept the content holds a lease, which prevents reuse.
                if (!_processingStopped && _snappyOutput is null && output.IsExclusive && output.Buffer.Length <= MaxRetainedSnappyOutputCapacity)
                    _snappyOutput = output;
                else
                    output.Release();
            }
        }
        catch (Exception e)
        {
            // Errors raised while handling an item are handled before the next item, as DotNetty fires them on the same handler.
            HandleError(e);
        }
        finally
        {
            input.Release();
        }
    }

    private void ReceiveMessage(ZeroPacket packet)
    {
        try
        {
            _session.ReceiveMessage(packet);
        }
        catch (RlpReaderException exception) when (exception is not IInternalNethermindException)
        {
            if (!_session.IsClosing)
            {
                HandleError(exception);
            }
        }
    }

    private void HandleError(Exception exception) => RlpxErrorHandler.TryHandle(_session, exception, _logger);

    private PooledPacket TakeSnappyOutput(int length)
    {
        PooledPacket? packet = _snappyOutput;
        if (packet is not null && length <= MaxRetainedSnappyOutputCapacity)
        {
            _snappyOutput = null;
            if (packet.Buffer.Length >= length)
            {
                packet.SetContent(0, 0);
                return packet;
            }

            packet.Release();
        }

        return new PooledPacket(_pool, _pool.Rent(length), 0, 0);
    }

    private void StopProcessing()
    {
        _processingStopped = true;
        PooledPacket? packet = _snappyOutput;
        _snappyOutput = null;
        packet?.Release();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceSnappyMessage(int length)
    {
        if (length > SnappyParameters.MaxSnappyLength / 4)
        {
            _logger.Trace($"Big Snappy message of length {length}");
        }
        else
        {
            _logger.Trace($"Uncompressing with Snappy a message of length {length}");
        }
    }

    #endregion

    #region Send

    public int Enqueue<T>(T message) where T : P2PMessage
    {
        if (!_outboundBytes.IsWritable)
        {
            return 0;
        }

        (byte[] buffer, int length) = Serialize(_serializationService, message, _pool);
        TryAdmit(buffer, length, raw: false);
        return length;
    }

    private void QueueRaw(byte[] data)
    {
        byte[] buffer = _pool.Rent(data.Length);
        data.CopyTo(buffer, 0);
        TryAdmit(buffer, data.Length, raw: true);
    }

    /// <summary>
    /// Queues a packet with the encoding mode and send time it has now, and charges it to the outbound bytes, or releases
    /// it if the connection has closed: the one place where a packet is either published or released.
    /// </summary>
    private void TryAdmit(byte[] buffer, int length, bool raw)
    {
        // The handshake messages are not delayed: DotNetty writes them without going through PacketSender.
        long sendAfter = raw || _sendLatencyMs == 0 ? 0 : Environment.TickCount64 + _sendLatencyMs;
        bool wake;
        lock (_sendLock)
        {
            if (_isClosed)
            {
                _pool.Return(buffer);
                return;
            }

            _sendQueue.Enqueue(new OutboundItem(buffer, length, raw, _encodingMode, sendAfter));
            _outboundBytes.Add(length);
            wake = _sendLoopWaiting;
            _sendLoopWaiting = false;
        }

        if (wake)
        {
            _sendSignal.Release();
        }
    }

    private void Charge(long bytes)
    {
        lock (_sendLock)
        {
            _outboundBytes.Add(bytes);
        }
    }

    private async Task SendLoopAsync()
    {
        byte[] batch = _pool.Rent(SendBatchSize);
        // The end of each packet in the batch and its charge, which is released once its last byte is sent.
        List<(int End, int Charge)> batchPackets = [];
        int used = 0;
        try
        {
            CancellationToken cancellation = _sendCancellation.Token;
            while (true)
            {
                OutboundItem item;
                FrameEncoder? encoder;
                lock (_sendLock)
                {
                    if (_isClosed)
                    {
                        return;
                    }

                    if (!_sendQueue.TryDequeue(out item))
                    {
                        _sendLoopWaiting = used == 0;
                    }

                    encoder = _encoder;
                }

                if (item.Buffer is null)
                {
                    if (used > 0)
                    {
                        await SendAsync(batch, used, batchPackets, cancellation);
                        used = 0;
                    }
                    else
                    {
                        await _sendSignal.WaitAsync(cancellation);
                    }

                    continue;
                }

                try
                {
                    if (item.SendAfter != 0)
                    {
                        long delay = item.SendAfter - Environment.TickCount64;
                        if (delay > 0)
                        {
                            if (used > 0)
                            {
                                await SendAsync(batch, used, batchPackets, cancellation);
                                used = 0;
                            }

                            await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellation);
                        }
                    }

                    int bound = item.Raw ? item.Length : encoder!.GetMaxEncodedLength(item.Length, item.Mode);
                    if (used + bound > batch.Length && used > 0)
                    {
                        await SendAsync(batch, used, batchPackets, cancellation);
                        used = 0;
                    }

                    if (bound > batch.Length)
                    {
                        // A packet larger than the batch gets its own buffer, since the encoder needs it contiguous.
                        byte[] own = _pool.Rent(bound);
                        try
                        {
                            if (TryEncode(item, encoder, own, out int written))
                            {
                                batchPackets.Add((written, written));
                                await SendAsync(own, written, batchPackets, cancellation);
                            }
                        }
                        finally
                        {
                            _pool.Return(own);
                        }
                    }
                    else if (TryEncode(item, encoder, batch.AsSpan(used), out int written))
                    {
                        used += written;
                        batchPackets.Add((used, written));
                    }
                }
                finally
                {
                    _pool.Return(item.Buffer);
                }
            }
        }
        catch (Exception e)
        {
            // Cancellation and disposal follow a local close, which needs no report.
            if (!_isClosed || e is not (OperationCanceledException or ObjectDisposedException))
            {
                HandleSendFailure(e);
            }
        }
        finally
        {
            _pool.Return(batch);
        }
    }

    /// <summary>Encodes a packet, or copies a raw handshake message, and charges its encoded length instead of its serialized one.</summary>
    private bool TryEncode(in OutboundItem item, FrameEncoder? encoder, Span<byte> destination, out int written)
    {
        try
        {
            if (item.Raw)
            {
                item.Buffer.AsSpan(0, item.Length).CopyTo(destination);
                written = item.Length;
            }
            else
            {
                written = encoder!.Encode(item.Buffer.AsSpan(0, item.Length), destination, item.Mode);
            }
        }
        catch (Exception e)
        {
            Charge(-item.Length);
            if (_logger.IsError) _logger.Error("Channel is active", e);
            written = 0;
            return false;
        }

        Charge(written - item.Length);
        return true;
    }

    /// <summary>Sends a buffer in full, releasing each packet's charge once its last byte has gone.</summary>
    private async ValueTask SendAsync(byte[] buffer, int length, List<(int End, int Charge)> packets, CancellationToken cancellation)
    {
        int offset = 0;
        int next = 0;
        while (offset < length)
        {
            int sent = await _socket.SendAsync(buffer.AsMemory(offset, length - offset), cancellation);
            if (sent <= 0)
            {
                throw new SocketException((int)SocketError.ConnectionAborted);
            }

            offset += sent;
            long released = 0;
            while (next < packets.Count && packets[next].End <= offset)
            {
                released += packets[next].Charge;
                next++;
            }

            if (released > 0)
            {
                Charge(-released);
            }
        }

        packets.Clear();
    }

    private void HandleSendFailure(Exception exception)
    {
        // DotNetty closes the channel before it fails the pending writes, so a lost connection is logged at trace.
        Close();
        if (_logger.IsTrace) _logger.Trace($"Channel is not active - {exception.Message}");
    }

    // Messages are enqueued from any thread, so each thread reuses its own writer.
    [ThreadStatic]
    private static PacketWriter? t_writer;

    /// <summary>Serializes a message into a rented buffer as its packet type followed by its content.</summary>
    internal static (byte[] Buffer, int Length) Serialize<T>(IMessageSerializationService serializationService, T message, ArrayPool<byte> pool)
        where T : P2PMessage
    {
        PacketWriter writer = t_writer ??= new();
        writer.Start(pool, message.AdaptivePacketType);
        try
        {
            serializationService.Serialize(message, writer);
            return writer.Buffer is { } buffer
                ? (buffer, writer.Length)
                : throw new InvalidOperationException($"{nameof(IMessageSerializationService)} did not request a span for the message.");
        }
        catch
        {
            if (writer.Buffer is { } buffer)
            {
                pool.Return(buffer);
            }

            throw;
        }
        finally
        {
            writer.Clear();
        }
    }

    /// <summary>Writes a packet into a single rented buffer: its packet type, then its content.</summary>
    /// <remarks>Rents the buffer on the one <see cref="GetSpan"/> call for the content, as <see cref="PacketSender"/>'s writer does.</remarks>
    private sealed class PacketWriter : IBufferWriter<byte>
    {
        private ArrayPool<byte>? _pool;
        private int _packetType;
        private int _packetTypeLength;

        public byte[]? Buffer { get; private set; }

        public int Length { get; private set; }

        public void Start(ArrayPool<byte> pool, int packetType)
        {
            _pool = pool;
            _packetType = packetType;
        }

        // Keeps an idle thread's writer from holding on to the last buffer and pool.
        public void Clear()
        {
            _pool = null;
            Buffer = null;
            Length = 0;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (Buffer is not null)
                throw new InvalidOperationException($"{nameof(PacketWriter)} takes the content in a single span.");

            int packetTypeLength = _packetTypeLength = Rlp.LengthOf(_packetType);
            // IBufferWriter requires a non-empty span when no size is requested.
            int length = packetTypeLength + Math.Max(sizeHint, 1);
            byte[] buffer = Buffer = _pool!.Rent(length);
            Span<byte> packet = buffer.AsSpan(0, length);
            RlpWriter writer = RlpWriter.ContentWriter(packet);
            writer.Encode(_packetType);
            Length = packetTypeLength;
            return packet[packetTypeLength..];
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();

        public void Advance(int count) => Length = _packetTypeLength + count;
    }

    #endregion

    private readonly record struct OutboundItem(byte[] Buffer, int Length, bool Raw, FrameEncoder.EncodingMode Mode, long SendAfter);

    private enum FrameState
    {
        Header,
        Payload,
        PayloadMac
    }

    /// <summary>Writes decompressed content into a <see cref="PooledPacket"/>'s buffer, growing it if needed.</summary>
    private sealed class SnappyOutputWriter : IBufferWriter<byte>
    {
        public PooledPacket? Packet { get; set; }

        public void Advance(int count)
        {
            PooledPacket packet = Packet!;
            packet.SetContent(0, packet.Content.Length + count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            PooledPacket packet = Packet!;
            int length = packet.Content.Length;
            int needed = length + Math.Max(1, sizeHint);
            if (needed > packet.Buffer.Length)
            {
                packet.Grow(Math.Max(needed, packet.Buffer.Length * 2));
            }

            return packet.Buffer.AsMemory(length);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
