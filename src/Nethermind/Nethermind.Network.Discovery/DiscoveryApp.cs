// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public class DiscoveryApp : IDiscoveryApp, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly INetworkConfig _networkConfig;
    private readonly IIPResolver _ipResolver;
    private readonly IKademliaNodeSource _kademliaNodeSource;
    private readonly DiscoveryPersistenceManager _persistenceManager;
    private readonly IKademliaDiscv4Adapter _discv4Adapter;
    private readonly IKademlia<PublicKey, Node> _kademlia;
    private readonly Func<IChannel, NettyDiscoveryHandler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv4Services;
    private readonly CancellationTokenSource _stopCts;

    private NettyDiscoveryHandler? _discoveryHandler;
    private Task? _runningTask;

    public DiscoveryApp(
        ILifetimeScope rootScope,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IIPResolver ipResolver,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        Action<ContainerBuilder>? configureDiscv4Services = null)
    {
        _logger = logManager.GetClassLogger<DiscoveryApp>();
        _networkConfig = networkConfig;
        _ipResolver = ipResolver;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

        List<Node> bootNodes = [];
        NetworkNode[] bootnodes = networkConfig.Bootnodes;
        if (bootnodes.Length == 0)
        {
            if (_logger.IsWarn) _logger.Warn("No bootnodes specified in configuration");
        }

        for (int i = 0; i < bootnodes.Length; i++)
        {
            NetworkNode bootnode = bootnodes[i];
            if (!bootnode.IsEnode)
            {
                if (_logger.IsTrace) _logger.Trace($"Ignoring ENR in discovery V4: {bootnode}");
                continue;
            }

            if (bootnode.NodeId is null)
            {
                _logger.Warn($"Bootnode ignored because of missing node ID: {bootnode}");
                continue;
            }

            bootNodes.Add(new(bootnode.NodeId, bootnode.Host, bootnode.Port));
        }

        _discv4Services = rootScope.BeginLifetimeScope(
            (builder) =>
            {
                builder
                .AddModule(new DiscV4KademliaModule(nodeKey.PublicKey, bootNodes))
                .AddSingleton<DiscV4Services>();

                configureDiscv4Services?.Invoke(builder);
            });

        (_kademliaNodeSource, _persistenceManager, _discv4Adapter, _kademlia, _discoveryHandlerFactory) = _discv4Services.Resolve<DiscV4Services>();
        _kademlia.OnNodeRemoved += OnKademliaNodeRemoved;
    }

    /// <summary>
    /// Just a small class to make resolve easier
    /// </summary>
    private record DiscV4Services(
        IKademliaNodeSource NodeSource,
        DiscoveryPersistenceManager PersistenceManager,
        IKademliaDiscv4Adapter Discv4Adapter,
        IKademlia<PublicKey, Node> Kademlia,
        Func<IChannel, NettyDiscoveryHandler> NettyDiscoveryHandlerFactory
    )
    {
    }

    public async Task StartAsync()
    {
        try
        {
            await Initialize();
        }
        catch (Exception e)
        {
            _logger.Error("Error during discovery app start process", e);
            throw;
        }
    }

    public async Task StopAsync()
    {
        DetachEventHandlers();

        try
        {
            await _stopCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (_runningTask is not null)
            {
                await _runningTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error in discovery task", e);
        }

        _stopCts.Dispose();

        if (_logger.IsInfo) _logger.Info("Discovery shutdown complete. Please wait for all components to close");
    }

    private void DetachEventHandlers()
    {
        try
        {
            _discoveryHandler?.OnChannelActivated -= OnChannelActivated;
        }
        catch (Exception e)
        {
            _logger.Error("Error during discovery cleanup", e);
        }
    }

    string IStoppableService.Description => "discv4";

    public void AddNodeToDiscovery(Node node) => _kademlia.AddOrRefresh(node);

    private async Task Initialize()
    {
        IIPResolver.NethermindIp ip = await _ipResolver.Resolve();
        if (_logger.IsDebug)
            _logger.Debug($"Discovery    : udp://{ip.ExternalIp}:{_networkConfig.DiscoveryPort}");
        ThisNodeInfo.AddInfo("Discovery    :", $"udp://{ip.ExternalIp}:{_networkConfig.DiscoveryPort}");
    }

    protected virtual NettyDiscoveryHandler CreateDiscoveryHandler(IChannel channel)
    {
        NettyDiscoveryHandler discoveryHandler = _discoveryHandlerFactory(channel);
        _discv4Adapter.MsgSender = discoveryHandler;
        return discoveryHandler;
    }

    public void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = CreateDiscoveryHandler(channel);
        _discoveryHandler.OnChannelActivated += OnChannelActivated;

        channel.Pipeline
            .AddLast(new LoggingHandler(LogLevel.INFO))
            .AddLast(_discoveryHandler);
    }

    private void OnChannelActivated(object? sender, EventArgs e)
    {
        if (_logger.IsDebug) _logger.Debug("Activated discovery channel.");

        // Make sure this is non blocking code, otherwise netty will not process messages
        // Explicitly use TaskScheduler.Default, otherwise it will use dotnetty's task scheduler which have a habit of
        // not working sometimes.
        if (_stopCts.IsCancellationRequested) return;
        _runningTask = StartActivationAsync(_stopCts.Token);
    }

    private async Task StartActivationAsync(CancellationToken cancellationToken)
    {
        const string faultMessage = "Cannot activate channel.";

        try
        {
            await Task.Factory.StartNew(static state => ((DiscoveryApp)state!).ActivateAsync(), this, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            if (!cancellationToken.IsCancellationRequested && _logger.IsDebug) _logger.Debug("Discovery App initialized.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (_logger.IsInfo) _logger.Info(faultMessage);
            throw;
        }
    }

    private Task ActivateAsync() => ActivateAsync(_stopCts.Token);

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            //Step 1 - read nodes and stats from db
            await _persistenceManager.LoadPersistedNodes(cancellationToken);

            Task persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationToken);

            try
            {
                // Step 2 - run the standard kademlia routine
                await _kademlia.Run(cancellationToken);
            }
            finally
            {
                // Block until persistence is finished
                await persistenceTask;
            }
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("Discovery App stopped");
        }
        catch (Exception e)
        {
            _logger.DebugError("Error during discovery initialization", e);
        }
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken token) => _kademliaNodeSource.DiscoverNodes(token);

    private void OnKademliaNodeRemoved(object? sender, Node node) => NodeRemoved?.Invoke(sender, new NodeEventArgs(node));

    public event EventHandler<NodeEventArgs>? NodeRemoved;

    public async ValueTask DisposeAsync()
    {
        _kademlia.OnNodeRemoved -= OnKademliaNodeRemoved;
        await _discv4Services.DisposeAsync();
    }
}
