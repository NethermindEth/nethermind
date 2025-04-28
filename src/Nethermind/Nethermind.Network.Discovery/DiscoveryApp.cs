// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Autofac;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels;
using Microsoft.ClearScript;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using Prometheus;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public class DiscoveryApp : IDiscoveryApp
{
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ITimestamper _timestamper;
    // private readonly INodesLocator _nodesLocator;
    // private readonly IDiscoveryManager _discoveryManager;
    // private readonly INodeTable _nodeTable;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly IMessageSerializationService _messageSerializationService;
    // private readonly ICryptoRandom _cryptoRandom;
    private readonly INetworkStorage _discoveryStorage;
    private readonly INetworkConfig _networkConfig;
    private IContainer? _kademliaServices;

    private readonly IList<Node> _bootNodes;
    private PublicKey _masterNode = null!;
    private readonly NodeRecord _selfNodeRecorrd;

    private KademliaDiscv4MessageReceiver _discv4MessageReceiver = null!;
    private KademliaDiscv4MessageSender _discv4MessageSender = null!;
    private IKademlia<Node> _kademlia = null!;
    private ILookupAlgo2<Node> _lookup2 = null!;

    private NettyDiscoveryHandler? _discoveryHandler;
    private Task? _storageCommitTask;

    public DiscoveryApp(
        NodeRecord selfNodeRecord,
        INodesLocator nodesLocator,
        IDiscoveryManager? discoveryManager,
        INodeTable? nodeTable,
        IMessageSerializationService? msgSerializationService,
        ICryptoRandom? cryptoRandom,
        INetworkStorage? discoveryStorage,
        INetworkConfig? networkConfig,
        IDiscoveryConfig? discoveryConfig,
        ITimestamper? timestamper,
        ILogManager? logManager)
    {
        _selfNodeRecorrd = selfNodeRecord;
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = _logManager.GetClassLogger();
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        // _nodesLocator = nodesLocator ?? throw new ArgumentNullException(nameof(nodesLocator));
        // _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
        // _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _messageSerializationService =
            msgSerializationService ?? throw new ArgumentNullException(nameof(msgSerializationService));
        // _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
        _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _bootNodes = new List<Node>();
        _discoveryStorage.StartBatch();

        // NetworkNode[] bootnodes = NetworkNode.ParseNodes(_discoveryConfig.Bootnodes, _logger);
        NetworkNode[] bootnodes = NetworkNode.ParseNodes("enode://8cd847302089d4906c5eb3125770b067fbcb7dc6bd62dfd3517483cc2e6acae6141a5fb4061f76825ea9f585d157b625f84f976fb6aa1582dc87b0d0b652f51f@127.0.0.1:40404", _logger);
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
        // enr:-Iq4QH5BqYiMrk3JM9PjbWQywalXCmIJEls45OVyd-DOZ662I-h9Te3B3l_DUYb69qODpUJXqROXZ-bsl0KTEsXl4JyGAZZ6r115gmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQNC_Da2PwnTdLvnKpys14XtqIGoZqdngnIMh6cPhVwK3oN1ZHCCo9c
    }

    private class NodeNodeHashProvider : INodeHashProvider<Node>
    {
        public ValueHash256 GetHash(Node node)
        {
            return node.Id.Hash;
        }
    }

    public void Initialize(PublicKey masterPublicKey)
    {
        // _discoveryManager.NodeDiscovered += OnNodeDiscovered;
        _masterNode = masterPublicKey;
        /*
        _nodeTable.Initialize(masterPublicKey);
        if (_nodeTable.MasterNode is null)
        {
            throw new NetworkingException(
                "Discovery node table initialization failed - master node is null",
                NetworkExceptionType.Discovery);
        }

        _nodesLocator.Initialize(_nodeTable.MasterNode);
        */

        _kademliaServices = new ContainerBuilder()
            .AddModule(new KademliaModule<Node>())
            .AddSingleton<INodeHashProvider<Node>, NodeNodeHashProvider>()
            .AddSingleton<ITimestamper>(_timestamper)
            .AddSingleton<INetworkConfig>(_networkConfig)
            .AddSingleton<ILogManager>(_logManager)
            .AddSingleton(_selfNodeRecorrd)
            .AddSingleton<KademliaConfig<Node>>(new KademliaConfig<Node>()
            {
                CurrentNodeId = new Node(_masterNode, "127.0.0.1", 9999, true)
            })
            .AddSingleton<IKademliaMessageSender<Node>, KademliaDiscv4MessageSender>()
            .AddSingleton<KademliaDiscv4MessageReceiver>()
            .Build();

        _kademlia = _kademliaServices.Resolve<IKademlia<Node>>();
        _lookup2 = _kademliaServices.Resolve<ILookupAlgo2<Node>>();
        _discv4MessageReceiver = _kademliaServices.Resolve<KademliaDiscv4MessageReceiver>();
        _discv4MessageSender = _kademliaServices.Resolve<KademliaDiscv4MessageSender>();

        // TODO: Setup kademlia here
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
        if (_logger.IsDebug) _logger.Debug("Stopping discovery timer");
        if (_logger.IsDebug) _logger.Debug("Stopping discovery persistence timer");

        _appShutdownSource.Cancel();

        if (_storageCommitTask is not null)
        {
            await _storageCommitTask.ContinueWith(x =>
            {
                if (x.IsFailedButNotCanceled())
                {
                    if (_logger.IsError) _logger.Error("Error during discovery persistence stop.", x.Exception);
                }
            });
        }

        Cleanup();
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

        NetworkChange.NetworkAvailabilityChanged += ResetUnreachableStatus;
    }

    private void ResetUnreachableStatus(object? sender, NetworkAvailabilityEventArgs e)
    {
        /*
        if (!e.IsAvailable)
        {
            return;
        }

        foreach (INodeLifecycleManager unreachable in _discoveryManager.GetNodeLifecycleManagers().Where(static x => x.State == NodeLifecycleState.Unreachable))
        {
            unreachable.ResetUnreachableStatus();
        }
        */
    }

    public void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = new NettyDiscoveryHandler(_discv4MessageReceiver, channel, _messageSerializationService,
            _timestamper, _logManager);

        _discv4MessageSender.MsgSender = _discoveryHandler;
        _discoveryHandler.OnChannelActivated += OnChannelActivated;

        channel.Pipeline
            .AddLast(new LoggingHandler(LogLevel.INFO))
            .AddLast(_discoveryHandler);
    }

    private readonly CancellationTokenSource _appShutdownSource = new();

    private void OnChannelActivated(object? sender, EventArgs e)
    {
        if (_logger.IsDebug) _logger.Debug("Activated discovery channel.");

        // Make sure this is non blocking code, otherwise netty will not process messages
        // Explicitly use TaskScheduler.Default, otherwise it will use dotnetty's task scheduler which have a habit of
        // not working sometimes.
        Task.Factory
            .StartNew(() => OnChannelActivated(_appShutdownSource.Token), _appShutdownSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
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

                    if (t.IsCompleted && !_appShutdownSource.IsCancellationRequested)
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
            AddPersistedNodes(cancellationToken);

            //Step 2 - initialize bootnodes
            if (_logger.IsDebug) _logger.Debug("Initializing bootnodes.");
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (await InitializeBootnodes(cancellationToken))
                {
                    break;
                }

                //Check if we were able to communicate with any trusted nodes or persisted nodes
                //if so no need to replay bootstrapping, we can start discovery process
                /*
                if (_discoveryManager.GetOrAddNodeLifecycleManagers(static x => x.State == NodeLifecycleState.Active).Count != 0)
                {
                    break;
                }
                */

                _logger.Warn("Could not communicate with any nodes (bootnodes, trusted nodes, persisted nodes).");
                await Task.Delay(1000, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            InitializeDiscoveryPersistenceTimer();
            InitializeDiscoveryTimer();
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error during discovery initialization", e);
        }
    }

    private void AddPersistedNodes(CancellationToken cancellationToken)
    {
        NetworkNode[] nodes = _discoveryStorage.GetPersistedNodes();
        foreach (NetworkNode networkNode in nodes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_discv4MessageSender.NodesFilter.Set(networkNode.HostIp))
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

            /*
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node, true);
            if (manager is null)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug(
                        $"Skipping persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}, manager couldn't be created");
                }

                continue;
            }

            manager.NodeStats.CurrentPersistedNodeReputation = networkNode.Reputation;
            */
            if (_logger.IsTrace)
                _logger.Trace($"Adding persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
        }

        if (_logger.IsDebug) _logger.Debug($"Added persisted discovery nodes: {nodes.Length}");
    }

    private void InitializeDiscoveryTimer()
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery timer");
        _ = RunDiscoveryProcess();
    }

    private void InitializeDiscoveryPersistenceTimer()
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
        _storageCommitTask = RunDiscoveryPersistenceCommit();
    }

    private void Cleanup()
    {
        try
        {
            if (_discoveryHandler is not null)
            {
                _discoveryHandler.OnChannelActivated -= OnChannelActivated;
            }

            NetworkChange.NetworkAvailabilityChanged -= ResetUnreachableStatus;
        }
        catch (Exception e)
        {
            _logger.Error("Error during discovery cleanup", e);
        }
    }

    private async Task<bool> InitializeBootnodes(CancellationToken cancellationToken)
    {
        /*
        foreach (var bootNode in _bootNodes)
        {
            _kademlia.AddOrRefresh(bootNode);
        }
        */

        //Wait for pong message to come back from Boot nodes
        /*
    int maxWaitTime = _discoveryConfig.BootnodePongTimeout;
    int itemTime = maxWaitTime / 100;
    for (int i = 0; i < 100; i++)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            break;
        }

        if (managers.Any(static x => x.State == NodeLifecycleState.Active))
        {
            break;
        }

        if (_discoveryManager.GetOrAddNodeLifecycleManagers(static x => x.State == NodeLifecycleState.Active).Count != 0)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    "Was not able to connect to any of the bootnodes, but successfully connected to at least one persisted node.");
            break;
        }

        if (_logger.IsTrace) _logger.Trace($"Waiting {itemTime} ms for bootnodes to respond");

        try
        {
            await Task.Delay(itemTime, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    int reachedNodeCounter = 0;
    for (int i = 0; i < managers.Count; i++)
    {
        INodeLifecycleManager manager = managers[i];
        if (manager.State != NodeLifecycleState.Active)
        {
            if (_logger.IsTrace)
                _logger.Trace($"Could not reach bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
        }
        else
        {
            if (_logger.IsTrace)
                _logger.Trace($"Reached bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
            reachedNodeCounter++;
        }
    }
        */

        /*
        if (_logger.IsInfo)
            _logger.Info(
                $"Connected to {reachedNodeCounter} bootnodes, {_discoveryManager.GetOrAddNodeLifecycleManagers(static x => x.State == NodeLifecycleState.Active).Count} trusted/persisted nodes");
        return reachedNodeCounter > 0;
                */

        await _kademlia.Bootstrap(cancellationToken);
        return true;
    }

    private Task RunDiscoveryProcess()
    {
        return Task.CompletedTask;
        /*
        byte[] randomId = new byte[64];
        CancellationToken cancellationToken = _appShutdownSource.Token;
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));

        long lastTickMs = Environment.TickCount64;
        long waitTimeTimeMs = 10;
        while (!cancellationToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(cancellationToken))
        {
            long currentTickMs = Environment.TickCount64;
            long elapsedMs = currentTickMs - lastTickMs;
            if (elapsedMs < waitTimeTimeMs)
            {
                // TODO: Change timer time in .NET 8.0 to avoid this https://github.com/dotnet/runtime/pull/82560
                // Wait for the remaining time
                await Task.Delay((int)(waitTimeTimeMs - elapsedMs), cancellationToken);
            }

            try
            {
                if (_logger.IsTrace) _logger.Trace("Running discovery process.");

                await _nodesLocator.LocateNodesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during discovery process: {e}");
            }

            try
            {
                if (_logger.IsTrace) _logger.Trace("Running refresh process.");

                _cryptoRandom.GenerateRandomBytes(randomId);
                await _nodesLocator.LocateNodesAsync(randomId, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during discovery refresh process: {e}");
            }

            int nodesCountAfterDiscovery = _nodeTable.Buckets.Sum(static x => x.BondedItemsCount);
            waitTimeTimeMs =
                nodesCountAfterDiscovery < 16
                    ? 10
                    : nodesCountAfterDiscovery < 128
                        ? 100
                        : nodesCountAfterDiscovery < 256
                            ? 1000
                            : _discoveryConfig.DiscoveryInterval;

            lastTickMs = Environment.TickCount64;
        }
        */
    }

    [Todo(Improve.Allocations, "Remove ToArray here - address as a part of the network DB rewrite")]
    private async Task RunDiscoveryPersistenceCommit()
    {
        CancellationToken cancellationToken = _appShutdownSource.Token;
        PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_discoveryConfig.DiscoveryPersistenceInterval));

        while (!cancellationToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                /*
                IReadOnlyCollection<INodeLifecycleManager> managers = _discoveryManager.GetNodeLifecycleManagers();
                DateTime utcNow = DateTime.UtcNow;
                //we need to update all notes to update reputation
                _discoveryStorage.UpdateNodes(managers.Select(x => new NetworkNode(x.ManagedNode.Id, x.ManagedNode.Host,
                    x.ManagedNode.Port, x.NodeStats.NewPersistedNodeReputation(utcNow))).ToArray());

                if (!_discoveryStorage.AnyPendingChange())
                {
                    if (_logger.IsTrace) _logger.Trace("No changes in discovery storage, skipping commit.");
                    continue;
                }

                _discoveryStorage.Commit();
                _discoveryStorage.StartBatch();
                */
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during discovery commit: {ex}");
            }
        }
    }

    /*
    private void OnNodeDiscovered(object? sender, NodeEventArgs e)
    {
        NodeAdded?.Invoke(this, e);
    }

    private event EventHandler<NodeEventArgs>? NodeAdded;
    */

    private Gauge _kademliaSize = Prometheus.Metrics.CreateGauge("kademlia_size", "kad size");
    private Counter _kademliaDiscoveredNodes = Prometheus.Metrics.CreateCounter("kademlia_discovered_nodes", "Discovered");

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Starting discover nodes");
        Channel<Node> ch = Channel.CreateBounded<Node>(64);
        ConcurrentDictionary<ValueHash256, ValueHash256> _writtenNodes = new();
        int duplicated = 0;
        int total = 0;
        int fromNewNode = 0;

        void handler(object? _, Node addedNode)
        {
            // _writtenNodes.TryAdd(addedNode.IdHash, addedNode.IdHash);
            // ch.Writer.TryWrite(addedNode);
        }

        async Task DiscoverAsync(ValueHash256 hash)
        {
            if (_logger.IsDebug) _logger.Debug($"Looking up {hash}");
            bool anyFound = false;
            int count = 0;

            await foreach (var node in _lookup2.Lookup(hash, token))
            {
                anyFound = true;
                count++;
                total++;
                if (!_writtenNodes.TryAdd(node.IdHash, node.IdHash))
                {
                    duplicated++;
                    continue;
                }
                _kademliaDiscoveredNodes.Inc();
                await ch.Writer.WriteAsync(node, token);
            }

            if (!anyFound)
            {
                if (_logger.IsDebug) _logger.Debug($"No node found for {hash}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Found {count} nodes");
            }
            Console.Error.WriteLine($"Total is {total}, duplicated {duplicated}, fromNewNode {fromNewNode}");
        }

        Task discoverTask = Task.WhenAll(Enumerable.Range(0, 6).Select((_) => Task.Run(async () =>
        {
            Random random = new();
            ValueHash256 randomNodeId = new();
            int iterationCount = 0;
            while (!token.IsCancellationRequested)
            {
                Stopwatch iterationTime = Stopwatch.StartNew();
                if (iterationCount % 10 == 0)
                {
                    // Probably shnould be done once or in a few interval
                    await EnsureBootNodes(token);
                }

                try
                {
                    random.NextBytes(randomNodeId.BytesAsSpan);
                    await DiscoverAsync(randomNodeId);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Discovery via custom random walk failed.", ex);
                }

                // Prevent high CPU when all node is not reachable due to network connectivity issue.
                if (iterationTime.Elapsed < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        })));

        try
        {
            _kademlia.OnNodeAdded += handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(token))
            {
                yield return node;
            }
        }
        finally
        {
            await discoverTask;
            _kademlia.OnNodeAdded -= handler;
        }
    }

    private async Task EnsureBootNodes(CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int onlineBootnodes = 0;
        await Parallel.ForEachAsync(_bootNodes, token, async (node, token) =>
        {
            try
            {
                await _discv4MessageSender.Ping(node, token);
                onlineBootnodes++;
                _kademlia.AddOrRefresh(node);
            }
            catch (OperationCanceledException)
            {
            }
        });

        _logger.Info($"Ensure bootnodes took {sw.Elapsed}. {onlineBootnodes} out of {_bootNodes.Count} online");
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}
