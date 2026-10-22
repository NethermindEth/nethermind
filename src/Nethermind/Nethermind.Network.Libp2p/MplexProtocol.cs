// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using ChannelClosedException = Nethermind.Libp2p.Core.Exceptions.ChannelClosedException;

namespace Nethermind.Network.Libp2p;

/// <summary>Provides the Mplex TCP fallback required by the consensus networking specification.</summary>
/// <remarks><see href="https://github.com/libp2p/specs/blob/master/mplex/README.md">Mplex</see> has no flow control.
/// Unread input is bounded per stream and connection; a slow stream is reset without blocking other streams.</remarks>
internal sealed class MplexProtocol : IConnectionProtocol
{
    internal const int MaxMessageSize = 1024 * 1024;
    internal const int MaxStreams = 256;
    internal const int MaxBufferedBytes = 8 * MaxMessageSize;
    internal const int OutboundChunkSize = 32 * 1024;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    public MplexProtocol(MultiplexerSettings? multiplexers = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        multiplexers?.Add(this);
        _logger = loggerFactory?.CreateLogger<MplexProtocol>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Id => "/mplex/6.7.0";

    public Task DialAsync(IChannel downChannel, IConnectionContext context) => new Connection(downChannel, context, _logger, _timeProvider).RunAsync(false);

    public Task ListenAsync(IChannel downChannel, IConnectionContext context) => new Connection(downChannel, context, _logger, _timeProvider).RunAsync(true);

    private readonly record struct StreamKey(ulong Id, bool LocalInitiator);

    private sealed class Connection(IChannel transport, IConnectionContext context, ILogger? logger, TimeProvider timeProvider)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<StreamKey, MplexStream> _streams = [];
        private readonly SemaphoreSlim _writer = new(1, 1);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(transport.CancellationToken);
        private INewSessionContext? _session;
        private CancellationTokenRegistration _sessionClosed;
        private Task _outgoing = Task.CompletedTask;
        private int _bufferedBytes;
        private bool _stopping;

        internal async Task RunAsync(bool listener)
        {
            try
            {
                if (!listener) StartSession();
                while (await ReadVarintAsync(allowEof: true) is { } header)
                {
                    byte flag = (byte)(header & 7);
                    if (flag == 7) throw new PeerConnectionException("Reserved Mplex flag");
                    ulong length = await ReadVarintAsync(allowEof: false) ?? throw new PeerConnectionException("Missing Mplex length");
                    if (length > MaxMessageSize) throw new PeerConnectionException("Mplex frame exceeds 1 MiB");
                    if (flag >= 3 && length != 0) throw new PeerConnectionException("Mplex close/reset has a payload");
                    StreamKey key = new(header >> 3, flag != 0 && (flag & 1) != 0);
                    int size = (int)length;

                    if (flag == 0)
                    {
                        await DrainAsync(size);
                        if (_session is null) StartSession();
                        if (!TryOpen(key, new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen }))
                            await WriteFrameAsync(key.Id, 5, default, _stop.Token);
                        continue;
                    }

                    MplexStream? stream;
                    lock (_gate) _streams.TryGetValue(key, out stream);
                    if (stream is null)
                    {
                        await DrainAsync(size);
                        continue;
                    }
                    if (flag is 5 or 6)
                    {
                        await stream.ResetAsync(notify: false);
                    }
                    else if (flag is 3 or 4)
                    {
                        stream.RemoteClosed = true;
                        stream.Input.Writer.TryComplete();
                    }
                    else if (stream.RemoteClosed)
                    {
                        await stream.ResetAsync(notify: true);
                        await DrainAsync(size);
                    }
                    else if (size != 0)
                    {
                        if (!TryReserve(stream, size))
                        {
                            await stream.ResetAsync(notify: true);
                            await DrainAsync(size);
                            continue;
                        }
                        bool queued = false;
                        try
                        {
                            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            deadline.CancelAfter(FrameTimeout);
                            ReadOnlySequence<byte> data = await ReadPayloadAsync(size, deadline.Token);
                            // IChannel acknowledges a read before its consumer finishes; immutable owned bytes cannot be returned to a pool here.
                            queued = stream.Input.Writer.TryWrite(new ReadOnlySequence<byte>(data.ToArray()));
                        }
                        finally
                        {
                            if (!queued) Release(stream, size);
                        }
                        if (!queued) await stream.ResetAsync(notify: true);
                    }
                }
            }
            catch (ChannelClosedException exception)
            {
                logger?.LogDebug(exception, "Mplex connection {Context} closed", context.Id);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                logger?.LogDebug("Mplex connection {Context} cancelled", context.Id);
            }
            finally
            {
                MplexStream[] streams;
                lock (_gate)
                {
                    _stopping = true;
                    streams = new MplexStream[_streams.Count];
                    _streams.Values.CopyTo(streams, 0);
                }
                await _stop.CancelAsync();
                await transport.AbortAsync();
                await context.DisconnectAsync();
                foreach (MplexStream stream in streams) await stream.ResetAsync(notify: false);
                try
                {
                    await _outgoing;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    logger?.LogDebug("Mplex outgoing dispatcher stopped");
                }
                finally
                {
                    foreach (MplexStream stream in streams) await stream.Completion;
                    _sessionClosed.Dispose();
                    _session?.Dispose();
                    _stop.Dispose();
                    _writer.Dispose();
                }
            }
        }

