// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Snap.V1;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.SnapServer;
using Nethermind.Stats;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2;

public class Snap2ProtocolHandler : Snap1ProtocolHandler, IStaticProtocolInfo, ISnapSyncPeer
{
    public override string Name => "snap2";
    public new static byte Version => SnapVersions.Snap2;
    public override byte ProtocolVersion => Version;

    public override int MessageIdSpaceSize => 10;

    private readonly MessageDictionary<GetBlockAccessListsMessage, BlockAccessListsMessage> _getBlockAccessListsRequests;

    public Snap2ProtocolHandler(ISession session,
        INodeStatsManager nodeStats,
        IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager,
        ISyncConfig syncConfig,
        ISnapServer snapServer)
        : base(session, nodeStats, serializer, backgroundTaskScheduler, logManager, syncConfig, snapServer) => _getBlockAccessListsRequests = new(this);

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;

        switch (message.PacketType)
        {
            case Snap1MessageCode.GetTrieNodes:
            case Snap1MessageCode.TrieNodes:
                return false;
            case Snap2MessageCode.GetBlockAccessLists:
                if (ShouldServeSnap())
                    HandleInBackground<GetBlockAccessListsMessage, BlockAccessListsMessage>(message, Handle);
                return true;
            case Snap2MessageCode.BlockAccessLists:
                BlockAccessListsMessage blockAccessListsMessage = Deserialize<BlockAccessListsMessage>(message.Content);
                ReportIn(blockAccessListsMessage, size);
                Handle(blockAccessListsMessage, size);
                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }
    private void Handle(BlockAccessListsMessage msg, long size) => _getBlockAccessListsRequests.Handle(msg.RequestId, msg, size);

    private ValueTask<BlockAccessListsMessage> Handle(GetBlockAccessListsMessage getBlockAccessListsMessage, CancellationToken cancellationToken)
    {
        using GetBlockAccessListsMessage message = getBlockAccessListsMessage;
        BlockAccessListsMessage response = FulfillBlockAccessListsMessage(message, cancellationToken);
        response.RequestId = message.RequestId;
        return new ValueTask<BlockAccessListsMessage>(response);
    }

    private BlockAccessListsMessage FulfillBlockAccessListsMessage(GetBlockAccessListsMessage message, CancellationToken cancellationToken)
    {
        IByteArrayList blockAccessLists = SyncServer.GetBlockAccessLists(message.BlockHashes, message.Bytes, cancellationToken);
        return new BlockAccessListsMessage(blockAccessLists);
    }

    public async Task<IByteArrayList> GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, CancellationToken token)
    {
        BlockAccessListsMessage response = await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
            SendRequest(new GetBlockAccessListsMessage
            {
                BlockHashes = blockHashes.ToPooledList(),
                Bytes = bytesLimit,
            }, _getBlockAccessListsRequests, token));

        return response.BlockAccessLists;
    }
}
