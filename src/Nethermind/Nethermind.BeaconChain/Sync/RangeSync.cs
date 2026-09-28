// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
/// column sidecars before yielding (Fulu columns from the peers custodying them, Gloas columns from the
/// batch's peer), so the gossip-fed availability gates
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
/// Fulu and Gloas blocks before the <see cref="DataAvailabilityBoundary"/> of <paramref name="clock"/> get no
/// column request (fulu/fork-choice.md <c>is_data_available</c> demands none).
/// </para>
/// </remarks>
/// <param name="clock">The one wall clock the importer's availability gate also reads, so both agree on the window.</param>
public class RangeSync(IBeaconSyncPeerPool peerPool, ILogManager logManager, DataColumnSidecarPool sidecarPool, BeaconChainSpec spec, SlotClock clock, BeaconDiscovery? discovery = null)
{
    /// <summary>
    /// Half an epoch per request. Larger batches trip peers' response rate limits, time out, and
    /// burn the peer-failure budget — with few connected mainnet peers that spirals into starvation.
    /// </summary>
    public const ulong DefaultBatchSize = 16;

    private const int FailuresBeforeBatchShrink = 3;

    /// <summary>How many peers <see cref="FetchGloasColumnsByRootAsync"/> and <see cref="FetchColumnsByRootAsync"/> ask before giving up for this call.</summary>
    private const int MaxByRootColumnPeers = 3;

