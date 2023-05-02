// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle;

public class NodeLifecycleManager : INodeLifecycleManager
{
    private readonly IDiscoveryManager _discoveryManager;
    private readonly INodeTable _nodeTable;
    private readonly ILogger _logger;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ITimestamper _timestamper;
    private readonly IEvictionManager _evictionManager;
    private readonly NodeRecord _nodeRecord;

    /// <summary>
    /// This is the value set by other clients based on real network tests.
    /// </summary>
    private const int ExpirationTimeInSeconds = 20;

    private PingMsg? _lastSentPing;
    private bool _isNeighborsExpected;

    // private bool _receivedPing;
    private bool _sentPing;
    // private bool _sentPong;
    private bool _receivedPong;

    public NodeLifecycleManager(Node node,
        IDiscoveryManager discoveryManager,
        INodeTable nodeTable,
        IEvictionManager evictionManager,
        INodeStats nodeStats,
        NodeRecord nodeRecord,
        IDiscoveryConfig discoveryConfig,
        ITimestamper timestamper,
        ILogger logger)
    {
        _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
        _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        _evictionManager = evictionManager ?? throw new ArgumentNullException(nameof(evictionManager));
        _nodeRecord = nodeRecord ?? throw new ArgumentNullException(nameof(nodeRecord));
        NodeStats = nodeStats ?? throw new ArgumentNullException(nameof(nodeStats));
        ManagedNode = node;
        UpdateState(NodeLifecycleState.New);
    }

    public Node ManagedNode { get; }
    public NodeLifecycleState State { get; private set; }
    public INodeStats NodeStats { get; }
    public bool IsBonded => _sentPing && _receivedPong;

    public event EventHandler<NodeLifecycleState>? OnStateChanged;

