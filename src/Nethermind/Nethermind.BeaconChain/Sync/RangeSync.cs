// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// Downloads the canonical block range above an anchor block in batches over
/// <c>beacon_blocks_by_range</c>, yielding parent-linked blocks in import order.
/// </summary>
/// <remarks>
/// <para>
/// Peers are taken round-robin from the pool and blocks are imported sequentially by the caller.
/// Every batch is verified by root linkage before anything is yielded: the first block's
/// <c>parent_root</c> must be the hash tree root of the last yielded block (initially the anchor),
/// and each subsequent block must link to its predecessor. A mismatching batch is dropped, the
/// offending peer penalized, and the range re-requested from another peer — starting again from the
/// slot after the last yielded block, which also recovers from a peer that falsely returned an
/// empty range. Repeated failures halve the batch size down to a single slot.
/// </para>
/// <para>
/// A verified batch containing blob-carrying blocks additionally requests this node's sampled data
/// column sidecars from the same peer before yielding, so the gossip-fed availability gates
/// (<see cref="DataAvailability.CustodySamplingAvailability"/> for a Fulu block,
/// <see cref="DataAvailability.GloasCustodySamplingAvailability"/> for a Gloas envelope) have something
/// to check against for a range-synced block. Fulu and Gloas blocks get one request each, over a window
/// that spans only that fork's blocks, since the two sidecar shapes cannot share a response. A Gloas
/// sidecar carries no commitments, so it is verified against the bid of the batch block it names. A
/// column-fetch failure penalizes the peer but does not fail the batch: a block whose columns are still
/// missing simply fails its availability gate and is retried next round, since the sync tip only
/// advances past a successfully imported block.
/// </para>
/// <para>
/// With a <see cref="SlotClock"/>, Gloas blocks before the <see cref="DataAvailabilityBoundary"/> get no
/// column request (fulu/fork-choice.md <c>is_data_available</c> demands none); without one, no window applies.
/// </para>
/// </remarks>
public class RangeSync(IBeaconSyncPeerPool peerPool, ILogManager logManager, DataColumnSidecarPool sidecarPool, BeaconChainSpec spec, BeaconDiscovery? discovery = null, SlotClock? clock = null)
{
    /// <summary>
    /// Half an epoch per request. Larger batches trip peers' response rate limits, time out, and
    /// burn the peer-failure budget — with few connected mainnet peers that spirals into starvation.
    /// </summary>
    public const ulong DefaultBatchSize = 16;

    private const int FailuresBeforeBatchShrink = 3;

    /// <summary>How many peers <see cref="FetchGloasColumnsByRootAsync"/> asks before giving up for this call.</summary>
    private const int MaxByRootColumnPeers = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logManager.GetClassLogger<RangeSync>();

    /// <summary>Resolved lazily and cached internally, the same pattern <see cref="BlockImporterFactory"/> uses: discovery has not started when this object is constructed.</summary>
    private readonly INodeColumnCustodySource _custodySource = new DiscoveryNodeCustodySource(discovery);

