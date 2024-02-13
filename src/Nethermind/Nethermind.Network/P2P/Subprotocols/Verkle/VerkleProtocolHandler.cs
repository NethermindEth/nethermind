// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Verkle.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.VerkleSync;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Network.P2P.Subprotocols.Verkle;

public class VerkleProtocolHandler : ZeroProtocolHandlerBase, IVerkleSyncPeer
{

    public static TimeSpan LowerLatencyThreshold = TimeSpan.FromMilliseconds(2000);
    public static TimeSpan UpperLatencyThreshold = TimeSpan.FromMilliseconds(3000);

    private readonly LatencyBasedRequestSizer _requestSizer = new(
        minRequestLimit: 50000,
        maxRequestLimit: 3_000_000,
        lowerWatermark: LowerLatencyThreshold,
        upperWatermark: UpperLatencyThreshold
    );

    public override string Name => "verkle1";
    protected override TimeSpan InitTimeout => Timeouts.Eth;

    public override byte ProtocolVersion => 1;
    public override string ProtocolCode => Protocol.Verkle;
    public override int MessageIdSpaceSize => 8;

    private const string DisconnectMessage = "Serving verkle sync data in not implemented in this node.";

    private readonly MessageQueue<GetSubTreeRangeMessage, SubTreeRangeMessage> _getSubTreeRangeRequests;
    private readonly MessageQueue<GetLeafNodesMessage, LeafNodesMessage> _getLeafNodesRequests;
    private static readonly byte[] _emptyBytes = { 0 };

    private VerkleSyncServer? _syncServer;

    public VerkleProtocolHandler(ISession session,
        VerkleSyncServer? server,
        INodeStatsManager nodeStats,
        IMessageSerializationService serializer,
        ILogManager logManager) : base(session, nodeStats, serializer, logManager)
    {
        _syncServer = server;
        _getLeafNodesRequests = new(Send);
        _getSubTreeRangeRequests = new(Send);
    }

    public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
    public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
    {
        add { }
        remove { }
    }

    public override void Init()
    {
        ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
    }

    public override void Dispose()
    {
    }

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;