    public void ProcessPingMsg(PingMsg pingMsg)
    {
        // _receivedPing = true;
        SendPong(pingMsg);
        if (pingMsg.EnrSequence is not null && pingMsg.EnrSequence > _lastEnrSequence)
        {
            SendEnrRequest();
        }

        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingIn);
        RefreshNodeContactTime();
    }

    private void SendEnrRequest()
    {
        EnrRequestMsg msg = new(ManagedNode.Address, CalculateExpirationTime());
        _discoveryManager.SendMessage(msg);
        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrRequestOut);
    }

    public void ProcessEnrResponseMsg(EnrResponseMsg enrResponseMsg)
    {
        if (!IsBonded)
        {
            return;
        }
        _lastEnrSequence = enrResponseMsg.NodeRecord.EnrSequence;

        // TODO: 6) use the fork ID knowledge to mark each node with info on the forkhash

        // Enr.ForkId? forkId = enrResponseMsg.NodeRecord.GetValue<Enr.ForkId>(EnrContentKey.Eth);
        // if (forkId is not null)
        // {
        //     _logger.Warn($"Discovered new node with forkId {forkId.Value.ForkHash.ToHexString()}");
        // }

        OnStateChanged?.Invoke(this, NodeLifecycleState.ActiveWithEnr);
        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrResponseIn);
    }

    public void ProcessEnrRequestMsg(EnrRequestMsg enrRequestMessage)
    {
        if (IsBonded)
        {
            Rlp requestRlp = Rlp.Encode(Rlp.Encode(enrRequestMessage.ExpirationTime));
            EnrResponseMsg msg = new(ManagedNode.Address, _nodeRecord, Keccak.Compute(requestRlp.Bytes));
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrRequestIn);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrResponseOut);
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug("Attempt to request ENR before bonding");
        }
    }

    public void ProcessPongMsg(PongMsg pongMsg)
    {
        PingMsg? sentPingMsg = Interlocked.Exchange(ref _lastSentPing, null);
        if (sentPingMsg is null)
        {
            return;
        }

        if (Bytes.AreEqual(sentPingMsg.Mdc, pongMsg.PingMdc))
        {
            _receivedPong = true;
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongIn);
            if (IsBonded)
            {
                UpdateState(NodeLifecycleState.Active);
                if (_logger.IsTrace) _logger.Trace($"Bonded with {ManagedNode.Host}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Bonding with {ManagedNode} failed.");
            }

            RefreshNodeContactTime();
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace($"Unmatched MDC when bonding with {ManagedNode}");
            // ignore spoofed message
            _receivedPong = false;
        }
    }

    public void ProcessNeighborsMsg(NeighborsMsg? msg)
    {
        if (msg is null)
        {
            return;
        }

        if (!IsBonded)
        {
            return;
        }

        if (_isNeighborsExpected)
        {
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursIn);
            RefreshNodeContactTime();

            foreach (Node node in msg.Nodes)
            {
                if (node.Address.Address.ToString().Contains("127.0.0.1"))
                {
                    if (_logger.IsTrace) _logger.Trace($"Received localhost as node address from: {msg.FarPublicKey}, node: {node}");
                    continue;
                }

                //If node is new it will create a new nodeLifecycleManager and will update state to New, which will trigger Ping
                _discoveryManager.GetNodeLifecycleManager(node);
            }
        }

        _isNeighborsExpected = false;
    }

    public void ProcessFindNodeMsg(FindNodeMsg msg)
    {
        if (!IsBonded)
        {
            return;
        }

        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeIn);
        RefreshNodeContactTime();

        Node[] nodes = _nodeTable.GetClosestNodes(msg.SearchedNodeId).ToArray();
        SendNeighbors(nodes);
    }

    private readonly DateTime _lastTimeSendFindNode = DateTime.MinValue;

    private long _lastEnrSequence;

    public void SendFindNode(byte[] searchedNodeId)
    {
        if (!IsBonded)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending FIND NODE on {ManagedNode} before bonding");
        }

        if (DateTime.UtcNow - _lastTimeSendFindNode < TimeSpan.FromSeconds(60))
        {
            return;
        }

        FindNodeMsg msg = new(ManagedNode.Address, CalculateExpirationTime(), searchedNodeId);
        _isNeighborsExpected = true;
        _discoveryManager.SendMessage(msg);
        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeOut);
    }

    private DateTime _lastPingSent = DateTime.MinValue;

    public async Task SendPingAsync()
    {
        _lastPingSent = DateTime.UtcNow;
        _sentPing = true;
        await CreateAndSendPingAsync(_discoveryConfig.PingRetryCount);
    }

    private long CalculateExpirationTime()
    {
        return ExpirationTimeInSeconds + _timestamper.UnixTime.SecondsLong;
    }

    public void SendPong(PingMsg discoveryMsg)
    {
        PongMsg msg = new(ManagedNode.Address, CalculateExpirationTime(), discoveryMsg.Mdc!);
        _discoveryManager.SendMessage(msg);
        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongOut);
        // _sentPong = true;
        if (IsBonded)
        {
            UpdateState(NodeLifecycleState.Active);
        }
    }

    public void SendNeighbors(Node[] nodes)
    {
        if (!IsBonded)
        {
            if (_logger.IsWarn) _logger.Warn("Attempt to send NEIGHBOURS before bonding");
            return;
        }

        NeighborsMsg msg = new(ManagedNode.Address, CalculateExpirationTime(), nodes);
        _discoveryManager.SendMessage(msg);
        NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursOut);
    }

    public void StartEvictionProcess()
    {
        UpdateState(NodeLifecycleState.EvictCandidate);
    }

    public void LostEvictionProcess()
    {
        if (State == NodeLifecycleState.Active)
        {
            UpdateState(NodeLifecycleState.ActiveExcluded);
        }
    }

    private void UpdateState(NodeLifecycleState newState)
    {
        if (newState == NodeLifecycleState.New)
        {
            //if node is just discovered we send ping to confirm it is active
#pragma warning disable 4014
            SendPingAsync();
#pragma warning restore 4014
        }
        else if (newState == NodeLifecycleState.Active)
        {
            //TODO && !ManagedNode.IsDiscoveryNode - should we exclude discovery nodes
            //received pong first time
            if (State == NodeLifecycleState.New)
            {
                NodeAddResult result = _nodeTable.AddNode(ManagedNode);
                if (result.ResultType == NodeAddResultType.Full && result.EvictionCandidate?.Node is not null)
                {
                    INodeLifecycleManager? evictionCandidate = _discoveryManager.GetNodeLifecycleManager(result.EvictionCandidate.Node);
                    if (evictionCandidate is not null)
                    {
                        _evictionManager.StartEvictionProcess(evictionCandidate, this);
                    }
                }
            }
        }
        else if (newState == NodeLifecycleState.EvictCandidate)
        {
            if (State == NodeLifecycleState.EvictCandidate)
            {
                throw new InvalidOperationException("Cannot start more than one eviction process on same node.");
            }

            if (DateTime.UtcNow - _lastPingSent > TimeSpan.FromSeconds(5))
            {
#pragma warning disable 4014
                SendPingAsync();
#pragma warning restore 4014
            }
            else
            {
                // TODO: this is very strange...?
                // seems like we quickly send two state updates here since we do not return after invocation?
                OnStateChanged?.Invoke(this, NodeLifecycleState.Active);
            }
        }

        State = newState;
        OnStateChanged?.Invoke(this, State);
    }

    private void RefreshNodeContactTime()
    {
        if (State == NodeLifecycleState.Active)
        {
            _nodeTable.RefreshNode(ManagedNode);
        }
    }

    private async Task CreateAndSendPingAsync(int counter = 1)
    {
        if (_nodeTable.MasterNode is null)
        {
            return;
        }

        PingMsg msg = new(ManagedNode.Address, CalculateExpirationTime(), _nodeTable.MasterNode.Address);
        msg.EnrSequence = _nodeRecord.EnrSequence;

        try
        {
            _lastSentPing = msg;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingOut);

            bool result = await _discoveryManager.WasMessageReceived(ManagedNode.IdHash, MsgType.Pong, _discoveryConfig.PongTimeout);
            if (!result)
            {
                if (counter > 1)
                {
                    await CreateAndSendPingAsync(counter - 1);
                }

                UpdateState(NodeLifecycleState.Unreachable);
            }
        }
        catch (Exception e)
        {
            _logger.Error("Error during sending ping message", e);
        }
    }
}
