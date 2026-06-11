// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// Downloads the canonical block range above an anchor block in batches over
/// <c>beacon_blocks_by_range</c>, yielding parent-linked blocks in import order.
/// </summary>
/// <remarks>
/// Peers are taken round-robin from the pool and blocks are imported sequentially by the caller.
/// Every batch is verified by root linkage before anything is yielded: the first block's
/// <c>parent_root</c> must be the hash tree root of the last yielded block (initially the anchor),
/// and each subsequent block must link to its predecessor. A mismatching batch is dropped, the
/// offending peer penalized, and the range re-requested from another peer — starting again from the
/// slot after the last yielded block, which also recovers from a peer that falsely returned an
/// empty range. Repeated failures halve the batch size down to a single slot.
/// </remarks>
public class RangeSync(IBeaconSyncPeerPool peerPool, ILogManager logManager)
{
    /// <summary>Two epochs per request; well under the spec cap of 128 blocks.</summary>
    public const ulong DefaultBatchSize = 64;

    private const int FailuresBeforeBatchShrink = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logManager.GetClassLogger<RangeSync>();

    /// <summary>
    /// Streams verified-order blocks from <paramref name="anchorSlot"/> (exclusive) up to the target
    /// head; completes when the target is reached. The caller re-invokes as its target advances.
    /// </summary>
    /// <param name="anchorRoot">The block root the first yielded block must link to.</param>
    /// <param name="anchorSlot">The slot of the anchor block.</param>
    /// <param name="targetHeadSlot">Re-evaluated each batch, so the target may move while syncing.</param>
    public async IAsyncEnumerable<SignedBeaconBlock> Run(
        Hash256 anchorRoot,
        ulong anchorSlot,
        Func<ulong> targetHeadSlot,
        [EnumeratorCancellation] CancellationToken token)
    {
        Hash256 lastRoot = anchorRoot;
        ulong lastSlot = anchorSlot;
        ulong nextSlot = anchorSlot + 1;
        ulong batchSize = DefaultBatchSize;
        int peerCursor = 0;
        int consecutiveFailures = 0;

        while (!token.IsCancellationRequested && nextSlot <= targetHeadSlot())
        {
            ulong count = Math.Min(batchSize, targetHeadSlot() - nextSlot + 1);
            IReadOnlyList<IBeaconSyncPeer> peers = peerPool.GetBestPeers(nextSlot);
            if (peers.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"No beacon chain peers with head at or past slot {nextSlot}; waiting");
                await Task.Delay(RetryDelay, token);
                continue;
            }

            IBeaconSyncPeer peer = peers[peerCursor++ % peers.Count];
            (IReadOnlyList<SignedBeaconBlock> Blocks, Hash256 LastRoot)? batch = await FetchAndVerifyBatchAsync(peer, nextSlot, count, lastRoot, token);
            if (batch is null)
            {
                consecutiveFailures++;
                if (consecutiveFailures % FailuresBeforeBatchShrink == 0 && batchSize > 1)
                {
                    batchSize = Math.Max(1, batchSize / 2);
                    if (_logger.IsDebug) _logger.Debug($"Shrinking range sync batch size to {batchSize} after repeated failures");
                }

                // Restart from the slot after the last verified block: a linkage failure may stem
                // from an earlier batch that falsely came back empty.
                nextSlot = lastSlot + 1;
                await Task.Delay(RetryDelay, token);
                continue;
            }

            consecutiveFailures = 0;
            batchSize = DefaultBatchSize;
            foreach (SignedBeaconBlock block in batch.Value.Blocks)
            {
                yield return block;
                lastSlot = block.Message!.Slot;
            }

            if (batch.Value.Blocks.Count > 0)
            {
                lastRoot = batch.Value.LastRoot;
            }

            nextSlot += count;
        }
    }

    /// <returns>The verified batch and its last block's root, or <c>null</c> when the request failed or the batch did not link up.</returns>
    private async Task<(IReadOnlyList<SignedBeaconBlock> Blocks, Hash256 LastRoot)?> FetchAndVerifyBatchAsync(
        IBeaconSyncPeer peer,
        ulong startSlot,
        ulong count,
        Hash256 parentRoot,
        CancellationToken token)
    {
        IReadOnlyList<SignedBeaconBlock> batch;
        try
        {
            batch = await peer.RequestBlocksByRangeAsync(startSlot, count, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            peer.ReportFailure($"Blocks-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return null;
        }

        // Slot bounds and ordering are already enforced at the protocol layer; verify parent linkage here.
        Hash256 expectedParent = parentRoot;
        foreach (SignedBeaconBlock block in batch)
        {
            if (block.Message!.ParentRoot != expectedParent)
            {
                peer.ReportFailure($"Block at slot {block.Message.Slot} has parent {block.Message.ParentRoot}, expected {expectedParent}");
                return null;
            }

            expectedParent = SszRoots.HashTreeRoot(block.Message);
        }

        return (batch, expectedParent);
    }
}
