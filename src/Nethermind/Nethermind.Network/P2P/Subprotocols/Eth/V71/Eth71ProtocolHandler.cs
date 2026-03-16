// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-8159 — eth/71: Block Access List Exchange
/// Adds GetBlockAccessLists (0x12) and BlockAccessLists (0x13) messages for BAL synchronization.
/// </summary>
public class Eth71ProtocolHandler : Eth70ProtocolHandler
{
    private readonly MessageDictionary<GetBlockAccessListsMessage66, BlockAccessListsMessage66> _balRequests;

    /// <summary>
    /// Recommended soft limit for BlockAccessLists responses (10 MiB per EIP-8159).
    /// </summary>
    private const int BalResponseSoftLimit = 10 * 1024 * 1024;

    public Eth71ProtocolHandler(
        ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IGossipPolicy gossipPolicy,
        IForkInfo forkInfo,
        ILogManager logManager,
        ITxPoolConfig txPoolConfig,
        ISpecProvider specProvider,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool,
            gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy)
    {
        _balRequests = new MessageDictionary<GetBlockAccessListsMessage66, BlockAccessListsMessage66>(Send);
    }

    public override string Name => "eth71";

    public new static byte Version => EthVersions.Eth71;
    public override byte ProtocolVersion => Version;

    // Message IDs 0x00–0x13 → 20 codes
    public override int MessageIdSpaceSize => 20;

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth71MessageCode.GetBlockAccessLists:
                HandleInBackground<GetBlockAccessListsMessage66, BlockAccessListsMessage66>(message, Handle);
                break;
            case Eth71MessageCode.BlockAccessLists:
                BlockAccessListsMessage66 balMsg = Deserialize<BlockAccessListsMessage66>(message.Content);
                ReportIn(balMsg, size);
                Handle(balMsg, size);
                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(BlockAccessListsMessage66 msg, long size) =>
        _balRequests.Handle(msg.RequestId, msg, size);

    private Task<BlockAccessListsMessage66> Handle(GetBlockAccessListsMessage66 request, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<Hash256> hashes = request.EthMessage.Hashes;
        ArrayPoolList<byte[]> results = new(hashes.Count);
        long totalSize = 0;

        for (int i = 0; i < hashes.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            byte[]? balRlp = SyncServer.GetBlockAccessListRlp(hashes[i]);

            if (balRlp is null)
            {
                results.Add(BlockAccessListsMessage.EmptyBal);
            }
            else
            {
                results.Add(balRlp);
                totalSize += balRlp.Length;
            }

            if (totalSize > BalResponseSoftLimit)
                break;
        }

        // Pad remaining entries with empty BALs if we stopped early
        while (results.Count < hashes.Count)
        {
            results.Add(BlockAccessListsMessage.EmptyBal);
        }

        BlockAccessListsMessage inner = new(results);
        return Task.FromResult(new BlockAccessListsMessage66(request.RequestId, inner));
    }

    public async Task<IOwnedReadOnlyList<byte[]>> GetBlockAccessLists(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        if (blockHashes.Count == 0)
        {
            return ArrayPoolList<byte[]>.Empty();
        }

        ArrayPoolList<Hash256> hashList = new(blockHashes.Count);
        for (int i = 0; i < blockHashes.Count; i++)
        {
            hashList.Add(blockHashes[i]);
        }

        GetBlockAccessListsMessage inner = new(hashList);
        GetBlockAccessListsMessage66 request = new(0, inner);

        Request<GetBlockAccessListsMessage66, BlockAccessListsMessage66> req = new(request);
        _balRequests.Send(req);

        BlockAccessListsMessage66 response = await HandleResponse(req, TransferSpeedType.Bodies, static _ => nameof(GetBlockAccessListsMessage66), token);
        return response.EthMessage.AccessLists;
    }
}
