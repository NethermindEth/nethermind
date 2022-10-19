using System.Net;
using MathGmp.Native;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Open.Nat;

namespace Nethermind.UPnP.Plugin;

public class UPnPPlugin : INethermindPlugin
{
    public string Name => "UPnP";
    public string Description => "Automatic port forwarding with UPnP";
    public string Author => "Nethermind";

    // Routers tend to clean mapping, so we need to periodically
    private readonly TimeSpan ExpirationRate = TimeSpan.FromMinutes(10);
    private CancellationTokenSource _cancellationTokenSource = new();
    private INetworkConfig _networkConfig = new NetworkConfig();
    private ILogger _logger = NullLogger.Instance;

    public Task Init(INethermindApi api)
    {
        _networkConfig = api.Config<INetworkConfig>();
        _logger = api.LogManager.GetClassLogger<UPnPPlugin>();

        if (_networkConfig.EnableUPnP)
        {
            Task.Factory.StartNew(RunRefreshLoop, TaskCreationOptions.LongRunning);
        }

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

            if (await _cancellationTokenSource.Token.WaitHandle.WaitOneAsync(ExpirationRate, CancellationToken.None))
            {
                break;
            }
        } while (true);
    }

    private async Task SetupMapping()
    {
        if (_logger.IsInfo) _logger.Info("Setting up port forwarding via UPnP...");

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
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

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        return ValueTask.CompletedTask;
    }
}
