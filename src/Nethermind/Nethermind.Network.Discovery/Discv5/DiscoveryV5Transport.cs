// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Discv5;

/// <summary>
/// Queues inbound discv5 datagrams for the protocol workers and sends outbound datagrams through the discovery socket.
/// </summary>
/// <remarks>
/// The discovery socket is shared with discv4 and bound only when discovery starts, so it is attached late
/// through <see cref="BindSocket"/>.
/// </remarks>
public sealed class DiscoveryV5Transport(ILogManager logManager)
{
    private const int MaxMessagesBuffered = 1024;

    private readonly ILogger _logger = logManager.GetClassLogger<DiscoveryV5Transport>();
    private readonly Channel<PooledUdpReceiveResult> _inboundQueue = Channel.CreateBounded<PooledUdpReceiveResult>(MaxMessagesBuffered);

    private IDatagramSocket? _socket;
    private int _activeReaders;

    internal void BindSocket(IDatagramSocket socket) => _socket = socket;

    /// <summary>
    /// Queues a received datagram, taking ownership of its buffer; the datagram is dropped when the queue is full or closed.
    /// </summary>
    internal void Receive(PooledUdpReceiveResult result)
    {
        if (_inboundQueue.Writer.TryWrite(result))
        {
            if (_logger.IsTrace) _logger.Trace($"Queued discv5 UDP packet from {result.RemoteEndPoint}, bytes: {result.Buffer.Length}.");
            return;
        }

        result.Dispose();
        if (_logger.IsWarn) _logger.Warn("Skipping discovery v5 message as inbound buffer is full");
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        IDatagramSocket socket = _socket ?? throw new InvalidOperationException("Discovery channel is not initialized.");

        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending discv5 UDP packet to {destination}, bytes: {data.Length}.");
            await socket.SendToAsync(data, destination, token);
            Interlocked.Add(ref Metrics.DiscoveryBytesSent, data.Length);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            if (_logger.IsTrace) TraceAddressNotAvailable(destination, exception);
            throw;
        }
        catch (SocketException exception)
        {
            _logger.DebugError("Error sending data", exception);
            throw;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceAddressNotAvailable(IPEndPoint failedDestination, SocketException exception) =>
            _logger.TraceError($"Failed to send discv5 UDP packet to {failedDestination}", exception);
    }

    internal async IAsyncEnumerable<PooledUdpReceiveResult> ReadMessagesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        Interlocked.Increment(ref _activeReaders);
        try
        {
            await foreach (PooledUdpReceiveResult result in _inboundQueue.Reader.ReadAllAsync(token))
            {
                yield return result;
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeReaders) == 0)
            {
                ReleaseQueuedPackets();
            }
        }
    }

    public void Close()
    {
        _inboundQueue.Writer.TryComplete();
        if (Volatile.Read(ref _activeReaders) == 0)
        {
            ReleaseQueuedPackets();
        }
    }

    private void ReleaseQueuedPackets()
    {
        while (_inboundQueue.Reader.TryRead(out PooledUdpReceiveResult result))
        {
            result.Dispose();
        }
    }
}
