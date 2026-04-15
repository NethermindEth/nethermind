// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Manages connections (Netty <see cref="IChannel"/>) allocated for all Discovery protocol versions.
/// </summary>
/// <remarks> Not thread-safe </remarks>
public class DiscoveryConnectionsPool(ILogger logger, INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig) : IConnectionsPool
{
    private readonly ILogger _logger = logger;
    private readonly INetworkConfig _networkConfig = networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig = discoveryConfig;
    private readonly IPAddress _ip = IPAddress.Parse(networkConfig.LocalIp!);
    private readonly Dictionary<int, Task<IChannel>> _byPort = new();

    public async Task<IChannel> BindAsync(Bootstrap bootstrap, int port)
    {
        if (_byPort.TryGetValue(port, out Task<IChannel>? task)) return await task;

        task = BindAsync(bootstrap, port, _ip);
        _byPort.Add(port, task);

        return await task;
    }

    private async Task<IChannel> BindAsync(Bootstrap bootstrap, int port, IPAddress ip)
    {
        try
        {
            return await NetworkHelper.HandlePortTakenError(() => bootstrap.BindAsync(ip, port), port);
        }
        catch (Exception e)
        {
            _logger.Error($"Error when establishing discovery connection on Address: {ip}({_networkConfig.LocalIp}:{port})", e);
            throw;
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