        switch (message.PacketType)
        {
            case VerkleMessageCode.GetSubTreeRange:
                GetSubTreeRangeMessage getSubTreeRangeMessage = Deserialize<GetSubTreeRangeMessage>(message.Content);
                ReportIn(getSubTreeRangeMessage, size);
                Handle(getSubTreeRangeMessage);
                break;
            case VerkleMessageCode.SubTreeRange:
                // Logger.Info($"HandleMessage(ZeroPacket message)|VerkleMessageCode.SubTreeRange");
                SubTreeRangeMessage subTreeRangeMessage = Deserialize<SubTreeRangeMessage>(message.Content);
                // IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
                // SubTreeRangeMessageSerializer ser = new();
                // ser.Serialize(buffer, subTreeRangeMessage);
                // Logger.Info($"{buffer.ReadAllBytesAsArray().ToHexString()}");
                ReportIn(subTreeRangeMessage, size);
                Handle(subTreeRangeMessage, size);
                break;
            case VerkleMessageCode.GetLeafNodes:
                GetLeafNodesMessage getLeafNodesMessage = Deserialize<GetLeafNodesMessage>(message.Content);
                ReportIn(getLeafNodesMessage, size);
                Handle(getLeafNodesMessage);
                break;
            case VerkleMessageCode.LeafNodes:
                LeafNodesMessage leafNodesMessage = Deserialize<LeafNodesMessage>(message.Content);
                ReportIn(leafNodesMessage, size);
                Handle(leafNodesMessage, size);
                break;
        }
    }

    private void Handle(SubTreeRangeMessage msg, long size)
    {
        Metrics.VerkleSubTreeRangeReceived++;
        _getSubTreeRangeRequests.Handle(msg, size);
    }

    private void Handle(LeafNodesMessage msg, long size)
    {
        Metrics.VerkleLeafNodesReceived++;
        _getLeafNodesRequests.Handle(msg, size);
    }

    private void Handle(GetSubTreeRangeMessage msg)
    {
        Metrics.VerkleGetSubTreeRangeReceived++;
        SubTreeRangeMessage? response = FulfillSubTreeRangeMessage(msg);
        response.RequestId = msg.RequestId;
        Send(response);
    }

    private void Handle(GetLeafNodesMessage msg)
    {
        Metrics.VerkleGetLeafNodesReceived++;
        LeafNodesMessage? response = FulfillLeafNodesMessage(msg);
        response.RequestId = msg.RequestId;
        Send(response);
    }

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
    {
        Dispose();
    }

    public async Task<SubTreesAndProofs> GetSubTreeRange(SubTreeRange range, CancellationToken token)
    {
        SubTreeRangeMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
            SendRequest(new GetSubTreeRangeMessage()
            {
                SubTreeRange = range,
                ResponseBytes = bytesLimit
            }, _getSubTreeRangeRequests, token));

        Metrics.VerkleGetSubTreeRangeSent++;

        return new SubTreesAndProofs(response.PathsWithSubTrees, response.Proofs);
    }

    public async Task<byte[][]> GetLeafNodes(GetLeafNodesRequest request, CancellationToken token)
    {
        LeafNodesMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
            SendRequest(new GetLeafNodesMessage()
            {
                RootHash = request.RootHash,
                Paths = request.LeafNodePaths,
                Bytes = bytesLimit
            }, _getLeafNodesRequests, token));

        Metrics.VerkleGetLeafNodesSent++;

        return response.Nodes;
    }

    public async Task<byte[][]> GetLeafNodes(LeafToRefreshRequest request, CancellationToken token)
    {
        LeafNodesMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
            SendRequest(new GetLeafNodesMessage()
            {
                RootHash = request.RootHash,
                Paths = request.Paths,
                Bytes = bytesLimit
            }, _getLeafNodesRequests, token));

        Metrics.VerkleGetLeafNodesSent++;

        return response.Nodes;
    }

    protected LeafNodesMessage FulfillLeafNodesMessage(GetLeafNodesMessage getTrieNodesMessage)
    {
        if (_syncServer is null)
        {
            Session.InitiateDisconnect(DisconnectReason.VerkleSyncServerNotImplemented, DisconnectMessage);
            if (Logger.IsDebug)
                Logger.Debug(
                    $"Peer disconnected because of requesting VerkleSync data (LeafNodes). Peer: {Session.Node.ClientId}");
            return new();

        }

        // var trieNodes = SyncServer.GetTrieNodes(getTrieNodesMessage.Paths, getTrieNodesMessage.RootHash);
        Metrics.VerkleLeafNodesSent++;
        return new LeafNodesMessage();
    }

    protected SubTreeRangeMessage FulfillSubTreeRangeMessage(GetSubTreeRangeMessage getAccountRangeMessage)
    {
        if (_syncServer is null)
        {
            Session.InitiateDisconnect(DisconnectReason.VerkleSyncServerNotImplemented, DisconnectMessage);
            if (Logger.IsDebug)
                Logger.Debug(
                    $"Peer disconnected because of requesting VerkleSync data (SubTreeRange). Peer: {Session.Node.ClientId}");
            ;
            return new();
        }

        SubTreeRange? accountRange = getAccountRangeMessage.SubTreeRange;
        (List<PathWithSubTree>, VerkleProof) data = _syncServer.GetSubTreeRanges(accountRange.RootHash, accountRange.StartingStem,
            accountRange.LimitStem, getAccountRangeMessage.ResponseBytes, out _);
        SubTreeRangeMessage? response = new();
        response.PathsWithSubTrees = data.Item1.ToArray();
        response.Proofs = data.Item2.EncodeRlp();

        TestSubTreeRangeMessageEncoding(accountRange.RootHash, accountRange.StartingStem, response);

        Metrics.VerkleSubTreeRangeSent++;
        return response;
    }

    private void TestSubTreeRangeMessageEncoding(Hash256 rootHash, Stem startingStem, SubTreeRangeMessage response)
    {
        SubTreeRangeMessageSerializer subTreeRangeMessageSerializer = new();
        VerkleProofSerializer verkleProofSerializer = new();

        Banderwagon rootPoint = Banderwagon.FromBytes(rootHash.Bytes.ToArray()) ?? throw new Exception("the root point is invalid");

        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
        subTreeRangeMessageSerializer.Serialize(buffer, response);
        SubTreeRangeMessage? decode = subTreeRangeMessageSerializer.Deserialize(buffer);

        var stateStore = new VerkleTreeStore<PersistEveryBlock>(new MemDb(), new MemDb(), new MemDb(), LimboLogs.Instance);
        var localTree = new VerkleTree(stateStore, LimboLogs.Instance);
        var isCorrect = localTree.CreateStatelessTreeFromRange(
            verkleProofSerializer.Decode(new RlpStream(decode.Proofs)), rootPoint, startingStem,
            decode.PathsWithSubTrees[^1].Path, decode.PathsWithSubTrees);
        Logger.Info(!isCorrect
            ? $"FulfillSubTreeRangeMessage: SubTreeRangeMessage encoding-decoding and verification: FAILED"
            : $"FulfillSubTreeRangeMessage: SubTreeRangeMessage encoding-decoding and verification: SUCCESS");
    }

    private async Task<TOut> SendRequest<TIn, TOut>(TIn msg, MessageQueue<TIn, TOut> requestQueue, CancellationToken token)
        where TIn : VerkleMessageBase
        where TOut : VerkleMessageBase
    {
        return await SendRequestGeneric(
            requestQueue,
            msg,
            TransferSpeedType.VerkleSyncRanges,
            static (request) => request.ToString(),
            token);
    }
}