    /// <summary>How many distinct peers one batch's Fulu columns are requested from.</summary>
    private const int MaxColumnPeersPerBatch = 8;
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
            await FetchColumnsForBatchAsync(batch.Value.Blocks, token);
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
    /// Requests and verifies this node's missing sampled data column sidecars for every blob-carrying block in
    /// <paramref name="blocks"/>, adding verified sidecars to <see cref="sidecarPool"/> so
    /// <see cref="DataAvailability.CustodySamplingAvailability"/> can see them when the importer checks a
    /// range-synced block. Never fails the batch: a request failure or an unverifiable sidecar only penalizes
    /// the peer, mirroring <see cref="FetchAndVerifyBatchAsync"/>'s parent-linkage handling.
    /// </summary>
    /// <remarks>
    /// Only Fulu-shaped blocks inside the data availability window are considered and the request window spans only them: a Gloas block's
    /// commitments are in its bid and its sidecars have the Gloas shape, so it has no part in a Fulu request. Each column is asked of one
    /// peer that custodies it (<see cref="AssignColumns"/>), since fulu/p2p-interface.md DataColumnSidecarsByRange serves custodied columns only.
    /// </remarks>
    private async Task FetchColumnsForBatchAsync(IReadOnlyList<ForkedSignedBeaconBlock> blocks, CancellationToken token)
    {
        ulong windowStartEpoch = DataAvailabilityStartEpoch();
        Dictionary<Hash256, BeaconBlock> blobBlocksByRoot = [];
        ulong startSlot = ulong.MaxValue;
        ulong endSlot = 0;
        foreach (ForkedSignedBeaconBlock block in blocks)
        {
            if (block is not ForkedSignedBeaconBlock.OfFulu { Block.Message: { } message }
                || !IsInDataAvailabilityWindow(message.Slot, windowStartEpoch))
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

        List<ulong> missing = [];
        foreach (ulong column in custody.SampledColumns)
        {
            foreach (Hash256 root in blobBlocksByRoot.Keys)
            {
                if (!sidecarPool.TryGet(root, column, out _))
                {
                    missing.Add(column);
                    break;
                }
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        ulong count = endSlot - startSlot + 1;
        List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = AssignColumns(missing, peerPool.GetBestPeers(endSlot), MaxColumnPeersPerBatch);
        Task<IReadOnlyList<DataColumnSidecar>?>[] responses = new Task<IReadOnlyList<DataColumnSidecar>?>[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            responses[i] = RequestColumnsByRangeAsync(requests[i].Peer, startSlot, count, requests[i].Columns, token);
        }

        await Task.WhenAll(responses);
        for (int i = 0; i < requests.Count; i++)
        {
            if (responses[i].Result is { } sidecars)
            {
                AddVerifiedSidecars(requests[i].Peer, sidecars, blobBlocksByRoot);
            }
        }
    }

    /// <returns>The peer's sidecars, or <c>null</c> when the request failed and the peer was penalized.</returns>
    private static async Task<IReadOnlyList<DataColumnSidecar>?> RequestColumnsByRangeAsync(IBeaconSyncPeer peer, ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
    {
        try
        {
            return await peer.RequestDataColumnSidecarsByRangeAsync(startSlot, count, columns, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Data-column-sidecars-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Adds each sidecar that verifies against the block it names in <paramref name="blocksByRoot"/>; any other penalizes <paramref name="peer"/>.</summary>
    private void AddVerifiedSidecars(IBeaconSyncPeer peer, IReadOnlyList<DataColumnSidecar> sidecars, Dictionary<Hash256, BeaconBlock> blocksByRoot)
    {
        foreach (DataColumnSidecar sidecar in sidecars)
        {
            if (sidecar.SignedBlockHeader?.Message is not { } header)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, "Data column sidecar has no block header");
                continue;
            }

            Hash256 sidecarBlockRoot = SszRoots.HashTreeRoot(header);
            bool requested = blocksByRoot.TryGetValue(sidecarBlockRoot, out BeaconBlock? block);
            // A column an earlier reply already supplied is not verified again.
            if (requested && sidecarPool.TryGet(sidecarBlockRoot, sidecar.Index, out _))
            {
                continue;
            }

            if (!requested
                || !DataColumnAvailability.IsVerifiedColumnOf(sidecar, sidecarBlockRoot, block!.Body!.BlobKzgCommitments!, spec))
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Data column sidecar at slot {header.Slot} column {sidecar.Index} failed verification");
                continue;
            }

            sidecarPool.Add(sidecarBlockRoot, header.Slot, sidecar);
        }
    }

    /// <summary>
    /// Assigns each of <paramref name="columns"/> to one of <paramref name="peers"/> that custodies it, using at most
    /// <paramref name="maxPeers"/> distinct peers. A column no usable peer custodies is left out.
    /// </summary>
    /// <remarks>
    /// Deterministic: a peer with advertised custody is preferred over one known only by its <c>CUSTODY_REQUIREMENT</c>
    /// floor, then the peer with the fewest columns so far, then the earlier peer in <paramref name="peers"/>.
    /// </remarks>
    /// <returns>One entry per chosen peer, in the order the peers were first chosen.</returns>
    internal static List<(IBeaconSyncPeer Peer, ulong[] Columns)> AssignColumns(IReadOnlyList<ulong> columns, IReadOnlyList<IBeaconSyncPeer> peers, int maxPeers)
    {
        PeerColumnCustody[] custodies = new PeerColumnCustody[peers.Count];
        for (int i = 0; i < peers.Count; i++)
        {
            custodies[i] = peers[i].Custody;
        }

        List<ulong>?[] assigned = new List<ulong>?[peers.Count];
        List<int> chosenOrder = [];
        foreach (ulong column in columns)
        {
            int best = -1;
            for (int i = 0; i < peers.Count; i++)
            {
                if (!custodies[i].Custodies(column) || (assigned[i] is null && chosenOrder.Count >= maxPeers))
                {
                    continue;
                }

                if (best < 0 || IsBetterColumnPeer(custodies[i], assigned[i]?.Count ?? 0, custodies[best], assigned[best]?.Count ?? 0))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                continue;
            }

            if (assigned[best] is null)
            {
                assigned[best] = [];
                chosenOrder.Add(best);
            }

            assigned[best]!.Add(column);
        }

        List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = new(chosenOrder.Count);
        foreach (int i in chosenOrder)
        {
            requests.Add((peers[i], [.. assigned[i]!]));
        }

        return requests;
    }

    private static bool IsBetterColumnPeer(PeerColumnCustody candidate, int candidateLoad, PeerColumnCustody best, int bestLoad) =>
        candidate.IsAdvertised != best.IsAdvertised ? candidate.IsAdvertised : candidateLoad < bestLoad;

    /// <summary>
    /// Fetches this node's missing sampled data column sidecars for the Fulu block <paramref name="block"/> by root,
    /// asking up to <see cref="MaxByRootColumnPeers"/> peers each only for the missing columns it custodies, and
    /// adds each verified sidecar to the pool.
    /// </summary>
    /// <param name="blockRoot">The root of <paramref name="block"/>.</param>
    /// <param name="block">The block whose commitments the sidecars are verified against.</param>
    /// <param name="token">Cancels the fetch.</param>
    /// <returns>
    /// Whether every sampled column is now held, or no column is demanded: the block commits no blobs or its
    /// slot is before the data availability window. <c>false</c> while this node's custody identity is unknown.
    /// </returns>
    /// <remarks>
    /// Peers with advertised custody are asked first; a peer custodying none of the missing columns is skipped
    /// without counting toward the bound, since fulu/p2p-interface.md DataColumnSidecarsByRoot serves custodied columns only.
    /// </remarks>
    public Task<bool> FetchColumnsByRootAsync(Hash256 blockRoot, BeaconBlock block, CancellationToken token) =>
        FetchColumnsByRootAsync(blockRoot, block, new ColumnFetchRotation(clock), token);

    /// <inheritdoc cref="FetchColumnsByRootAsync(Hash256, BeaconBlock, CancellationToken)"/>
    /// <param name="rotation">The custodians earlier fetches for this block asked; this call asks only custodians it has not, all at once.</param>
    /// <remarks>
    /// The requests run in parallel, so a peer that never answers costs one request timeout, not one per peer asked.
    /// </remarks>
    internal async Task<bool> FetchColumnsByRootAsync(Hash256 blockRoot, BeaconBlock block, ColumnFetchRotation rotation, CancellationToken token)
    {
        SszKzgCommitment[] commitments = block.Body?.BlobKzgCommitments ?? [];
        if (commitments.Length == 0 || !IsInDataAvailabilityWindow(block.Slot, DataAvailabilityStartEpoch()))
        {
            return true;
        }

        if (_custodySource.Current is not { } custody)
        {
            return false;
        }

        List<ulong> missing = MissingColumns(blockRoot, custody);
        // A by-root request names the block, so a custodian whose last status head is behind it may still serve it (fulu/p2p-interface.md DataColumnSidecarsByRoot).
        // OrderBy is stable, so the pool's own order breaks ties.
        IBeaconSyncPeer[] custodians = [.. peerPool.GetBestPeers(0).Where(p => p.Custody.CountCustodied(missing) > 0).OrderByDescending(static p => p.Custody.IsAdvertised)];
        List<IBeaconSyncPeer> asked = rotation.Take(custodians, MaxByRootColumnPeers);
        Task<IReadOnlyList<DataColumnSidecar>?>[] responses = new Task<IReadOnlyList<DataColumnSidecar>?>[asked.Count];
        for (int i = 0; i < asked.Count; i++)
        {
            PeerColumnCustody peerCustody = asked[i].Custody;
            responses[i] = RequestColumnsByRootAsync(asked[i], blockRoot, [.. missing.Where(peerCustody.Custodies)], static (peer, ids, t) => peer.RequestDataColumnSidecarsByRootAsync(ids, t), token);
        }

        await Task.WhenAll(responses);
        Dictionary<Hash256, BeaconBlock> blocksByRoot = new() { [blockRoot] = block };
        for (int i = 0; i < asked.Count; i++)
        {
            if (responses[i].Result is { } sidecars)
            {
                AddVerifiedSidecars(asked[i], sidecars, blocksByRoot);
            }
        }

        return MissingColumns(blockRoot, custody).Count == 0;
    }

    /// <returns>The peer's sidecars, or <c>null</c> when the request failed and the peer was penalized.</returns>
    private static async Task<IReadOnlyList<TSidecar>?> RequestColumnsByRootAsync<TSidecar>(
        IBeaconSyncPeer peer,
        Hash256 blockRoot,
        ulong[] columns,
        Func<IBeaconSyncPeer, DataColumnsByRootIdentifier[], CancellationToken, Task<IReadOnlyList<TSidecar>>> request,
        CancellationToken token)
    {
        try
        {
            return await request(peer, [new DataColumnsByRootIdentifier { BlockRoot = blockRoot, Columns = columns }], token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Data-column-sidecars-by-root for {blockRoot} failed: {e.Message}");
            return null;
        }
    }

    private List<ulong> MissingColumns(Hash256 blockRoot, NodeColumnCustody custody)
    {
        List<ulong> missing = [];
        foreach (ulong column in custody.SampledColumns)
        {
            if (!sidecarPool.TryGet(blockRoot, column, out _))
            {
                missing.Add(column);
            }
        }

        return missing;
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
    /// The peers are asked at once, each for the missing columns it custodies, so a peer that never answers costs one request
    /// timeout, not one per peer asked. A sidecar that fails verification penalizes its peer; the other sidecars in that
    /// response still count, since each is verified on its own against the bid.
    /// </remarks>
    public Task<bool> FetchGloasColumnsByRootAsync(Hash256 blockRoot, ExecutionPayloadBid bid, CancellationToken token) =>
        FetchGloasColumnsByRootAsync(blockRoot, bid, new ColumnFetchRotation(clock), token);

    /// <inheritdoc cref="FetchGloasColumnsByRootAsync(Hash256, ExecutionPayloadBid, CancellationToken)"/>
    /// <param name="rotation">The custodians earlier fetches for this block asked; this call asks only custodians it has not, all at once.</param>
    internal async Task<bool> FetchGloasColumnsByRootAsync(Hash256 blockRoot, ExecutionPayloadBid bid, ColumnFetchRotation rotation, CancellationToken token)
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

        ulong[] missing = MissingGloasColumns(blockRoot, custody);
        if (missing.Length == 0)
        {
            return true;
        }

        // A by-root request names the block, so a custodian whose last status head is behind it may still serve it (fulu/p2p-interface.md DataColumnSidecarsByRoot).
        IBeaconSyncPeer[] custodians = [.. peerPool.GetBestPeers(0).Where(p => p.Custody.CountCustodied(missing) > 0)];
        List<IBeaconSyncPeer> asked = rotation.Take(custodians, MaxByRootColumnPeers);
        ulong[][] requested = new ulong[asked.Count][];
        Task<IReadOnlyList<DataColumnSidecarGloas>?>[] responses = new Task<IReadOnlyList<DataColumnSidecarGloas>?>[asked.Count];
        for (int i = 0; i < asked.Count; i++)
        {
            PeerColumnCustody peerCustody = asked[i].Custody;
            requested[i] = [.. missing.Where(peerCustody.Custodies)];
            responses[i] = RequestColumnsByRootAsync(asked[i], blockRoot, requested[i], static (peer, ids, t) => peer.RequestGloasDataColumnSidecarsByRootAsync(ids, t), token);
        }

        await Task.WhenAll(responses);
        for (int i = 0; i < asked.Count; i++)
        {
            foreach (DataColumnSidecarGloas sidecar in responses[i].Result ?? [])
            {
                // A column an earlier reply already supplied is not verified again.
                if (sidecar.BeaconBlockRoot == blockRoot && sidecarPool.TryGetGloas(blockRoot, sidecar.Index, out _))
                {
                    continue;
                }

                if (sidecar.BeaconBlockRoot != blockRoot || !IsVerifiedGloasColumnOf(sidecar, bid.Slot, commitments, requested[i]))
                {
                    asked[i].ReportFailure(PeerFailureReason.ProtocolViolation, $"Gloas data column sidecar at slot {sidecar.Slot} column {sidecar.Index} failed verification");
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

    /// <summary>The first epoch whose columns are demanded. The window moves every epoch, so callers recompute it per use.</summary>
    private ulong DataAvailabilityStartEpoch() => DataAvailabilityBoundary.Compute(clock.CurrentEpoch, spec);

    private bool IsInDataAvailabilityWindow(ulong slot, ulong windowStartEpoch) => spec.GetEpoch(slot) >= windowStartEpoch;

    /// <summary>The custodians the by-root column fetches for one block have asked, so repeated fetches rotate through every custodian.</summary>
    /// <remarks>
    /// A fetch takes custodians not asked yet, those absent at the previous fetch first but for one place kept for a custodian
    /// seen before, so newcomers arriving before every fetch cannot keep an earlier custodian from being asked. Once every
    /// custodian was asked the rotation starts over, at most once per slot of <paramref name="clock"/>. Only the custodians of
    /// the latest fetch are remembered as asked. Not thread-safe.
    /// </remarks>
    internal sealed class ColumnFetchRotation(SlotClock clock)
    {
        private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
        private HashSet<string> _previous = new(StringComparer.Ordinal);
        private ulong _cycleSlot = clock.CurrentSlot;

        /// <summary>The custodians recorded as asked; for tests.</summary>
        internal int AskedCount => _asked.Count;

        /// <summary>Takes up to <paramref name="max"/> of <paramref name="custodians"/>, in their order within each group, and records them as asked.</summary>
        public List<IBeaconSyncPeer> Take(IReadOnlyList<IBeaconSyncPeer> custodians, int max)
        {
            HashSet<string> current = new(custodians.Select(static p => p.Id), StringComparer.Ordinal);
            _asked.IntersectWith(current);
            ulong slot = clock.CurrentSlot;
            if (slot > _cycleSlot && _asked.Count == current.Count)
            {
                _asked.Clear();
                _cycleSlot = slot;
            }

            List<IBeaconSyncPeer> taken = [];
            TakeUpTo(max - 1, newcomers: true);
            TakeUpTo(max, newcomers: false);
            TakeUpTo(max, newcomers: true);
            _previous = current;
            return taken;

            void TakeUpTo(int limit, bool newcomers)
            {
                foreach (IBeaconSyncPeer peer in custodians)
                {
                    if (taken.Count < limit && _previous.Contains(peer.Id) != newcomers && _asked.Add(peer.Id))
                    {
                        taken.Add(peer);
                    }
                }
            }
        }
    }
}