        private void StartSession()
        {
            _session = context.UpgradeToSession();
            _sessionClosed = _session.Token.Register(static state => ((CancellationTokenSource)state!).Cancel(), _stop);
            // The library exposes dial requests as a blocking enumerable; this task is joined at connection shutdown.
            _outgoing = Task.Run(async () =>
            {
                ulong nextId = 0;
                foreach (UpgradeOptions request in _session.DialRequests)
                {
                    if (_stop.IsCancellationRequested) break;
                    if (request.CancellationToken.IsCancellationRequested) continue;
                    try
                    {
                        if (nextId == 1UL << 60) throw new PeerConnectionException("Mplex stream IDs exhausted");
                        if (!TryOpen(new(nextId++, true), request with { ModeOverride = UpgradeModeOverride.Dial }))
                            request.CompletionSource?.TrySetException(new PeerConnectionException("Mplex stream limit reached"));
                    }
                    catch (Exception exception)
                    {
                        request.CompletionSource?.TrySetException(exception);
                        await _stop.CancelAsync();
                        throw;
                    }
                }
            });
        }

        private bool TryOpen(StreamKey key, UpgradeOptions options)
        {
            lock (_gate)
            {
                if (_streams.ContainsKey(key)) throw new PeerConnectionException("Duplicate Mplex stream");
                if (_stopping || _streams.Count == MaxStreams) return false;
                IChannel channel = _session!.Upgrade(options);
                MplexStream stream = new(this, key, channel, _stop.Token, logger);
                _streams.Add(key, stream);
                stream.Completion = Task.Run(stream.RunAsync);
                return true;
            }
        }

        private bool TryReserve(MplexStream stream, int size)
        {
            lock (_gate)
            {
                if (stream.IsReset || stream.BufferedBytes + size > MaxMessageSize || _bufferedBytes + size > MaxBufferedBytes)
                    return false;
                stream.BufferedBytes += size;
                _bufferedBytes += size;
                return true;
            }
        }

        internal void Release(MplexStream stream, int size)
        {
            lock (_gate)
            {
                stream.BufferedBytes -= size;
                _bufferedBytes -= size;
            }
        }

        internal void Remove(StreamKey key)
        {
            lock (_gate) _streams.Remove(key);
        }

