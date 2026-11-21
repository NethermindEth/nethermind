// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Eez.Network;

internal sealed class SequencerPeerBlocks(ISyncPeerPool peers, IBlockValidator validator, ILogManager logManager)
{
    private const int MaxPeersPerRead = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<SequencerPeerBlocks>();
    private readonly BySpeedStrategy _bodies = new(TransferSpeedType.Bodies, true);

    internal async Task<BlockHeader?> Header(Hash256 hash, CancellationToken token)
    {
        HashSet<PublicKey> attempted = [];
        for (int attempt = 0; attempt < MaxPeersPerRead; attempt++)
        {
            using SyncPeerAllocation allocation = await peers.Allocate(new UntriedPeers(BySpeedStrategy.FastestHeader, attempted),
                AllocationContexts.Headers, 1000, token);
            if (allocation.Current is not { } peer) return null;
            attempted.Add(peer.SyncPeer.Id);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                BlockHeader? header = await peer.SyncPeer.GetHeadBlockHeader(hash, deadline.Token);
                if (header is null) continue;
                if (header.Hash == hash && HeaderValidator.ValidateHash(header)) return header;
                peers.ReportBreachOfProtocol(peer, DisconnectReason.UnexpectedHeaderHash, "EEZ ancestry header does not match its requested hash.");
            }
            catch (Exception exception) when (IsUnavailable(exception, token))
            {
                if (_logger.IsDebug) _logger.Debug($"EEZ header {hash} unavailable from {peer}: {exception.Message}");
            }
        }
        return null;
    }

    internal async Task<Block?> Block(BlockHeader header, CancellationToken token)
    {
        HashSet<PublicKey> attempted = [];
        Hash256[] hashes = [header.Hash!];
        for (int attempt = 0; attempt < MaxPeersPerRead; attempt++)
        {
            using SyncPeerAllocation allocation = await peers.Allocate(new UntriedPeers(_bodies, attempted),
                AllocationContexts.Bodies, 1000, token);
            if (allocation.Current is not { } peer) return null;
            attempted.Add(peer.SyncPeer.Id);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using OwnedBlockBodies bodies = await peer.SyncPeer.GetBlockBodies(hashes, deadline.Token);
                if (bodies.Count == 0) continue;
                if (bodies.Count != 1 || bodies[0] is not { } body || !validator.ValidateBodyAgainstHeader(header, body, out _))
                {
                    peers.ReportBreachOfProtocol(peer, DisconnectReason.InvalidTxOrUncle, "EEZ block body does not match its header.");
                    continue;
                }
                bodies.Disown();
                return new Block(header, body);
            }
            catch (Exception exception) when (IsUnavailable(exception, token))
            {
                if (_logger.IsDebug) _logger.Debug($"EEZ body {header.Hash} unavailable from {peer}: {exception.Message}");
            }
        }
        return null;
    }

    private static bool IsUnavailable(Exception exception, CancellationToken token) =>
        exception is TimeoutException or ConcurrencyLimitReachedException || exception is OperationCanceledException && !token.IsCancellationRequested;

    private sealed class UntriedPeers(IPeerAllocationStrategy strategy, HashSet<PublicKey> attempted)
        : FilterPeerAllocationStrategy(strategy)
    {
        protected override bool Filter(PeerInfo peerInfo) => !attempted.Contains(peerInfo.SyncPeer.Id);
    }
}
