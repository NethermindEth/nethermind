// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Manages connections (Netty <see cref="IChannel"/>) allocated for all Discovery protocol versions.
/// </summary>
/// <remarks> Not thread-safe </remarks>
public class DiscoveryConnectionsPool : IConnectionsPool
{
    private readonly ILogger _logger;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly IPAddress _ip;
    private readonly McsLock _lock = new McsLock();
    private readonly Dictionary<int, Task<IChannel>> _byPort = new();

    public DiscoveryConnectionsPool(ILogger logger, INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig)
    {
        _logger = logger;
        _networkConfig = networkConfig;
        _discoveryConfig = discoveryConfig;
        _ip = IPAddress.Parse(networkConfig.LocalIp!);
    }

    public async Task<IChannel> BindAsync(Bootstrap bootstrap, int port)
    {
        Task<IChannel> task = GetChannel(bootstrap, port);

        return await task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.Error(
                    $"Error when establishing discovery connection on Address: {_ip}({_networkConfig.LocalIp}:{port})",
                    t.Exception
                );
            }

            return t.Result;
        });
    }

    private Task<IChannel> GetChannel(Bootstrap bootstrap, int port)
    {
        Task<IChannel> task;

        using (var _ = _lock.Acquire())
        {
            if (_byPort.TryGetValue(port, out task!))
            {
                return task;
            }

            _logger.Error($"Binding port {port}");
            task = bootstrap.BindAsync(_ip, port);
            _byPort.Add(port, task);
        }

        return task;
    }

    public async Task StopAsync()
    {
        foreach ((var port, Task<IChannel> channel) in _byPort)
            await StopAsync(port, channel);
    }

    private async Task StopAsync(int port, Task<IChannel> channelTask)
    {
        try
        {
            IChannel channel = await channelTask;
            _logger.Info($"Stopping discovery udp channel on port {port}");

            Task closeTask = channel.CloseAsync();
            CancellationTokenSource delayCancellation = new();

            if (await Task.WhenAny(closeTask, Task.Delay(_discoveryConfig.UdpChannelCloseTimeout, delayCancellation.Token)) != closeTask)
                _logger.Error($"Could not close udp connection in {_discoveryConfig.UdpChannelCloseTimeout} milliseconds");
            else
                await delayCancellation.CancelAsync();
        }
        catch (Exception e)
        {
            _logger.Error("Error during udp channel stop process", e);
        }
    }
}
