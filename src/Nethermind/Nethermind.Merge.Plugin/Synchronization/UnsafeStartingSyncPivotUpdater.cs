// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class UnsafeStartingSyncPivotUpdater(
    IBlockTree blockTree,
    ISyncPeerPool syncPeerPool,
    ISyncConfig syncConfig,
    ISyncProgressResolver syncProgressResolver,
    IBlockCacheService blockCacheService,
    IBeaconSyncStrategy beaconSyncStrategy,
    ILogManager logManager,
    TimeProvider? timeProvider = null)
    : StartingSyncPivotUpdater(blockTree, syncPeerPool, syncConfig, syncProgressResolver,
        blockCacheService, beaconSyncStrategy, logManager, timeProvider)
{
    private readonly ISyncPeerPool _ancestryPeers = syncPeerPool;
    private readonly HashSet<PeerInfo> _deferredPeers = [];

    protected override bool ReverifiesStoredPivot => true;

    protected override async Task<(Hash256 Hash, ulong Number)?> TryGetPivotData(CancellationToken cancellationToken)
    {
        // These chains may not supply a finalized hash until sync completes, so anchor ancestry to the CL head.
        Hash256? headHash = _beaconSyncStrategy.GetHeadBlockHash();
        if (headHash is null || headHash == Keccak.Zero) return null;
        AncestryPosition start = FindCachedPrefix(headHash, cancellationToken);
        if (start.IsComplete)
        {
            return CompleteAncestry(start);
        }
        if (start.Hash is not { } missingHash || missingHash == Keccak.Zero) return null;
        int count = (int)Reorganization.MaxDepth + 1 - start.Depth;

        List<PeerInfo> peers = [];
        List<PeerInfo> deferred = [];
        foreach (PeerInfo peer in _ancestryPeers.InitializedPeers)
        {
            if (_deferredPeers.Contains(peer)) deferred.Add(peer);
            else peers.Add(peer);
        }
        peers.AddRange(deferred);
        _deferredPeers.IntersectWith(peers);
        if (_logger.IsInfo) _logger.Info($"Checking pivot ancestry from CL head {headHash} with {peers.Count} peers");
        foreach (PeerInfo peer in peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _deferredPeers.Add(peer);
            using CancellationTokenSource timeout = new(Timeouts.Eth, AncestryTimeProvider);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                if (_logger.IsDebug) _logger.Debug($"Asking peer {peer} for reverse ancestry of {missingHash}");
                using IOwnedReadOnlyList<BlockHeader>? headers = await RequestAncestry(peer.SyncPeer, missingHash, count, deadline.Token);
                if (headers?.Count > count) continue;
                AncestryPosition? pivot = await ResolveAncestry(start, peer.SyncPeer, headers, deadline.Token);
                if (pivot is { } complete)
                {
                    return CompleteAncestry(complete);
                }
            }
            catch (Exception exception) when (exception is TimeoutException or ConcurrencyLimitReachedException ||
                (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (_logger.IsDebug) _logger.Debug($"Peer {peer} did not complete pivot ancestry: {exception}");
            }
        }
        return null;
    }

    private (Hash256 Hash, ulong Number) CompleteAncestry(AncestryPosition position)
    {
        _deferredPeers.Clear();
        UpdateAndPrintPotentialNewPivot(position.Hash!);
        return (position.Hash!, position.Number!.Value);
    }

    private AncestryPosition FindCachedPrefix(Hash256 headHash, CancellationToken cancellationToken)
    {
        AncestryPosition position = new(headHash, null, 0);
        while (!position.IsComplete && position.Hash is { } hash && hash != Keccak.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_blockCacheService.BlockCache.TryGetValue(hash, out Block? block) || !TryAdvance(ref position, block.Header)) break;
        }
        return position;
    }

    private async Task<AncestryPosition?> ResolveAncestry(AncestryPosition position, ISyncPeer peer,
        IOwnedReadOnlyList<BlockHeader>? headers, CancellationToken cancellationToken)
    {
        int prefixDepth = position.Depth;
        while (!position.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position.Hash is not { } hash || hash == Keccak.Zero) return null;
            int index = position.Depth - prefixDepth;
            BlockHeader? header = index < (headers?.Count ?? 0) ? headers![index] : null;
            if (header is null)
            {
                if (index > 0 && _blockCacheService.BlockCache.TryGetValue(hash, out Block? block) && TryAdvance(ref position, block.Header)) continue;
                if (_logger.IsDebug) _logger.Debug($"Asking peer {peer.Node} for missing pivot ancestor {hash}");
                header = await peer.GetHeadBlockHeader(hash, cancellationToken).WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (header is null || !TryAdvance(ref position, header)) return null;
        }
        return position;
    }

    private static bool TryAdvance(ref AncestryPosition position, BlockHeader header)
    {
        if (header.Hash != position.Hash || (position.Number is { } number && header.Number != number)
            || (position.Depth == 0 && header.Number <= Reorganization.MaxDepth) || !HeaderValidator.ValidateHash(header)) return false;

        int depth = position.Depth + 1;
        position = depth > (int)Reorganization.MaxDepth
            ? new(header.Hash, header.Number, depth)
            : new(header.ParentHash, header.Number - 1, depth);
        return true;
    }

    private readonly record struct AncestryPosition(Hash256? Hash, ulong? Number, int Depth)
    {
        public bool IsComplete => Depth > (int)Reorganization.MaxDepth;
    }
}
