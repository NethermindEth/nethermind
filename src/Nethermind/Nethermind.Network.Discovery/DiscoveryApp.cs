// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;
using LogLevel = DotNetty.Handlers.Logging.LogLevel;

namespace Nethermind.Network.Discovery;

public class DiscoveryApp : IDiscoveryApp
{
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ITimestamper _timestamper;
    private readonly INodesLocator _nodesLocator;
    private readonly IDiscoveryManager _discoveryManager;
    private readonly INodeTable _nodeTable;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly IMessageSerializationService _messageSerializationService;
    private readonly ICryptoRandom _cryptoRandom;
    private readonly INetworkStorage _discoveryStorage;
    private readonly DiscoveryPersistenceManager _persistenceManager;
    private readonly IProcessExitSource _processExitSource;
    private readonly INetworkConfig _networkConfig;

    private NettyDiscoveryHandler? _discoveryHandler;
    private Task? _runningTask;

    public DiscoveryApp(
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        INodesLocator nodesLocator,
        IDiscoveryManager? discoveryManager,
        INodeTable? nodeTable,
        IMessageSerializationService? msgSerializationService,
        ICryptoRandom? cryptoRandom,
        [KeyFilter(DbNames.DiscoveryNodes)] INetworkStorage? discoveryStorage,
        DiscoveryPersistenceManager discoveryPersistenceManager,
        IProcessExitSource processExitSource,
        INetworkConfig? networkConfig,
        IDiscoveryConfig? discoveryConfig,
        ITimestamper? timestamper,
        ILogManager? logManager)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = _logManager.GetClassLogger();
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        _nodesLocator = nodesLocator ?? throw new ArgumentNullException(nameof(nodesLocator));
        _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
        _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _messageSerializationService =
            msgSerializationService ?? throw new ArgumentNullException(nameof(msgSerializationService));
        _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
        _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
        _persistenceManager = discoveryPersistenceManager;
        _processExitSource = processExitSource;
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _discoveryStorage.StartBatch();

        _discoveryManager.NodeDiscovered += OnNodeDiscovered;
        _nodeTable.Initialize(nodeKey.PublicKey);
        if (_nodeTable.MasterNode is null)
        {
            throw new NetworkingException(
                "Discovery node table initialization failed - master node is null",
                NetworkExceptionType.Discovery);
        }

        _nodesLocator.Initialize(_nodeTable.MasterNode);
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

            NetworkChange.NetworkAvailabilityChanged -= ResetUnreachableStatus;
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
        _discoveryManager.GetNodeLifecycleManager(node);
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
        if (!e.IsAvailable)
        {
            return;
        }

        foreach (INodeLifecycleManager unreachable in _discoveryManager.GetNodeLifecycleManagers().Where(static x => x.State == NodeLifecycleState.Unreachable))
        {
            unreachable.ResetUnreachableStatus();
        }
    }

    public void InitializeChannel(IChannel channel)
    {
        _discoveryHandler = new NettyDiscoveryHandler(_discoveryManager, channel, _messageSerializationService,
            _timestamper, _logManager);
        _discoveryManager.MsgSender = _discoveryHandler;
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
                if (_discoveryManager.GetOrAddNodeLifecycleManagers(static x => x.State == NodeLifecycleState.Active).Count != 0)
                {
                    break;
                }

                _logger.Warn("Could not communicate with any nodes (bootnodes, trusted nodes, persisted nodes).");
                await Task.Delay(1000, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Task persistenceTask = _persistenceManager.RunDiscoveryPersistenceCommit(cancellationToken);

            try
            {
                // Step 2 - run the standard kademlia routine
                await RunDiscoveryProcess();
            }
            finally
            {
                // Block until persistence is finished
                await persistenceTask;
            }
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error during discovery initialization", e);
        }
    }

    private async Task<bool> InitializeBootnodes(CancellationToken cancellationToken)
    {
        NetworkNode[] bootnodes = NetworkNode.ParseNodes(_discoveryConfig.Bootnodes, _logger);
        if (bootnodes.Length == 0)
        {
            if (_logger.IsWarn) _logger.Warn("No bootnodes specified in configuration");
            return true;
        }

        List<INodeLifecycleManager> managers = new();
        for (int i = 0; i < bootnodes.Length; i++)
        {
            NetworkNode bootnode = bootnodes[i];
            if (bootnode.NodeId is null)
            {
                _logger.Warn($"Bootnode ignored because of missing node ID: {bootnode}");
            }

            Node node = new(bootnode.NodeId, bootnode.Host, bootnode.Port);
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
            if (manager is not null)
            {
                managers.Add(manager);
            }
            else
            {
                _logger.Warn($"Bootnode config contains self: {bootnode.NodeId}");
            }
        }

        //Wait for pong message to come back from Boot nodes
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

        if (_logger.IsInfo)
            _logger.Info(
                $"Connected to {reachedNodeCounter} bootnodes, {_discoveryManager.GetOrAddNodeLifecycleManagers(static x => x.State == NodeLifecycleState.Active).Count} trusted/persisted nodes");
        return reachedNodeCounter > 0;
    }

    private async Task RunDiscoveryProcess()
    {
        byte[] randomId = new byte[64];
        CancellationToken cancellationToken = _processExitSource.Token;
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
    }

    private void OnNodeDiscovered(object? sender, NodeEventArgs e)
    {
        NodeAdded?.Invoke(this, e);
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // TODO: Rewrote this to properly support throttling.
        Channel<Node> ch = Channel.CreateBounded<Node>(64); // Some reasonably large value
        void handler(object? _, NodeEventArgs args)
        {
            if (!ch.Writer.TryWrite(args.Node))
            {
                // Keep in mind, the channel is already buffered, so forgetting this node is probably fine.
                _nodesLocator.ShouldThrottle = true;
            }
            else
            {
                _nodesLocator.ShouldThrottle = false;
            }
        }

        try
        {
            // TODO: Use lookup like kademlia
            NodeAdded += handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(cancellationToken))
            {
                yield return node;
            }
        }
        finally
        {
            NodeAdded -= handler;
            _nodesLocator.ShouldThrottle = false;
        }
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}
