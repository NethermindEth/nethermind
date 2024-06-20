// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Lantern.Discv5.WireProtocol.Connection;
using Microsoft.Extensions.Logging;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Adapter, implementing send-only <see cref="IUdpConnection"/> using DotNetty <see cref="IChannel"/>.
/// </summary>
public class NettySendOnlyConnection: IUdpConnection
{
    private readonly ILogger<NettySendOnlyConnection> _logger;
    private readonly IChannel _nettyChannel;
    private readonly IByteBufferAllocator _bufferAllocator = PooledByteBufferAllocator.Default;

    public NettySendOnlyConnection(ILogger<NettySendOnlyConnection> logger, IChannel nettyChannel)
    {
        _logger = logger;
        _nettyChannel = nettyChannel;
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        UdpConnection.ValidatePacketSize(data);

        IByteBuffer packet = _bufferAllocator.Buffer(data.Length, data.Length);
        packet.WriteBytes(data);

        try
        {
            await _nettyChannel.WriteAndFlushAsync(packet);
        }
        catch (SocketException se)
        {
            _logger.LogError(se, "Error sending data");
            throw;
        }
    }

    public Task ListenAsync(CancellationToken token = default)
    {
        throw new NotSupportedException($"{nameof(NettySendOnlyConnection)}.{nameof(ListenAsync)} should not be called.");
    }

    public IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync(CancellationToken token = default)
    {
        throw new NotSupportedException($"{nameof(NettySendOnlyConnection)}.{nameof(ReadMessagesAsync)} should not be called.");
    }

    public void Close()
    {
        throw new NotSupportedException($"{nameof(NettySendOnlyConnection)}.{nameof(ReadMessagesAsync)} should not be called.");
    }
}
