// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-8159 — eth/71: Block Access List Exchange
/// Adds GetBlockAccessLists (0x12) and BlockAccessLists (0x13) messages for BAL synchronization.
/// </summary>
public class Eth71ProtocolHandler : Eth70ProtocolHandler, ISyncPeer, IStaticProtocolInfo
{
    private readonly MessageDictionary<GetBlockAccessListsMessage, BlockAccessListsMessage> _balRequests;

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
            gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy) =>
        _balRequests = new MessageDictionary<GetBlockAccessListsMessage, BlockAccessListsMessage>(Send);

    public override string Name => "eth71";

    public new static string Code => Protocol.Eth;
    public new static byte Version => EthVersions.Eth71;
    public override byte ProtocolVersion => Version;

    // Message IDs 0x00–0x13 → 20 codes
    public override int MessageIdSpaceSize => 20;

    protected override void HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth71MessageCode.GetBlockAccessLists:
                HandleInBackground<GetBlockAccessListsMessage, BlockAccessListsMessage>(message, Handle);
                break;
            case Eth71MessageCode.BlockAccessLists:
                BlockAccessListsMessage balMsg = Deserialize<BlockAccessListsMessage>(message.Content);
                ReportIn(balMsg, size);
                Handle(balMsg, size);
                break;
            default:
                base.HandleMessageCore(message);
                break;
        }
    }

    private void Handle(BlockAccessListsMessage msg, long size) =>
        _balRequests.Handle(msg.RequestId, msg, size);

    private Task<BlockAccessListsMessage> Handle(GetBlockAccessListsMessage request, CancellationToken cancellationToken)
    {
        using GetBlockAccessListsMessage req = request;
        IOwnedReadOnlyList<Hash256> hashes = req.Hashes;
        long totalSize = 0;
        RlpByteArrayList results;

        using (DeferredRlpItemList.Builder builder = new(entryCapacity: hashes.Count))
        {
            using (DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer())
            {
                for (int i = 0; i < hashes.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    using MemoryManager<byte>? balRlp = SyncServer.GetBlockAccessListRlp(hashes[i]);
                    ReadOnlySpan<byte> balRlpSpan = balRlp is null ? ReadOnlySpan<byte>.Empty : balRlp.Memory.Span;
                    writer.WriteValue(balRlpSpan);
                    totalSize += Rlp.LengthOf(balRlpSpan);

                    if (totalSize > BalResponseSoftLimit)
                    {
                        break;
                    }
                }
            }

            results = new RlpByteArrayList(builder.ToRlpItemList());
        }

        return Task.FromResult(new BlockAccessListsMessage(req.RequestId, results));
    }

    public async Task<IByteArrayList> GetBlockAccessLists(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        if (blockHashes.Count == 0)
        {
            return EmptyByteArrayList.Instance;
        }

        ArrayPoolList<Hash256> hashList = new(blockHashes.Count);
        for (int i = 0; i < blockHashes.Count; i++)
        {
            hashList.Add(blockHashes[i]);
        }

        GetBlockAccessListsMessage request = new(hashList);
        Request<GetBlockAccessListsMessage, BlockAccessListsMessage> req = new(request);

        try
        {
            _balRequests.Send(req);
            BlockAccessListsMessage response = await HandleResponse(req, TransferSpeedType.BlockAccessLists, static _ => nameof(GetBlockAccessListsMessage), token);
            return response.BlockAccessLists;
        }
        finally
        {
            request.Dispose();
        }
    }

    Task<IByteArrayList> ISyncPeer.GetBlockAccessLists(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        => GetBlockAccessLists(blockHashes, token);
}
