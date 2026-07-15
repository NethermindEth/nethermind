// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Snap.V1;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.SnapServer;
using Nethermind.Stats;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2;

public class Snap2ProtocolHandler(
    ISession session,
    INodeStatsManager nodeStats,
    IMessageSerializationService serializer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ILogManager logManager,
    ISyncConfig syncConfig,
    ISnapServer snapServer)
    : Snap1ProtocolHandler(session, nodeStats, serializer, backgroundTaskScheduler, logManager, syncConfig, snapServer), IStaticProtocolInfo
{
    public override string Name => "snap2";
    public new static byte Version => 2;
    public override byte ProtocolVersion => Version;

    public override int MessageIdSpaceSize => 8;

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case Snap2MessageCode.GetBlockAccessLists:
                if (ShouldServeSnap())
                    HandleInBackground<GetBlockAccessListsMessage, BlockAccessListsMessage>(message, Handle);
                return true;
            case Snap2MessageCode.BlockAccessLists:
                // Serve-only: we never issue GetBlockAccessLists, so an inbound response is unsolicited; ignore it.
                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }

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
}
