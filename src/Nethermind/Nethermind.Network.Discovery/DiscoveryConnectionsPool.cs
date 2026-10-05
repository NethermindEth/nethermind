// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Manages UDP sockets (<see cref="IDatagramSocket"/>) allocated for all Discovery protocol versions.
/// </summary>
/// <remarks> Not thread-safe </remarks>
internal sealed class DiscoveryConnectionsPool(
    ILogger logger,
    IDiscoveryConfig discoveryConfig,
    NetworkListenerState listenerState)
{
    // https://github.com/ethereum/devp2p/blob/master/discv4.md#wire-protocol
    // https://github.com/ethereum/devp2p/blob/master/discv5/discv5-wire.md#udp-communication
    internal const int MaxPacketSize = 1280;

    // Must exceed MaxPacketSize so that oversized datagrams are detected instead of being truncated to a valid size.
    private const int ReceiveBufferSize = 2048 * 2;

    private readonly ILogger _logger = logger;
    private readonly IDiscoveryConfig _discoveryConfig = discoveryConfig;
    private readonly NetworkListenerState _listenerState = listenerState;
    private readonly Dictionary<int, Listener> _byPort = [];

    /// <summary>
    /// Binds a socket to <paramref name="port"/>, falling back to the listener's fallback address, and starts
    /// passing every valid received datagram to <paramref name="onReceive"/>.
    /// </summary>
    /// <param name="socketFactory">Creates an unbound socket suitable for the given local address.</param>
    /// <param name="port">The local port to bind.</param>
    /// <param name="onReceive">Takes ownership of each received datagram; called on the receive loop.</param>
    public IDatagramSocket Bind(
        Func<IPAddress, IDatagramSocket> socketFactory,
        int port,
        Action<PooledUdpReceiveResult> onReceive)
    {
        if (_byPort.TryGetValue(port, out Listener? existing)) return existing.Socket;

        IDatagramSocket socket = BindWithFallback(socketFactory, port);
        Task receiveTask = Task.Run(() => ReceiveAsync(socket, onReceive));
        if (socket.LocalEndpoint is { } endpoint)
        {
            _ = _listenerState.TrackDiscoveryAddress(endpoint.Address, receiveTask);
        }

        _byPort.Add(port, new Listener(socket, receiveTask));
        return socket;
    }

    private IDatagramSocket BindWithFallback(Func<IPAddress, IDatagramSocket> socketFactory, int port)
    {
        IPAddress preferredAddress = _listenerState.PreferredAddress;
        IPAddress fallbackAddress = _listenerState.FallbackAddress;
        try
        {
            try
            {
                return Bind(socketFactory, preferredAddress, port);
            }
            catch (Exception e) when (!preferredAddress.Equals(fallbackAddress))
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to bind discovery UDP channel on {preferredAddress}:{port}. Retrying on {fallbackAddress}:{port}. {e}");
                return Bind(socketFactory, fallbackAddress, port);
            }
        }
        catch (Exception e)
        {
            _logger.Error($"Error when establishing discovery connection on port {port}", e);
            throw;
        }
    }

    private static IDatagramSocket Bind(Func<IPAddress, IDatagramSocket> socketFactory, IPAddress address, int port)
    {
        IDatagramSocket socket = socketFactory(address);
        try
        {
            return NetworkHelper.HandlePortTakenError(() =>
            {
                socket.Bind(new IPEndPoint(address, port));
                return socket;
            }, port);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveAsync(IDatagramSocket socket, Action<PooledUdpReceiveResult> onReceive)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        try
        {
            while (true)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer);
                }
                // Windows reports ICMP errors caused by earlier sends on the next receive, and fails receives of datagrams
                // larger than the buffer; neither affects later datagrams.
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.NetworkReset or SocketError.MessageSize)
                {
                    if (_logger.IsTrace) _logger.Trace($"Ignoring discovery receive error: {e.SocketErrorCode}");
                    continue;
                }

                Interlocked.Add(ref Metrics.DiscoveryBytesReceived, result.ReceivedBytes);
                if (result.ReceivedBytes is 0 or > MaxPacketSize)
                {
                    // Potential cases where this can happen:
                    // - Neighbors response containing 16+ nodes in a single packet
                    if (_logger.IsDebug) _logger.Debug($"Skipping discovery packet of invalid size: {result.ReceivedBytes}");
                    continue;
                }

                try
                {
                    onReceive(PooledUdpReceiveResult.Copy(buffer.AsSpan(0, result.ReceivedBytes), NormalizeEndpoint((IPEndPoint)result.RemoteEndPoint)));
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Exception when processing discovery messages", e);
                }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException { SocketErrorCode: SocketError.OperationAborted })
        {
        }
        catch (SocketException e)
        {
            if (_logger.IsTrace) _logger.Trace($"Exception when receiving discovery messages (SocketException): {e}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Exception when receiving discovery messages", e);
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>
    /// Reduces an IPv4-mapped IPv6 sender address (<c>::ffff:a.b.c.d</c>, reported by dual-stack sockets) to its plain IPv4 form.
    /// </summary>
    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint)
    {
        IPAddress address = endpoint.Address.NormalizeMappedIPv4();
        return ReferenceEquals(address, endpoint.Address) ? endpoint : new IPEndPoint(address, endpoint.Port);
    }

    public async Task StopAsync()
    {
        foreach ((int port, Listener listener) in _byPort)
            await StopAsync(port, listener);
    }

    private async Task StopAsync(int port, Listener listener)
    {
        _logger.Info($"Stopping discovery udp channel on port {port}");
        listener.Socket.Dispose();

        try
        {
            await listener.ReceiveTask.WaitAsync(TimeSpan.FromMilliseconds(_discoveryConfig.UdpChannelCloseTimeout));
        }
        catch (TimeoutException)
        {
            _logger.Error($"Could not close udp connection in {_discoveryConfig.UdpChannelCloseTimeout} milliseconds");
        }
    }

    private sealed record Listener(IDatagramSocket Socket, Task ReceiveTask);
}
