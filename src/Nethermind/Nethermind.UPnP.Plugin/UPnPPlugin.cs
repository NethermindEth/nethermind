// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Open.Nat;

namespace Nethermind.UPnP.Plugin;

[RunnerStepDependencies(typeof(InitializeBlockTree))]
public class UPnPPlugin : Module, INethermindPlugin, IStep
{
    public string Name => "UPnP";
    public string Description => "Automatic port forwarding with UPnP";
    public string Author => "Nethermind";
    public bool Enabled => _networkConfig.EnableUPnP;

    // Routers tend to clean mapping, so we need to periodically
    private readonly TimeSpan ExpirationRate = TimeSpan.FromMinutes(10);
    private PeriodicTimer? _timer = null;
    private readonly INetworkConfig _networkConfig = new NetworkConfig();
    private ILogger _logger = NullLogger.Instance;

    public UPnPPlugin(INetworkConfig networkConfig, ILogger logger)
    {
        _networkConfig = networkConfig;
        _logger = logger;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(ExpirationRate);

        do
        {
            try
            {
                await SetupMapping(cancellationToken);
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Error("Unable to setup UPnP mapping.", exception);
            }

            await _timer.WaitForNextTickAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        } while (true);
    }

    private async Task SetupMapping(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("Setting up port forwarding via UPnP...");

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            _networkConfig.P2PPort,
            _networkConfig.P2PPort,
            ExpirationRate.Milliseconds + 10000,
            "Nethermind P2P"));
        await device.CreatePortMapAsync(new Mapping(
            Protocol.Udp,
            _networkConfig.DiscoveryPort,
            _networkConfig.DiscoveryPort,
            ExpirationRate.Milliseconds + 10000,
            "Nethermind Discovery"));

        if (_logger.IsDebug) _logger.Debug("UPnP mapping added");
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder.RegisterIStep(typeof(UPnPPlugin));
    }

    public IModule? Module => this;
}
