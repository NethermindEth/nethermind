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
using Nethermind.Stats.Model;
using Nethermind.Stats.SyncLimits;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.FastSync;

/// <summary>Fills gaps in the BALs healing needs for a block range - a backstop for
/// <see cref="FastBlocks.BlockAccessListsSyncFeed"/>, which normally has them by this point.</summary>
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

    // Peers whose last answer held none of the window's missing BALs, skipped until the window completes or no other
    // peer is eligible. Kept across calls, as healing retries the same range.
    private readonly HashSet<PeerInfo> _answeredEmpty = [];
    private readonly Lock _answeredEmptyLock = new();

    public async Task<bool> EnsureRange(BlockHeader from, BlockHeader to, CancellationToken token)
    {
        for (ulong windowStart = from.Number + 1; windowStart <= to.Number; windowStart += WindowSize)
        {
            ulong windowEnd = Math.Min(windowStart + WindowSize - 1, to.Number);

            int noProgress = 0;
            bool fetched = false;
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

                if (missing.Count == 0)
                {
                    // A peer that lacked this window's BALs may hold the next window's. A window that was already
                    // complete leaves the set alone, so a retried range still skips them at the window that stalled.
                    if (fetched) lock (_answeredEmptyLock) _answeredEmpty.Clear();
                    break;
                }
                if (noProgress >= MaxNoProgressRounds)
                {
                    if (_logger.IsWarn) _logger.Warn($"No peer served block access lists for {missing.Count} of blocks {windowStart}..{windowEnd} after {MaxNoProgressRounds} attempts.");
                    return false;
                }

                fetched = true;
                int stored;
                using (SyncPeerAllocation allocation = await peerPool.Allocate(new SkipAnsweredEmptyStrategy(this), AllocationContexts.State, _allocateTimeoutMs, token))
                {
                    if (allocation.Current is null)
                    {
                        // No eligible peer: let the ones that answered empty be asked again.
                        lock (_answeredEmptyLock) _answeredEmpty.Clear();
                        stored = 0;
                    }
                    else
                    {
                        stored = await FetchFromPeer(allocation.Current, missing, token);
                    }
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

        int requested = snap2 ? missing.Count : Math.Min(missing.Count, GethSyncLimits.MaxBodyFetch);
        int stored = 0;
        string? invalidBalError = null;
        bool requestFailed = false;
        try
        {
            IByteArrayList response;
            if (snap2)
            {
                using ArrayPoolList<ValueHash256> hashes = new(requested);
                foreach (BlockHeader header in missing) hashes.Add(header.Hash!.ValueHash256);

                response = await snap.GetBlockAccessLists(hashes, token);
            }
            else
            {
                using ArrayPoolList<Hash256> hashes = new(requested);
                for (int i = 0; i < requested; i++) hashes.Add(missing[i].Hash!);

                response = new ByteArrayListAdapter(await syncPeer.GetBlockAccessLists(hashes, token)!);
            }

            using (response)
            {
                for (int i = 0; i < requested; i++)
                {
                    ReadOnlySpan<byte> rlp = i < response.Count ? response[i] : default;
                    if (rlp.IsEmpty) continue;
                    if (TryStore(missing[i], rlp, out string? error)) stored++;
                    else invalidBalError ??= error;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            requestFailed = true;
            if (_logger.IsDebug) _logger.Debug($"Error fetching block access lists from {peer}: {e}");
        }

        // An empty entry is a well-formed answer from a peer that does not have that BAL. A list that does not
        // match the header it was requested by is provable misbehaviour; a failed request may be an honest timeout.
        if (invalidBalError is not null) peerPool.ReportBreachOfProtocol(peer, DisconnectReason.InvalidTxOrUncle, $"invalid block access list: {invalidBalError}");
        else if (requestFailed) peerPool.ReportWeakPeer(peer, AllocationContexts.State);
        else if (stored == 0) lock (_answeredEmptyLock) _answeredEmpty.Add(peer);
        return stored;
    }

    private bool TryStore(BlockHeader header, ReadOnlySpan<byte> rlp, out string? error)
    {
        if (!BlockAccessListHashValidator.Validate(header, rlp, out error)) return false;
        balStore.Insert(header.Number, header.Hash!, rlp);
        return true;
    }

    private sealed class SkipAnsweredEmptyStrategy(BalFetcher fetcher) : FilterPeerAllocationStrategy(PeerStrategy)
    {
        protected override bool Filter(PeerInfo peer)
        {
            lock (fetcher._answeredEmptyLock) return !fetcher._answeredEmpty.Contains(peer);
        }
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
