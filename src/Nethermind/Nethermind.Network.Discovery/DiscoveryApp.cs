// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public class DiscoveryApp : IDiscoveryApp
{
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ITimestamper _timestamper;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly IMessageSerializationService _messageSerializationService;
    private readonly INetworkStorage _discoveryStorage;
    private readonly INetworkConfig _networkConfig;
    private readonly INodeStatsManager _nodeStatsManager;
    private ILifetimeScope? _kademliaServices;

    private readonly List<Node> _bootNodes;
    private PublicKey _masterNode = null!;
    private readonly NodeRecord _selfNodeRecorrd;

    private KademliaDiscv4Adapter _discv4Adapter = null!;
    private IKademlia<PublicKey, Node> _kademlia = null!;

    private NettyDiscoveryHandler? _discoveryHandler;

    private readonly ILifetimeScope _rootLifetimeScope;
    private KademliaNodeSource _kademliaNodeSource = null!;
    private Task? _runningTask;
    private readonly IProcessExitSource _processExitSouce;

    public DiscoveryApp(
        NodeRecord selfNodeRecord,
        ILifetimeScope lifetimeScope,
        INodeStatsManager nodeStatsManager,
        IMessageSerializationService? msgSerializationService,
        INetworkStorage? discoveryStorage,
        INetworkConfig? networkConfig,
        IDiscoveryConfig? discoveryConfig,
        ITimestamper? timestamper,
        IProcessExitSource processExitSource,
        ILogManager? logManager)
    {
        _selfNodeRecorrd = selfNodeRecord;
        _rootLifetimeScope = lifetimeScope;
        _nodeStatsManager = nodeStatsManager;
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = _logManager.GetClassLogger();
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _messageSerializationService =
            msgSerializationService ?? throw new ArgumentNullException(nameof(msgSerializationService));
        _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        _processExitSouce = processExitSource ?? throw new ArgumentNullException(nameof(processExitSource));

        _bootNodes = new List<Node>();
        NetworkNode[] bootnodes = NetworkNode.ParseNodes(_discoveryConfig.Bootnodes, _logger);
        if (bootnodes.Length == 0)
        {
            if (_logger.IsWarn) _logger.Warn("No bootnodes specified in configuration");
            return;
        }

        for (int i = 0; i < bootnodes.Length; i++)
        {
            NetworkNode bootnode = bootnodes[i];
            if (bootnode.NodeId is null)
            {
                _logger.Warn($"Bootnode ignored because of missing node ID: {bootnode}");
            }

            _bootNodes.Add(new(bootnode.NodeId, bootnode.Host, bootnode.Port));
        }
    }

    public void Initialize(PublicKey masterPublicKey)
    {
        _masterNode = masterPublicKey;
        _kademliaServices = _rootLifetimeScope
            .BeginLifetimeScope((builder) => builder.AddModule(
                new DiscV4KademliaModule(_selfNodeRecorrd, _masterNode, _bootNodes)));

        _kademlia = _kademliaServices.Resolve<IKademlia<PublicKey, Node>>();
        _discv4Adapter = _kademliaServices.Resolve<KademliaDiscv4Adapter>();
        _kademliaNodeSource = _kademliaServices.Resolve<KademliaNodeSource>();
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
        _kademliaServices?.DisposeAsync();
    }

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
        _discoveryHandler = new NettyDiscoveryHandler(_discv4Adapter, channel, _messageSerializationService,
            _timestamper, _logManager);
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
        _runningTask = Task.Factory
            .StartNew(() => OnChannelActivated(_processExitSouce.Token), _processExitSouce.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
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

                    if (t.IsCompleted && !_processExitSouce.Token.IsCancellationRequested)
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
            // Step 1 - read nodes and stats from db
            await AddPersistedNodes(cancellationToken);

            Task persistenceTask = RunDiscoveryPersistenceCommit(cancellationToken);

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

    private async Task AddPersistedNodes(CancellationToken cancellationToken)
    {
        NetworkNode[] nodes = _discoveryStorage.GetPersistedNodes();
        foreach (NetworkNode networkNode in nodes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_discv4Adapter.NodesFilter.Set(networkNode.HostIp))
            {
                // Already seen this node ip recently
                continue;
            }

            Node node;
            try
            {
                node = new Node(networkNode.NodeId, networkNode.Host, networkNode.Port);
            }
            catch (Exception)
            {
                if (_logger.IsDebug)
                    _logger.Error(
                        $"ERROR/DEBUG peer could not be loaded for {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
                continue;
            }

            try
            {
                await _discv4Adapter.Ping(node, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception)
            {
                if (_logger.IsDebug)
                    _logger.Error(
                        $"ERROR/DEBUG error when pinging persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
                continue;
            }

            if (_logger.IsTrace)
                _logger.Trace($"Adding persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
        }

        if (_logger.IsDebug) _logger.Debug($"Added persisted discovery nodes: {nodes.Length}");
    }

    [Todo(Improve.Allocations, "Remove ToArray here - address as a part of the network DB rewrite")]
    private async Task RunDiscoveryPersistenceCommit(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
        PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_discoveryConfig.DiscoveryPersistenceInterval));

        while (!cancellationToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                _discoveryStorage.StartBatch();

                var nodes = _kademlia.IterateNodes().ToArray();
                _discoveryStorage.UpdateNodes(nodes.Select(x => new NetworkNode(x.Id, x.Host,
                    x.Port, _nodeStatsManager.GetNewPersistedReputation(x))).ToArray());

                _discoveryStorage.Commit();
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during discovery commit: {ex}");
            }
        }
    }

    public IAsyncEnumerable<Node> DiscoverNodes(CancellationToken token)
    {
        return _kademliaNodeSource.DiscoverNodes(token);
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}
