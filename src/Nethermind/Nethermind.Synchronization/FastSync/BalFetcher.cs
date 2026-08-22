// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.SyncLimits;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.FastSync;

public class BalFetcher(
    ISyncPeerPool peerPool,
    IBlockTree blockTree,
    IBlockAccessListStore balStore,
    ISyncConfig syncConfig,
    ILogManager logManager)
{
    private const int WindowSize = 1024;
    private const int MaxNoProgressRounds = 50;
    private static readonly IPeerAllocationStrategy PeerStrategy =
        new BalCapablePeerAllocationStrategy(new BySpeedStrategy(TransferSpeedType.BlockAccessLists, true));

    private readonly int _allocateTimeoutMs = syncConfig.SyncDispatcherAllocateTimeoutMs;
    private readonly ILogger _logger = logManager.GetClassLogger<BalFetcher>();

    public async Task<bool> EnsureRange(BlockHeader from, BlockHeader to, CancellationToken token)
    {
        for (ulong windowStart = from.Number + 1; windowStart <= to.Number; windowStart += WindowSize)
        {
            ulong windowEnd = Math.Min(windowStart + WindowSize - 1, to.Number);

            int noProgress = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Rescan each round: the forward BlockDownloader stores BALs as it suggests blocks, so gaps can close without us.
                using ArrayPoolList<BlockHeader> missing = new((int)(windowEnd - windowStart + 1));
                for (ulong number = windowStart; number <= windowEnd; number++)
                {
                    BlockHeader? header = blockTree.FindHeader(number);
                    if (header?.Hash is null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Header missing for block {number}");
                        return false;
                    }

                    if (!balStore.Exists(number, header.Hash))
                        missing.Add(header);
                }

                if (missing.Count == 0) break;
                if (noProgress >= MaxNoProgressRounds)
                {
                    if (_logger.IsWarn) _logger.Warn($"No peer served block access lists for {missing.Count} of blocks {windowStart}..{windowEnd} after {MaxNoProgressRounds} attempts.");
                    return false;
                }

                SyncPeerAllocation allocation = await peerPool.Allocate(PeerStrategy, AllocationContexts.State, _allocateTimeoutMs, token);
                int stored;
                try
                {
                    stored = allocation.Current is null ? 0 : await FetchFromPeer(allocation.Current, missing, token);
                }
                finally
                {
                    peerPool.Free(allocation);
                }

                if (stored == 0)
                    noProgress++;
                else
                    noProgress = 0;
            }
        }

        return true;
    }

    private async Task<int> FetchFromPeer(PeerInfo peer, IReadOnlyList<BlockHeader> missing, CancellationToken token)
    {
        ISyncPeer syncPeer = peer.SyncPeer;
        bool snap2 = syncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer snap)
                     && snap.SnapProtocolVersion >= SnapVersions.Snap2;
        if (!snap2 && !syncPeer.SupportsBlockAccessLists()) return 0;

        int stored = 0;
        try
        {
            if (snap2)
            {
                using ArrayPoolList<ValueHash256> hashes = new(missing.Count);
                foreach (BlockHeader header in missing) hashes.Add(header.Hash!.ValueHash256);

                using IByteArrayList response = await snap.GetBlockAccessLists(hashes, token);
                for (int i = 0; i < missing.Count; i++)
                    if (TryStore(missing[i], i < response.Count ? response[i] : default)) stored++;
            }
            else
            {
                // eth/71 caps the hash list on both the request and the response decoder; going over it
                // makes the peer reject the frame as a breach of protocol and disconnect.
                int count = Math.Min(missing.Count, GethSyncLimits.MaxBodyFetch);
                using ArrayPoolList<Hash256> hashes = new(count);
                for (int i = 0; i < count; i++) hashes.Add(missing[i].Hash!);

                using IOwnedReadOnlyList<byte[]?> response = await syncPeer.GetBlockAccessLists(hashes, token);
                for (int i = 0; i < count; i++)
                    if (TryStore(missing[i], i < response.Count ? response[i] : null)) stored++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error fetching block access lists from {peer}: {e}");
        }

        if (stored == 0) peerPool.ReportWeakPeer(peer, AllocationContexts.State);
        return stored;
    }

    private bool TryStore(BlockHeader header, ReadOnlySpan<byte> rlp)
    {
        if (rlp.IsEmpty || !BlockAccessListHashValidator.Validate(header, rlp, out _)) return false;
        balStore.Insert(header.Number, header.Hash!, rlp);
        return true;
    }

    private sealed class BalCapablePeerAllocationStrategy(IPeerAllocationStrategy strategy)
        : FilterPeerAllocationStrategy(strategy)
    {
        protected override bool Filter(PeerInfo peer) =>
            (peer.SyncPeer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snap)
                && snap.SnapProtocolVersion >= SnapVersions.Snap2)
            || peer.SyncPeer.SupportsBlockAccessLists();
    }
}
