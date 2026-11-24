// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V74.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74;

/// <summary>EIP-8512 <c>eth/74</c>: <c>eth/72</c> plus the exchange of the EIP-8288 dependency proofs that neither headers nor bodies carry.</summary>
/// <remarks>EIP-8512 extends EIP-8408's <c>eth/73</c>, whose indexed cell requests this client does not implement, so this
/// handler is built on <c>eth/72</c> and exchanges cells only in the shared-mask forms.</remarks>
public class Eth74ProtocolHandler(
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
    IChainHeadSpecProvider specProvider,
    IBlobCustodyTracker blobCustodyTracker,
    ISparseBlobPoolPeerRegistry sparseBlobPoolPeerRegistry,
    IBlockFinder blockFinder,
    IDependencyProofStore proofStore,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth72ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo,
        logManager, txPoolConfig, specProvider, blobCustodyTracker, sparseBlobPoolPeerRegistry, transactionsGossipPolicy),
    ISyncPeer, IStaticProtocolInfo
{
    private static readonly Func<DependencyProofsMessage, int, DependencyProofsMessage> SelectProofs = static (message, _) => message;

    private MessageDictionary<GetDependencyProofsMessage, DependencyProofsMessage>? _proofRequests;
    private readonly RateLimiter _serveLimiter = new(Eth74Protocol.MaxServedRequestsPerSecond);

    private MessageDictionary<GetDependencyProofsMessage, DependencyProofsMessage> ProofRequests =>
        LazyInitializer.EnsureInitialized(ref _proofRequests, () => new(this));

    public override string Name => "eth74";

    public new static byte Version => EthVersions.Eth74;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => Eth74Protocol.MessageIdSpaceSize;

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case Eth74MessageCode.GetDependencyProofs:
                HandleInBackground<GetDependencyProofsMessage, DependencyProofsMessage>(message, Handle);
                return true;
            case Eth74MessageCode.DependencyProofs:
                HandleCorrelatedResponse(ProofRequests, message.Content, message.Content.ReadableBytes, SelectProofs);
                return true;
            default:
                return base.HandleMessageCore(message);
        }
    }

    /// <remarks>EIP-8512 asks for rate limiting against amplification. A request arriving with too long a backlog is answered
    /// with no proofs, which its sender treats as unavailable and asks another peer for.</remarks>
    private async Task<DependencyProofsMessage> Handle(GetDependencyProofsMessage request, CancellationToken cancellationToken)
    {
        using GetDependencyProofsMessage req = request;
        if (_serveLimiter.Backlog > Eth74Protocol.MaxServeBacklog) return new DependencyProofsMessage(req.RequestId, new ArrayPoolList<byte[]>(0));
        await _serveLimiter.WaitAsync(cancellationToken);

        ReadOnlySpan<Hash256> hashes = req.Hashes.AsSpan();
        ArrayPoolList<byte[]> proofs = new(hashes.Length);
        long totalSize = 0;
        for (int i = 0; i < hashes.Length && totalSize < Eth74Protocol.SoftResponseLimit && !cancellationToken.IsCancellationRequested; i++)
        {
            byte[] proof = GetProof(hashes[i]);
            proofs.Add(proof);
            totalSize += Rlp.LengthOf(proof);
        }

        return new DependencyProofsMessage(req.RequestId, proofs);
    }

    /// <returns>The stored proof, or the empty entry for an unknown, pre-fork or pruned block.</returns>
    private byte[] GetProof(Hash256 blockHash) =>
        blockFinder.FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing) is
        { DependencyProofCommitment: not null } header
            ? proofStore.Get(header.Number, blockHash) ?? []
            : [];

    /// <inheritdoc cref="ISyncPeer.GetDependencyProofs"/>
    public async Task<IOwnedReadOnlyList<byte[]>> GetDependencyProofs(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        if (blockHashes.Count == 0) return IOwnedReadOnlyList<byte[]>.Empty;

        ArrayPoolList<Hash256> hashes = new(blockHashes.Count);
        for (int i = 0; i < blockHashes.Count; i++) hashes.Add(blockHashes[i]);

        using GetDependencyProofsMessage request = new(hashes);
        Request<GetDependencyProofsMessage, DependencyProofsMessage> req = new(request);
        ProofRequests.Send(req);
        using DependencyProofsMessage response =
            await HandleResponse(req, TransferSpeedType.DependencyProofs, static _ => nameof(GetDependencyProofsMessage), token);
        return response.DisownProofs();
    }

    Task<IOwnedReadOnlyList<byte[]>> ISyncPeer.GetDependencyProofs(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        => GetDependencyProofs(blockHashes, token);
}
