// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    private readonly IKademliaNodeSource _kademliaNodeSource;
    private readonly DiscoveryPersistenceManager _persistenceManager;
    private readonly IKademliaDiscv4Adapter _discv4Adapter;
    private readonly IKademlia<PublicKey, Node> _kademlia;
    private readonly Func<IChannel, NettyDiscoveryHandler> _discoveryHandlerFactory;
    private readonly ILifetimeScope _discv4Services;

    private NettyDiscoveryHandler? _discoveryHandler;
    private Task? _runningTask;
    private readonly IProcessExitSource _processExitSource;

    public DiscoveryApp(
        ILifetimeScope rootScope,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _networkConfig = networkConfig;
        _processExitSource = processExitSource;

        var bootNodes = new List<Node>();
        NetworkNode[] bootnodes = NetworkNode.ParseNodes(discoveryConfig.Bootnodes, _logger);
        if (bootnodes.Length == 0)
        {
            if (_logger.IsWarn) _logger.Warn("No bootnodes specified in configuration");
        }

        for (int i = 0; i < bootnodes.Length; i++)
        {
            NetworkNode bootnode = bootnodes[i];
            if (bootnode.NodeId is null)
            {
                _logger.Warn($"Bootnode ignored because of missing node ID: {bootnode}");
            }

            bootNodes.Add(new(bootnode.NodeId, bootnode.Host, bootnode.Port));
        }

        _discv4Services = rootScope.BeginLifetimeScope(
            (builder) => builder
                .AddModule(new DiscV4KademliaModule(nodeKey.PublicKey, bootNodes))
                .AddSingleton<DiscV4Services>()
        );

        (_kademliaNodeSource, _persistenceManager, _discv4Adapter, _kademlia, _discoveryHandlerFactory) = _discv4Services.Resolve<DiscV4Services>();
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

    public Task StartAsync()
    {
        try
        {
            Initialize();
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            _logger.Error("Error during discovery app start process", e);
            throw;
        }
    }

    public async Task StopAsync()
    {
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

        try
        {
            if (_discoveryHandler is not null)
            {
                _discoveryHandler.OnChannelActivated -= OnChannelActivated;
            }
        }
        catch (Exception e)
        {
            _logger.Error("Error during discovery cleanup", e);
        }

        if (_logger.IsInfo) _logger.Info("Discovery shutdown complete.. please wait for all components to close");
    }

    string IStoppableService.Description => "discv4";

    public void AddNodeToDiscovery(Node node)
    {
        _kademlia.AddOrRefresh(node);
    }

    private void Initialize()
    {
        if (_logger.IsDebug)
            _logger.Debug($"Discovery    : udp://{_networkConfig.ExternalIp}:{_networkConfig.DiscoveryPort}");
        ThisNodeInfo.AddInfo("Discovery    :", $"udp://{_networkConfig.ExternalIp}:{_networkConfig.DiscoveryPort}");
    }

    public void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = _discoveryHandlerFactory(channel);
        _discv4Adapter.MsgSender = _discoveryHandler;

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
        if (_processExitSource.Token.IsCancellationRequested) return;
        _runningTask = Task.Factory
            .StartNew(() => OnChannelActivated(_processExitSource.Token), _processExitSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith
            (
                t =>
                {
                    if (t.IsFaulted)
                    {
                        string faultMessage = "Cannot activate channel.";
                        _logger.Info(faultMessage);
                        throw t.Exception ??
                              (Exception)new NetworkingException(faultMessage, NetworkExceptionType.Discovery);
                    }

                    if (t.IsCompleted && !_processExitSource.Token.IsCancellationRequested)
                    {
                        _logger.Debug("Discovery App initialized.");
                    }
                }
            );
    }

    private async Task OnChannelActivated(CancellationToken cancellationToken)
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
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error during discovery initialization", e);
        }
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken token)
    {
        return _kademliaNodeSource.DiscoverNodes(token);
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
    public ValueTask DisposeAsync()
    {
        return _discv4Services.DisposeAsync();
    }
}
