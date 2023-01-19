// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Subprotocols.NodeData;

public class NodeDataProtocolHandler : SyncPeerProtocolHandlerBase
{
    private readonly MessageDictionary<GetNodeDataMessage, Eth.V63.Messages.GetNodeDataMessage, byte[][]> _nodeDataRequests;

    public override string Name => "NodeData";
    protected override TimeSpan InitTimeout => Timeouts.Eth;
    public override byte ProtocolVersion => 1;
    public override string ProtocolCode => Protocol.NodeData;
    public override int MessageIdSpaceSize => 2;

    public NodeDataProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager statsManager,
        ISyncServer syncServer,
        ILogManager logManager)
        : base(session, serializer, statsManager, syncServer, logManager)
    {
        _nodeDataRequests = new MessageDictionary<GetNodeDataMessage, Eth.V63.Messages.GetNodeDataMessage, byte[][]>(Send);
    }
    public override void Init()
    {
        ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
    }

    public override void NotifyOfNewBlock(Block block, SendBlockMode mode)
    {
        throw new NotImplementedException();
    }

    protected override void OnDisposed()
    {
        throw new NotImplementedException();
    }

    public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
    public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
    {
        add { }
        remove { }
    }

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;

        switch (message.PacketType)
        {
            case NodeDataMessageCode.GetNodeData:
                GetNodeDataMessage getNodeDataMessage = Deserialize<GetNodeDataMessage>(message.Content);
                Metrics.GetNodeDataReceived++;
                ReportIn(getNodeDataMessage);
                Handle(getNodeDataMessage);
                break;
            case NodeDataMessageCode.NodeData:
                NodeDataMessage nodeDataMessage = Deserialize<NodeDataMessage>(message.Content);
                Metrics.Eth66NodeDataReceived++;
                ReportIn(nodeDataMessage);
                Handle(nodeDataMessage, size);
                break;
        }
    }

    private void Handle(GetNodeDataMessage getNodeDataMessage)
    {
        Send(new NodeDataMessage(getNodeDataMessage.RequestId,
            FulfillNodeDataRequest(getNodeDataMessage.EthMessage)));
    }

    private Eth.V63.Messages.NodeDataMessage FulfillNodeDataRequest(Eth.V63.Messages.GetNodeDataMessage msg)
    {
        if (msg.Hashes.Count > 4096)
        {
            throw new EthSyncException("Incoming node data request for more than 4096 nodes");
        }

        byte[][] nodeData = SyncServer.GetNodeData(msg.Hashes);

        return new Eth.V63.Messages.NodeDataMessage(nodeData);
    }

    private void Handle(NodeDataMessage msg, int size)
    {
        _nodeDataRequests.Handle(msg.RequestId, msg.EthMessage.Data, size);
    }
}