        private async ValueTask<ulong?> ReadVarintAsync(bool allowEof)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            if (!allowEof) deadline.CancelAfter(FrameTimeout);
            ulong value = 0;
            for (int index = 0; index < 9; index++)
            {
                ReadResult result = await transport.ReadAsync(1, token: deadline.Token);
                _stop.Token.ThrowIfCancellationRequested();
                if (result.Result == IOResult.Ended && index == 0 && allowEof) return null;
                if (result.Result != IOResult.Ok || result.Data.Length != 1)
                    throw new PeerConnectionException("Truncated Mplex varint");
                byte current = result.Data.FirstSpan[0];
                if (index == 0) deadline.CancelAfter(FrameTimeout);
                value |= (ulong)(current & 127) << (7 * index);
                if ((current & 128) == 0)
                {
                    if (index != 0 && current == 0) throw new PeerConnectionException("Nonminimal Mplex varint");
                    return value;
                }
            }
            throw new PeerConnectionException("Mplex varint exceeds nine bytes");
        }

        private async Task DrainAsync(int size)
        {
            if (size == 0) return;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(FrameTimeout);
            while (size > 0)
            {
                int chunk = Math.Min(size, OutboundChunkSize);
                await ReadPayloadAsync(chunk, deadline.Token);
                size -= chunk;
            }
        }

        private async ValueTask<ReadOnlySequence<byte>> ReadPayloadAsync(int size, CancellationToken token)
        {
            ReadResult result = await transport.ReadAsync(size, token: token);
            _stop.Token.ThrowIfCancellationRequested();
            if (result.Result != IOResult.Ok || result.Data.Length != size)
                throw new PeerConnectionException("Truncated or timed out Mplex payload");
            return result.Data;
        }

        internal async Task WriteFrameAsync(ulong id, byte flag, ReadOnlySequence<byte> data, CancellationToken token)
        {
            byte[] frame = new byte[18 + (int)data.Length];
            int count = WriteVarint(frame, (id << 3) | flag);
            count += WriteVarint(frame.AsSpan(count), (ulong)data.Length);
            data.CopyTo(frame.AsSpan(count));
            await _writer.WaitAsync(token);
            Task writing = Task.CompletedTask;
            try
            {
                writing = transport.WriteAsync(new ReadOnlySequence<byte>(frame.AsMemory(0, count + (int)data.Length)), _stop.Token).OrThrow().AsTask();
                await writing.WaitAsync(FrameTimeout, timeProvider, _stop.Token);
            }
            catch
            {
                // A cancelled partial frame cannot be followed by another stream's frame.
                await _stop.CancelAsync();
                await transport.AbortAsync();
                await writing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }
            finally
            {
                _writer.Release();
            }
        }

        private static int WriteVarint(Span<byte> destination, ulong value)
        {
            int count = 0;
            do
            {
                byte current = (byte)(value & 127);
                value >>= 7;
                destination[count++] = value == 0 ? current : (byte)(current | 128);
            } while (value != 0);
            return count;
        }
    }

    private sealed class MplexStream(Connection connection, StreamKey key, IChannel channel, CancellationToken token, ILogger? logger)
    {
        internal readonly Channel<ReadOnlySequence<byte>> Input = System.Threading.Channels.Channel.CreateBounded<ReadOnlySequence<byte>>(
            new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = false });
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        private readonly TaskCompletionSource _resetCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reset;
        internal bool IsReset => Volatile.Read(ref _reset) != 0;
        internal volatile bool RemoteClosed;
        internal int BufferedBytes;
        internal Task Completion = Task.CompletedTask;

        internal async Task ResetAsync(bool notify)
        {
            if (Interlocked.CompareExchange(ref _reset, 1, 0) != 0)
            {
                await _resetCompleted.Task;
                return;
            }
            try
            {
                await _stop.CancelAsync();
                Input.Writer.TryComplete();
                await channel.AbortAsync();
                if (notify && !token.IsCancellationRequested)
                    await connection.WriteFrameAsync(key.Id, key.LocalInitiator ? (byte)6 : (byte)5, default, token);
            }
            finally
            {
                _resetCompleted.TrySetResult();
            }
        }

        internal async Task RunAsync()
        {
            Task input = ReceiveAsync();
            Task output = SendAsync();
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.GetAwaiter().OnCompleted(() => closed.TrySetResult());
            try
            {
                Task first = await Task.WhenAny(input, output);
                await first;
                Task second = first == input ? output : input;
                if (await Task.WhenAny(second, closed.Task) == closed.Task && !second.IsCompleted)
                {
                    if (second == output) await output;
                    if (!input.IsCompleted && !RemoteClosed &&
                        (await channel.ReadAsync(0, ReadBlockingMode.DoNotWait)).Result == IOResult.Aborted)
                        await ResetAsync(notify: true);
                }
                await Task.WhenAll(input, output);
            }
            catch (Exception exception)
            {
                logger?.LogDebug(exception, "Mplex stream {Stream} stopped", key);
                try { await ResetAsync(notify: true); }
                catch (Exception resetFailure) { logger?.LogDebug(resetFailure, "Mplex reset failed"); }
            }
            finally
            {
                // State 2 closes admission to resets; an existing reset owner must finish before its cancellation source is disposed.
                if (Interlocked.CompareExchange(ref _reset, 2, 0) == 1) await _resetCompleted.Task;
                try
                {
                    await _stop.CancelAsync();
                    if (input.IsCompletedSuccessfully && output.IsCompletedSuccessfully) await channel.CloseAsync();
                    else await channel.AbortAsync();
                    Input.Writer.TryComplete();
                    try { await Task.WhenAll(input, output); }
                    catch (Exception exception) { logger?.LogDebug(exception, "Mplex stream pumps stopped"); }
                    while (Input.Reader.TryRead(out ReadOnlySequence<byte> pending)) connection.Release(this, (int)pending.Length);
                    connection.Remove(key);
                }
                finally
                {
                    _stop.Dispose();
                    _resetCompleted.TrySetResult();
                }
            }
        }

        private async Task ReceiveAsync()
        {
            await foreach (ReadOnlySequence<byte> data in Input.Reader.ReadAllAsync(_stop.Token))
            {
                try { await channel.WriteAsync(data, _stop.Token).OrThrow(); }
                finally { connection.Release(this, (int)data.Length); }
            }
            await channel.WriteEofAsync(_stop.Token).OrThrow();
        }

        private async Task SendAsync()
        {
            if (key.LocalInitiator) await connection.WriteFrameAsync(key.Id, 0, default, _stop.Token);
            while (true)
            {
                ReadResult result = await channel.ReadAsync(OutboundChunkSize, ReadBlockingMode.WaitAny, _stop.Token);
                if (result.Result == IOResult.Ended)
                {
                    await connection.WriteFrameAsync(key.Id, key.LocalInitiator ? (byte)4 : (byte)3, default, _stop.Token);
                    return;
                }
                if (result.Result != IOResult.Ok) throw new PeerConnectionException("Mplex application stream aborted");
                if (!result.Data.IsEmpty)
                    await connection.WriteFrameAsync(key.Id, key.LocalInitiator ? (byte)2 : (byte)1,
                        result.Data, _stop.Token);
            }
        }
    }
}