    /// <summary>
    /// Streams verified-order blocks from <paramref name="anchorSlot"/> (exclusive) up to the target
    /// head; completes when the target is reached. The caller re-invokes as its target advances.
    /// </summary>
    /// <param name="anchorRoot">The block root the first yielded block must link to.</param>
    /// <param name="anchorSlot">The slot of the anchor block.</param>
    /// <param name="targetHeadSlot">Re-evaluated each batch, so the target may move while syncing.</param>
    public async IAsyncEnumerable<ForkedSignedBeaconBlock> Run(
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
            (IReadOnlyList<ForkedSignedBeaconBlock> Blocks, Hash256 LastRoot)? batch = await FetchAndVerifyBatchAsync(peer, nextSlot, count, lastRoot, token);
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
            await FetchColumnsForBatchAsync(peer, batch.Value.Blocks, token);
            await FetchGloasColumnsForBatchAsync(peer, batch.Value.Blocks, token);
            foreach (ForkedSignedBeaconBlock block in batch.Value.Blocks)
            {
                yield return block;
                lastSlot = block.Slot;
            }

            if (batch.Value.Blocks.Count > 0)
            {
                lastRoot = batch.Value.LastRoot;
            }

            nextSlot += count;
        }
    }

    /// <returns>The verified batch and its last block's root, or <c>null</c> when the request failed or the batch did not link up.</returns>
    private async Task<(IReadOnlyList<ForkedSignedBeaconBlock> Blocks, Hash256 LastRoot)?> FetchAndVerifyBatchAsync(
        IBeaconSyncPeer peer,
        ulong startSlot,
        ulong count,
        Hash256 parentRoot,
        CancellationToken token)
    {
        IReadOnlyList<ForkedSignedBeaconBlock> batch;
        try
        {
            batch = await peer.RequestBlocksByRangeAsync(startSlot, count, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // Includes per-request timeouts, which cancel the request without cancelling the sync.
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Blocks-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return null;
        }

        // Slot bounds and ordering are already enforced at the protocol layer; verify parent linkage here.
        Hash256 expectedParent = parentRoot;
        foreach (ForkedSignedBeaconBlock block in batch)
        {
            if (block.ParentRoot != expectedParent)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Block at slot {block.Slot} has parent {block.ParentRoot}, expected {expectedParent}");
                return null;
            }

            expectedParent = block.ComputeMessageRoot();
        }

        return (batch, expectedParent);
    }

    /// <summary>
    /// Requests and verifies this node's sampled data column sidecars for every blob-carrying block in
    /// <paramref name="blocks"/> from <paramref name="peer"/>, adding verified sidecars to
    /// <see cref="sidecarPool"/> so <see cref="DataAvailability.CustodySamplingAvailability"/> can see
    /// them when the importer checks a range-synced block. Never fails the batch: a request failure or
    /// an unverifiable sidecar only penalizes the peer, mirroring <see cref="FetchAndVerifyBatchAsync"/>'s
    /// parent-linkage handling.
    /// </summary>
    /// <remarks>
    /// Only Fulu-shaped blocks are considered and the request window spans only them: a Gloas block's
    /// commitments are in its bid and its sidecars have the Gloas shape, so it has no part in a Fulu request.
    /// </remarks>
    private async Task FetchColumnsForBatchAsync(IBeaconSyncPeer peer, IReadOnlyList<ForkedSignedBeaconBlock> blocks, CancellationToken token)
    {
        Dictionary<Hash256, BeaconBlock> blobBlocksByRoot = [];
        ulong startSlot = ulong.MaxValue;
        ulong endSlot = 0;
        foreach (ForkedSignedBeaconBlock block in blocks)
        {
            if (block is not ForkedSignedBeaconBlock.OfFulu { Block.Message: { } message })
            {
                continue;
            }

            startSlot = Math.Min(startSlot, message.Slot);
            endSlot = Math.Max(endSlot, message.Slot);
            if (message.Body?.BlobKzgCommitments is { Length: > 0 })
            {
                blobBlocksByRoot[SszRoots.HashTreeRoot(message)] = message;
            }
        }

        if (blobBlocksByRoot.Count == 0)
        {
            return;
        }

        // Discovery has not resolved this node's identity yet; nothing to demand columns as.
        if (_custodySource.Current is not { } custody)
        {
            return;
        }

        ulong[] sampledColumns = new ulong[custody.SampledColumns.Count];
        for (int i = 0; i < sampledColumns.Length; i++)
        {
            sampledColumns[i] = custody.SampledColumns[i];
        }

        ulong count = endSlot - startSlot + 1;

        IReadOnlyList<DataColumnSidecar> sidecars;
        try
        {
            sidecars = await peer.RequestDataColumnSidecarsByRangeAsync(startSlot, count, sampledColumns, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Data-column-sidecars-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return;
        }

        foreach (DataColumnSidecar sidecar in sidecars)
        {
            if (sidecar.SignedBlockHeader?.Message is not { } header)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, "Data column sidecar has no block header");
                continue;
            }

            Hash256 sidecarBlockRoot = SszRoots.HashTreeRoot(header);
            if (!blobBlocksByRoot.TryGetValue(sidecarBlockRoot, out BeaconBlock? block)
                || !DataColumnAvailability.IsVerifiedColumnOf(sidecar, sidecarBlockRoot, block.Body!.BlobKzgCommitments!, spec))
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Data column sidecar at slot {header.Slot} column {sidecar.Index} failed verification");
                continue;
            }

            sidecarPool.Add(sidecarBlockRoot, header.Slot, sidecar);
        }
    }

    /// <summary>
    /// Fetches this node's missing sampled Gloas data column sidecars for the block at <paramref name="blockRoot"/>
    /// by root, from up to <see cref="MaxByRootColumnPeers"/> peers, adding each verified sidecar to the pool.
    /// </summary>
    /// <param name="blockRoot">The root of the block that committed <paramref name="bid"/>.</param>
    /// <param name="bid">The bid of that block; sidecars are verified against its commitments and slot.</param>
    /// <param name="token">Cancels the fetch.</param>
    /// <returns>
    /// Whether every sampled column is now held, or no column is demanded: the bid commits no blobs or its
    /// slot is before the data availability window. <c>false</c> while this node's custody identity is unknown.
    /// </returns>
    /// <remarks>
    /// Each peer is asked only for the columns still missing after the previous one. A sidecar that fails
    /// verification penalizes its peer; the other sidecars in that response still count, since each is
    /// verified on its own against the bid.
    /// </remarks>
    public async Task<bool> FetchGloasColumnsByRootAsync(Hash256 blockRoot, ExecutionPayloadBid bid, CancellationToken token)
    {
        SszKzgCommitment[] commitments = bid.BlobKzgCommitments ?? [];
        if (commitments.Length == 0 || !IsInDataAvailabilityWindow(bid.Slot, DataAvailabilityStartEpoch()))
        {
            return true;
        }

        if (_custodySource.Current is not { } custody)
        {
            return false;
        }

        IReadOnlyList<IBeaconSyncPeer> peers = peerPool.GetBestPeers(bid.Slot);
        for (int i = 0; i < peers.Count && i < MaxByRootColumnPeers; i++)
        {
            ulong[] missing = MissingGloasColumns(blockRoot, custody);
            if (missing.Length == 0)
            {
                return true;
            }

            IBeaconSyncPeer peer = peers[i];
            IReadOnlyList<DataColumnSidecarGloas> sidecars;
            try
            {
                sidecars = await peer.RequestGloasDataColumnSidecarsByRootAsync([new DataColumnsByRootIdentifier { BlockRoot = blockRoot, Columns = missing }], token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Data-column-sidecars-by-root for {blockRoot} failed: {e.Message}");
                continue;
            }

            foreach (DataColumnSidecarGloas sidecar in sidecars)
            {
                if (sidecar.BeaconBlockRoot != blockRoot || !IsVerifiedGloasColumnOf(sidecar, bid.Slot, commitments, missing))
                {
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Gloas data column sidecar at slot {sidecar.Slot} column {sidecar.Index} failed verification");
                    continue;
                }

                sidecarPool.AddGloas(sidecar);
            }
        }

        return MissingGloasColumns(blockRoot, custody).Length == 0;
    }

    /// <summary>
    /// Requests and verifies this node's sampled Gloas data column sidecars for every blob-carrying Gloas block
    /// in <paramref name="blocks"/> inside the data availability window, from <paramref name="peer"/>. Never
    /// fails the batch, as <see cref="FetchColumnsForBatchAsync"/>.
    /// </summary>
    /// <remarks>
    /// The request window spans only those blocks, so it lies wholly in Gloas epochs as the Gloas request requires.
    /// A sidecar must name a Gloas block of this batch and that block's slot, and pass gloas/p2p-interface.md
    /// <c>verify_data_column_sidecar</c> and <c>verify_data_column_sidecar_kzg_proofs</c> against the block's bid.
    /// </remarks>
    private async Task FetchGloasColumnsForBatchAsync(IBeaconSyncPeer peer, IReadOnlyList<ForkedSignedBeaconBlock> blocks, CancellationToken token)
    {
        ulong windowStartEpoch = DataAvailabilityStartEpoch();
        Dictionary<Hash256, (ulong Slot, ExecutionPayloadBid Bid)> bidsByRoot = [];
        ulong startSlot = ulong.MaxValue;
        ulong endSlot = 0;
        foreach (ForkedSignedBeaconBlock block in blocks)
        {
            if (block is not ForkedSignedBeaconBlock.OfGloas { Block.Message: { } message }
                || message.Body?.SignedExecutionPayloadBid?.Message is not { BlobKzgCommitments.Length: > 0 } bid
                || !IsInDataAvailabilityWindow(message.Slot, windowStartEpoch))
            {
                continue;
            }

            startSlot = Math.Min(startSlot, message.Slot);
            endSlot = Math.Max(endSlot, message.Slot);
            bidsByRoot[SszRoots.HashTreeRoot(message)] = (message.Slot, bid);
        }

        if (bidsByRoot.Count == 0 || _custodySource.Current is not { } custody)
        {
            return;
        }

        ulong[] sampledColumns = [.. custody.SampledColumns];
        ulong count = endSlot - startSlot + 1;

        IReadOnlyList<DataColumnSidecarGloas> sidecars;
        try
        {
            sidecars = await peer.RequestGloasDataColumnSidecarsByRangeAsync(startSlot, count, sampledColumns, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Gloas data-column-sidecars-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return;
        }

        foreach (DataColumnSidecarGloas sidecar in sidecars)
        {
            if (sidecar.BeaconBlockRoot is not { } root
                || !bidsByRoot.TryGetValue(root, out (ulong Slot, ExecutionPayloadBid Bid) block)
                || !IsVerifiedGloasColumnOf(sidecar, block.Slot, block.Bid.BlobKzgCommitments!, sampledColumns))
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Gloas data column sidecar at slot {sidecar.Slot} column {sidecar.Index} failed verification");
                continue;
            }

            sidecarPool.AddGloas(sidecar);
        }
    }

    /// <remarks>KZG does not bind <c>sidecar.slot</c>, so a sidecar naming another slot would still verify against the right bid.</remarks>
    private static bool IsVerifiedGloasColumnOf(DataColumnSidecarGloas sidecar, ulong blockSlot, SszKzgCommitment[] commitments, ulong[] requestedColumns) =>
        sidecar.Slot == blockSlot
        && Array.IndexOf(requestedColumns, sidecar.Index) >= 0
        && DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments);

    private ulong[] MissingGloasColumns(Hash256 blockRoot, NodeColumnCustody custody)
    {
        List<ulong> missing = [];
        foreach (ulong column in custody.SampledColumns)
        {
            if (!sidecarPool.TryGetGloas(blockRoot, column, out _))
            {
                missing.Add(column);
            }
        }

        return [.. missing];
    }

    /// <summary>The first epoch whose columns are demanded; 0 without a clock. The window moves every epoch, so callers recompute it per use.</summary>
    private ulong DataAvailabilityStartEpoch() => clock is null ? 0 : DataAvailabilityBoundary.Compute(clock.CurrentEpoch, spec);

    private bool IsInDataAvailabilityWindow(ulong slot, ulong windowStartEpoch) => spec.GetEpoch(slot) >= windowStartEpoch;
}
