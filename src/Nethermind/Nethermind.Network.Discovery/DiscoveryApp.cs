// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
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
    private readonly INetworkConfig _networkConfig;

    private Task? _discoveryTimerTask;

    private IChannel? _channel;
    private MultithreadEventLoopGroup? _group;
    private NettyDiscoveryHandler? _discoveryHandler;
    private Task? _storageCommitTask;

    public DiscoveryApp(INodesLocator nodesLocator,
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
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _discoveryStorage.StartBatch();
    }

    public void Initialize(PublicKey masterPublicKey)
    {
        _discoveryManager.NodeDiscovered += OnNodeDiscovered;
        _nodeTable.Initialize(masterPublicKey);
        if (_nodeTable.MasterNode is null)
        {
            throw new NetworkingException(
                "Discovery node table initialization failed - master node is null",
                NetworkExceptionType.Discovery);
        }

        _nodesLocator.Initialize(_nodeTable.MasterNode);
    }

    public void Start()
    {
        try
        {
            InitializeUdpChannel();
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

        await StopUdpChannelAsync();
        if (_logger.IsInfo) _logger.Info("Discovery shutdown complete.. please wait for all components to close");
    }

    public void AddNodeToDiscovery(Node node)
    {
        _discoveryManager.GetNodeLifecycleManager(node);
    }

    private void InitializeUdpChannel()
    {
        if (_logger.IsDebug)
            _logger.Debug($"Discovery    : udp://{_networkConfig.ExternalIp}:{_networkConfig.DiscoveryPort}");
        ThisNodeInfo.AddInfo("Discovery    :", $"udp://{_networkConfig.ExternalIp}:{_networkConfig.DiscoveryPort}");

        _group = new MultithreadEventLoopGroup(1);
        Bootstrap bootstrap = new();
        bootstrap.Group(_group);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            bootstrap.ChannelFactory(() => new SocketDatagramChannel(AddressFamily.InterNetwork))
                .Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));
        }
        else
        {
            bootstrap.Channel<SocketDatagramChannel>()
                .Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));
        }

        IPAddress ip = IPAddress.Parse(_networkConfig.LocalIp!);
        _bindingTask = bootstrap.BindAsync(ip, _networkConfig.DiscoveryPort)
            .ContinueWith(
                t
                    =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error($"Error when establishing discovery connection on Address: {ip}({_networkConfig.LocalIp}:{_networkConfig.DiscoveryPort})", t.Exception);
                    }

                    return _channel = t.Result;
                });
    }

    private Task? _bindingTask;

    private void InitializeChannel(IDatagramChannel channel)
    {
        _discoveryHandler = new NettyDiscoveryHandler(_discoveryManager, channel, _messageSerializationService,
            _timestamper, _logManager);
        _discoveryManager.MsgSender = _discoveryHandler;
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
                if (_discoveryManager.GetOrAddNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Count != 0)
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
            if (_logger.IsTrace)
                _logger.Trace($"Adding persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
        }

        if (_logger.IsDebug) _logger.Debug($"Added persisted discovery nodes: {nodes.Length}");
    }

    private void InitializeDiscoveryTimer()
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery timer");
        _discoveryTimerTask = RunDiscoveryProcess();
    }

    private void InitializeDiscoveryPersistenceTimer()
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
        _storageCommitTask = RunDiscoveryPersistenceCommit();
    }

    private async Task StopUdpChannelAsync()
    {
        try
        {
            if (_discoveryHandler is not null)
            {
                _discoveryHandler.OnChannelActivated -= OnChannelActivated;
            }

            if (_bindingTask is not null)
            {
                await _bindingTask; // if we are still starting
            }

            _logger.Info("Stopping discovery udp channel");
            if (_channel is null)
            {
                return;
            }

            Task closeTask = _channel.CloseAsync();
            CancellationTokenSource delayCancellation = new();
            if (await Task.WhenAny(closeTask,
                    Task.Delay(_discoveryConfig.UdpChannelCloseTimeout, delayCancellation.Token)) != closeTask)
            {
                _logger.Error(
                    $"Could not close udp connection in {_discoveryConfig.UdpChannelCloseTimeout} miliseconds");
            }
            else
            {
                delayCancellation.Cancel();
            }
        }
        catch (Exception e)
        {
            _logger.Error("Error during udp channel stop process", e);
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

            if (managers.Any(x => x.State == NodeLifecycleState.Active))
            {
                break;
            }

            if (_discoveryManager.GetOrAddNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Count != 0)
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
                $"Connected to {reachedNodeCounter} bootnodes, {_discoveryManager.GetOrAddNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Count} trusted/persisted nodes");
        return reachedNodeCounter > 0;
    }

    private async Task RunDiscoveryProcess()
    {
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

            int nodesCountAfterDiscovery = _nodeTable.Buckets.Sum(x => x.BondedItemsCount);
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
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during discovery commit: {ex}");
            }
        }
    }

    private void OnNodeDiscovered(object? sender, NodeEventArgs e)
    {
        NodeAdded?.Invoke(this, e);
    }

    public List<Node> LoadInitialList()
    {
        return new List<Node>();
    }

    public event EventHandler<NodeEventArgs>? NodeAdded;

    public event EventHandler<NodeEventArgs>? NodeRemoved { add { } remove { } }
}
