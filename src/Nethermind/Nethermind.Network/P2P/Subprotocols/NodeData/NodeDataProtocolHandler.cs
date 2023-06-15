// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.NodeData.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Subprotocols.NodeData;

public class NodeDataProtocolHandler : ZeroProtocolHandlerBase, INodeDataPeer
{
    private readonly ISyncServer _syncServer;
    private readonly MessageQueue<GetNodeDataMessage, byte[][]> _nodeDataRequests;

    public override string Name => "nodedata1";
    protected override TimeSpan InitTimeout => Timeouts.Eth;
    public override byte ProtocolVersion => 1;
    public override string ProtocolCode => Protocol.NodeData;
    public override int MessageIdSpaceSize => 2;

    public NodeDataProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager statsManager,
        ISyncServer syncServer,
        ILogManager logManager)
        : base(session, statsManager, serializer, logManager)
    {
        _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
        _nodeDataRequests = new MessageQueue<GetNodeDataMessage, byte[][]>(Send);
    }
    public override void Init()
    {
        ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
    }

    public override void Dispose()
    {
    }
    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
    {
        Dispose();
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
                ReportIn(getNodeDataMessage, size);
                Handle(getNodeDataMessage);
                break;
            case NodeDataMessageCode.NodeData:
                NodeDataMessage nodeDataMessage = Deserialize<NodeDataMessage>(message.Content);
                Metrics.NodeDataReceived++;
                ReportIn(nodeDataMessage, size);
                Handle(nodeDataMessage, size);
                break;
        }
    }

    private void Handle(GetNodeDataMessage getNodeDataMessage)
    {
        Send(FulfillNodeDataRequest(getNodeDataMessage));
    }

    private NodeDataMessage FulfillNodeDataRequest(GetNodeDataMessage msg)
    {
        if (msg.Hashes.Count > 4096)
        {
            throw new EthSyncException("NODEDATA protocol: Incoming node data request for more than 4096 nodes");
        }

        byte[][] nodeData = _syncServer.GetNodeData(msg.Hashes);

        return new NodeDataMessage(nodeData);
    }

    private void Handle(NodeDataMessage msg, int size)
    {
        _nodeDataRequests.Handle(msg.Data, size);
    }

    public async Task<byte[][]> GetNodeData(IReadOnlyList<Keccak> keys, CancellationToken token)
    {
        if (keys.Count == 0)
        {
            return Array.Empty<byte[]>();
        }

        GetNodeDataMessage msg = new(keys);
        byte[][] nodeData = await SendRequest(msg, token);
        return nodeData;
    }

    private async Task<byte[][]> SendRequest(GetNodeDataMessage message, CancellationToken token)
    {
        if (Logger.IsTrace) Logger.Trace($"NODEDATA protocol: Sending node data request with keys count: {message.Hashes.Count}");

        Request<GetNodeDataMessage, byte[][]>? request = new(message);
        _nodeDataRequests.Send(request);

        return await HandleResponse(request, TransferSpeedType.NodeData, static (_) => $"{nameof(GetNodeDataMessage)}", token);
    }
}
