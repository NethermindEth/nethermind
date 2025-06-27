// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Open.Nat;

namespace Nethermind.UPnP.Plugin;

[RunnerStepDependencies()]
public class UPnPStep(
    IProcessExitSource processExitSource,
    INetworkConfig networkConfig,
    ILogManager logManager
) : IStep, IAsyncDisposable
{
    private static readonly TimeSpan ExpirationRate = TimeSpan.FromMinutes(10);
    private readonly ILogger _logger = logManager.GetClassLogger<UPnPStep>();

    // Routers tend to clean mapping, so we need to periodically
    private readonly PeriodicTimer _timer = new(ExpirationRate);

    public Task Execute(CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(RunRefreshLoop);

        return Task.CompletedTask;
    }

    private async Task RunRefreshLoop()
    {
        do
        {
            try
            {
                await SetupMapping();
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Error("Unable to setup UPnP mapping.", exception);
            }

            await _timer.WaitForNextTickAsync(processExitSource.Token);
            if (processExitSource.Token.IsCancellationRequested)
            {
                break;
            }
        } while (true);
    }

    private async Task SetupMapping()
    {
        if (_logger.IsInfo) _logger.Info("Setting up port forwarding via UPnP...");

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        NatDiscoverer discoverer = new();
        NatDevice device;

        try
        {
            device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);
        }
        catch (NatDeviceNotFoundException)
        {
            if (_logger.IsInfo) _logger.Info("Unable to find UPnP support. Automatic port forwarding not available.");
            return;
        }

        await device.CreatePortMapAsync(new Mapping(
            Protocol.Tcp,
            networkConfig.P2PPort,
            networkConfig.P2PPort,
            ExpirationRate.Milliseconds + 10000,
            "Nethermind P2P"));
        await device.CreatePortMapAsync(new Mapping(
            Protocol.Udp,
            networkConfig.DiscoveryPort,
            networkConfig.DiscoveryPort,
            ExpirationRate.Milliseconds + 10000,
            "Nethermind Discovery"));

        if (_logger.IsDebug) _logger.Debug("UPnP mapping added");
    }

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
