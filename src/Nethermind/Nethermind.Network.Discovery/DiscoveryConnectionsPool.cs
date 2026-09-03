// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Manages connections (Netty <see cref="IChannel"/>) allocated for all Discovery protocol versions.
/// </summary>
/// <remarks> Not thread-safe </remarks>
internal sealed class DiscoveryConnectionsPool(
    ILogger logger,
    IDiscoveryConfig discoveryConfig,
    NetworkListenerState listenerState)
{
    private readonly ILogger _logger = logger;
    private readonly IDiscoveryConfig _discoveryConfig = discoveryConfig;
    private readonly NetworkListenerState _listenerState = listenerState;
    private readonly Dictionary<int, Task<IChannel>> _byPort = [];

    public async Task<IChannel> BindAsync(
        Func<Bootstrap> bootstrapFactory,
        Func<IPAddress, IChannel> channelFactory,
        int port)
    {
        if (_byPort.TryGetValue(port, out Task<IChannel>? task)) return await task;

        task = BindWithFallbackAsync(bootstrapFactory, channelFactory, port);
        _byPort.Add(port, task);

        try
        {
            return await task;
        }
        catch
        {
            _byPort.Remove(port);
            throw;
        }
    }

    private async Task<IChannel> BindWithFallbackAsync(
        Func<Bootstrap> bootstrapFactory,
        Func<IPAddress, IChannel> channelFactory,
        int port)
    {
        IPAddress preferredAddress = _listenerState.PreferredAddress;
        IPAddress fallbackAddress = _listenerState.FallbackAddress;
        try
        {
            try
            {
                return await BindAsync(bootstrapFactory, channelFactory, preferredAddress, port);
            }
            catch (Exception e) when (!preferredAddress.Equals(fallbackAddress))
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to bind discovery UDP channel on {preferredAddress}:{port} ({e.Message}). Retrying on {fallbackAddress}:{port}.");
                return await BindAsync(bootstrapFactory, channelFactory, fallbackAddress, port);
            }
        }
        catch (Exception e)
        {
            _logger.Error($"Error when establishing discovery connection on port {port}", e);
            throw;
        }
    }

    private async Task<IChannel> BindAsync(
        Func<Bootstrap> bootstrapFactory,
        Func<IPAddress, IChannel> channelFactory,
        IPAddress address,
        int port)
    {
        IChannel? createdChannel = null;
        Bootstrap bootstrap = bootstrapFactory()
            .ChannelFactory(() => createdChannel = channelFactory(address));
        try
        {
            IChannel channel = await NetworkHelper.HandlePortTakenError(() => bootstrap.BindAsync(address, port), port);
            IPEndPoint? endpoint = channel.LocalAddress switch
            {
                IPEndPoint ipEndpoint => ipEndpoint,
                IIPEndpointSource source => source.IPEndpoint,
                _ => (channel as IIPEndpointSource)?.IPEndpoint
            };
            if (endpoint is not null)
            {
                _ = _listenerState.TrackDiscoveryAddress(endpoint.Address, channel.CloseCompletion);
            }

            return channel;
        }
        catch
        {
            await CloseFailedChannel(createdChannel);
            throw;
        }
    }

    private async Task CloseFailedChannel(IChannel? channel)
    {
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.CloseAsync();
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to close an unsuccessful discovery bind attempt. {e}");
        }
    }

    public async Task StopAsync()
    {
        foreach ((int port, Task<IChannel> channel) in _byPort)
            await StopAsync(port, channel);
    }

    private async Task StopAsync(int port, Task<IChannel> channelTask)
    {
        try
        {
            IChannel channel = await channelTask;
            _logger.Info($"Stopping discovery udp channel on port {port}");

            Task closeTask = channel.CloseAsync();
            using CancellationTokenSource delayCancellation = new();

            if (await Task.WhenAny(closeTask, Task.Delay(_discoveryConfig.UdpChannelCloseTimeout, delayCancellation.Token)) != closeTask)
                _logger.Error($"Could not close udp connection in {_discoveryConfig.UdpChannelCloseTimeout} milliseconds");
            else
                delayCancellation.Cancel();
        }
        catch (Exception e)
        {
            _logger.Error("Error during udp channel stop process", e);
        }
    }
}
