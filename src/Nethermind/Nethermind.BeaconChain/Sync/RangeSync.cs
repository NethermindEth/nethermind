// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
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
/// offending peer penalized, and the range re-requested from another peer - starting again from the
/// slot after the last yielded block, which also recovers from a peer that falsely returned an
/// empty range. Repeated failures halve the batch size down to a single slot.
/// </para>
/// <para>
/// A verified batch containing blob-carrying blocks additionally requests this node's sampled data
/// column sidecars before yielding (each column from a peer custodying it, for Fulu and Gloas alike), so the gossip-fed availability gates
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
    /// burn the peer-failure budget - with few connected mainnet peers that spirals into starvation.
    /// </summary>
    public const ulong DefaultBatchSize = 16;

    private const int FailuresBeforeBatchShrink = 3;

    /// <summary>How many peers <see cref="FetchGloasColumnsByRootAsync"/> and <see cref="FetchColumnsByRootAsync"/> ask before giving up for this call.</summary>
    private const int MaxByRootColumnPeers = 3;

    /// <summary>How many distinct peers one batch's Fulu columns are requested from.</summary>
    private const int MaxColumnPeersPerBatch = 8;

    /// <summary>The fewest columns one peer is asked for in a batch, so a small sample still spreads over several custodians rather than one supernode.</summary>
    private const int MinColumnsPerPeer = 2;

    /// <summary>How many times one batch's still-missing columns are requested, each time from peers that have not failed it.</summary>
    private const int MaxColumnRounds = 3;

    /// <summary>No further column round starts once a batch has spent this long on columns; the importer fetches what is left by root.</summary>
    private static readonly TimeSpan ColumnBatchBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logManager.GetClassLogger<RangeSync>();

    /// <summary>Resolved lazily and cached internally, the same pattern <see cref="BlockImporterFactory"/> uses: discovery has not started when this object is constructed.</summary>
    private readonly INodeColumnCustodySource _custodySource = new DiscoveryNodeCustodySource(discovery);

    /// <summary>The anchor a run restarts from when its first block does not link to the unverified anchor it was given; <paramref name="Rejected"/> runs when that happens.</summary>
    public sealed record AnchorFallback(Hash256 Root, ulong Slot, Action Rejected);

    /// <summary>
    /// Streams verified-order blocks from <paramref name="anchorSlot"/> (exclusive) up to the target
    /// head; completes when the target is reached. The caller re-invokes as its target advances.
    /// </summary>
    /// <param name="anchorRoot">The block root the first yielded block must link to.</param>
    /// <param name="anchorSlot">The slot of the anchor block.</param>
    /// <param name="targetHeadSlot">Re-evaluated each batch, so the target may move while syncing.</param>
    /// <param name="fallback">Marks the anchor as unverified: while no block has linked to it, a first block that does not is not held against its peer, and the run restarts from this one.</param>
    public async IAsyncEnumerable<ForkedSignedBeaconBlock> Run(
        Hash256 anchorRoot,
        ulong anchorSlot,
        Func<ulong> targetHeadSlot,
        [EnumeratorCancellation] CancellationToken token,
        AnchorFallback? fallback = null)
    {
        Hash256 lastRoot = anchorRoot;
        ulong lastSlot = anchorSlot;
        ulong nextSlot = anchorSlot + 1;
        ulong batchSize = DefaultBatchSize;
        int peerCursor = 0;
        int consecutiveFailures = 0;

        while (!token.IsCancellationRequested && nextSlot <= targetHeadSlot())
        {
            ulong target = targetHeadSlot();
            // The fallback window is the default batch, not the shrunk one, so failures cannot exclude a peer that serves from inside the range.
            IReadOnlyList<IBeaconSyncPeer> peers = ServingFrom(peerPool.GetBestPeers(nextSlot), nextSlot, Math.Min(nextSlot + DefaultBatchSize - 1, target));
            if (peers.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"No beacon chain peers with head at or past slot {nextSlot} and earliest available slot within the next {DefaultBatchSize} slots; waiting");
                await Task.Delay(RetryDelay, token);
                continue;
            }

            IBeaconSyncPeer peer = peers[peerCursor++ % peers.Count];
            // A peer may answer ResourceUnavailable below its earliest slot (phase0/p2p-interface.md); slots under it are taken as empty and block linkage still checks that.
            ulong from = Math.Max(nextSlot, peer.EarliestAvailableSlot);
            ulong count = Math.Min(batchSize, target - from + 1);
            (IReadOnlyList<ForkedSignedBeaconBlock> Blocks, Hash256[] Roots, bool Linked)? batch = await FetchAndVerifyBatchAsync(peer, from, count, lastRoot, fallback is not null, token);
            if (batch is { Linked: false })
            {
                // The first block names another parent: the unverified anchor is off the peers' chain, not the peer at fault.
                lastRoot = fallback!.Root;
                lastSlot = fallback.Slot;
                nextSlot = lastSlot + 1;
                fallback.Rejected();
                fallback = null;
                continue;
            }

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
            if (batch.Value.Blocks.Count > 0)
            {
                fallback = null;
            }

            if (batchSize != DefaultBatchSize && _logger.IsDebug) _logger.Debug($"Restoring range sync batch size to {DefaultBatchSize} from {batchSize} after a served batch");
            batchSize = DefaultBatchSize;
            await FetchColumnsForBatchAsync(batch.Value.Blocks, batch.Value.Roots, token);
            await FetchGloasColumnsForBatchAsync(batch.Value.Blocks, batch.Value.Roots, token);
            foreach (ForkedSignedBeaconBlock block in batch.Value.Blocks)
            {
                yield return block;
                lastSlot = block.Slot;
            }

            if (batch.Value.Blocks.Count > 0)
            {
                lastRoot = batch.Value.Roots[^1];
            }

            nextSlot = from + count;
        }
    }

    /// <returns>The verified batch and the root of each of its blocks, or <c>null</c> when the request failed or the batch did not link up; not <c>Linked</c> when <paramref name="anchorUnverified"/> and the first block does not link to <paramref name="parentRoot"/>.</returns>
    private async Task<(IReadOnlyList<ForkedSignedBeaconBlock> Blocks, Hash256[] Roots, bool Linked)?> FetchAndVerifyBatchAsync(
        IBeaconSyncPeer peer,
        ulong startSlot,
        ulong count,
        Hash256 parentRoot,
        bool anchorUnverified,
        CancellationToken token)
    {
        IReadOnlyList<ForkedSignedBeaconBlock> batch;
        if (_logger.IsDebug) _logger.Debug($"Requesting blocks [{startSlot}, {startSlot + count}) ({count}) by range from {peer.Id}");
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            batch = await peer.RequestBlocksByRangeAsync(startSlot, count, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Blocks [{startSlot}, {startSlot + count}) by range from {peer.Id} failed after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms: {PeerManager.DescribeFailure(e)}");
            // Includes per-request timeouts, which cancel the request without cancelling the sync.
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Blocks-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return null;
        }

        if (_logger.IsDebug) _logger.Debug($"Blocks [{startSlot}, {startSlot + count}) by range from {peer.Id}: {batch.Count} blocks in {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms");

        // Slot bounds and ordering are already enforced at the protocol layer; verify parent linkage here.
        Hash256 expectedParent = parentRoot;
        Hash256[] roots = new Hash256[batch.Count];
        for (int i = 0; i < roots.Length; i++)
        {
            ForkedSignedBeaconBlock block = batch[i];
            if (block.ParentRoot != expectedParent)
            {
                if (anchorUnverified && expectedParent == parentRoot)
                {
                    return (batch, [], false);
                }

                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Block at slot {block.Slot} has parent {block.ParentRoot}, expected {expectedParent}");
                return null;
            }

            expectedParent = roots[i] = block.ComputeMessageRoot();
        }

        return (batch, roots, true);
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
    /// Peers whose <c>earliest_available_slot</c> is at or before the request's start are preferred; when none is, those serving from at or before the last blob-carrying slot are asked from their earliest slot.
    /// A peer that fails or leaves a requested column unserved is not asked again for the batch; the sidecars a failed reply had already delivered are kept, and up to <see cref="MaxColumnRounds"/> rounds request only what is still missing, within <see cref="ColumnBatchBudget"/>.
    /// Blocks are yielded whatever the outcome: the importer defers a block still missing columns and fetches them by root.
    /// </remarks>
    private async Task FetchColumnsForBatchAsync(IReadOnlyList<ForkedSignedBeaconBlock> blocks, Hash256[] roots, CancellationToken token)
    {
        ulong windowStartEpoch = DataAvailabilityStartEpoch();
        Dictionary<Hash256, BeaconBlock> blobBlocksByRoot = [];
        ulong startSlot = ulong.MaxValue;
        ulong endSlot = 0;
        ulong lastBlobSlot = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not ForkedSignedBeaconBlock.OfFulu { Block.Message: { } message }
                || !IsInDataAvailabilityWindow(message.Slot, windowStartEpoch))
            {
                continue;
            }

            startSlot = Math.Min(startSlot, message.Slot);
            endSlot = Math.Max(endSlot, message.Slot);
            if (message.Body?.BlobKzgCommitments is { Length: > 0 })
            {
                blobBlocksByRoot[roots[i]] = message;
                lastBlobSlot = Math.Max(lastBlobSlot, message.Slot);
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

        HashSet<string> spent = [];
        long startedAt = Stopwatch.GetTimestamp();
        for (int round = 0; round < MaxColumnRounds; round++)
        {
            List<ulong> missing = MissingBatchColumns(custody, blobBlocksByRoot, out ulong firstMissingSlot, out ulong lastMissingSlot);
            if (missing.Count == 0 || (round > 0 && Stopwatch.GetElapsedTime(startedAt) >= ColumnBatchBudget))
            {
                return;
            }

            // Later rounds ask only for the slots still lacking a column, not the whole batch again.
            ulong roundStart = round == 0 ? startSlot : firstMissingSlot;
            ulong roundEnd = round == 0 ? endSlot : lastMissingSlot;
            List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = AssignBatchColumns(missing, roundStart, round == 0 ? lastBlobSlot : lastMissingSlot, spent, boundPerPeer: true);
            if (requests.Count == 0)
            {
                return;
            }

            Task<ColumnsReply>[] responses = new Task<ColumnsReply>[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                // A peer serves from its earliest slot; a request below it may be answered ResourceUnavailable (fulu/p2p-interface.md).
                ulong from = Math.Max(roundStart, requests[i].Peer.EarliestAvailableSlot);
                responses[i] = RequestColumnsByRangeAsync(requests[i].Peer, from, roundEnd - from + 1, requests[i].Columns, token);
            }

            await Task.WhenAll(responses);
            for (int i = 0; i < requests.Count; i++)
            {
                ColumnsReply reply = responses[i].Result;
                AddVerifiedSidecars(requests[i].Peer, reply.Sidecars, blobBlocksByRoot);
                // A peer that failed, or left a column it was asked for unserved, is not asked again for this batch.
                if (reply.Failed || requests[i].Columns.Any(column => !HoldsColumnForAll(column, blobBlocksByRoot)))
                {
                    spent.Add(requests[i].Peer.Id);
                }
            }
        }
    }

    /// <returns>The sampled columns some blob-carrying block of the batch lacks, and the first and last slot of a block lacking one.</returns>
    private List<ulong> MissingBatchColumns(NodeColumnCustody custody, Dictionary<Hash256, BeaconBlock> blobBlocksByRoot, out ulong firstSlot, out ulong lastSlot)
    {
        firstSlot = ulong.MaxValue;
        lastSlot = 0;
        List<ulong> missing = [];
        foreach (ulong column in custody.SampledColumns)
        {
            bool lacking = false;
            foreach ((Hash256 root, BeaconBlock block) in blobBlocksByRoot)
            {
                if (!sidecarPool.TryGet(root, column, out _))
                {
                    lacking = true;
                    firstSlot = Math.Min(firstSlot, block.Slot);
                    lastSlot = Math.Max(lastSlot, block.Slot);
                }
            }

            if (lacking)
            {
                missing.Add(column);
            }
        }

        return missing;
    }

    private bool HoldsColumnForAll(ulong column, Dictionary<Hash256, BeaconBlock> blobBlocksByRoot)
    {
        foreach (Hash256 root in blobBlocksByRoot.Keys)
        {
            if (!sidecarPool.TryGet(root, column, out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Assigns each of <paramref name="missing"/> to a peer reaching <paramref name="startSlot"/> that custodies it, serves the range and is not in
    /// <paramref name="excluded"/>; empty when no such peer exists.
    /// </summary>
    /// <param name="boundPerPeer">Caps each peer at an even share of <paramref name="missing"/>, at least <see cref="MinColumnsPerPeer"/>; a column the cap leaves out
    /// waits for a later round, so only a caller that retries sets it.</param>
    private List<(IBeaconSyncPeer Peer, ulong[] Columns)> AssignBatchColumns(List<ulong> missing, ulong startSlot, ulong lastSlot, HashSet<string>? excluded, bool boundPerPeer)
    {
        IReadOnlyList<IBeaconSyncPeer> reaching = peerPool.GetBestPeers(startSlot);
        IBeaconSyncPeer[] custodians = [.. reaching.Where(p => excluded?.Contains(p.Id) is not true && p.Custody.CountCustodied(missing) > 0)];
        if (custodians.Length == 0)
        {
            LogNoCustodian(missing, reaching.Count);
            return [];
        }

        IReadOnlyList<IBeaconSyncPeer> serving = ServingFrom(custodians, startSlot, lastSlot);
        if (!boundPerPeer || serving.Count == 0)
        {
            return AssignColumns(missing, serving, MaxColumnPeersPerBatch);
        }

        // A lone custodian has to take the lot.
        int askedPeers = Math.Min(MaxColumnPeersPerBatch, serving.Count);
        int perPeer = Math.Max(MinColumnsPerPeer, (missing.Count + askedPeers - 1) / askedPeers);
        return AssignColumns(missing, serving, MaxColumnPeersPerBatch, perPeer);
    }

    /// <summary>
    /// The peers of <paramref name="peers"/> whose Status v2 <c>earliest_available_slot</c> is at or before <paramref name="startSlot"/>, so the range starts inside what they serve;
    /// when none is, those whose earliest slot is at or before <paramref name="endSlot"/>, because the slots below it may be empty and a by-range reply skips empty slots (phase0/p2p-interface.md).
    /// </summary>
    private IReadOnlyList<IBeaconSyncPeer> ServingFrom(IReadOnlyList<IBeaconSyncPeer> peers, ulong startSlot, ulong endSlot)
    {
        IBeaconSyncPeer[] covering = [.. peers.Where(p => p.EarliestAvailableSlot <= startSlot)];
        if (covering.Length > 0)
        {
            if (covering.Length < peers.Count && _logger.IsDebug) _logger.Debug($"Left out {peers.Count - covering.Length} of {peers.Count} sync peers whose earliest available slot is after {startSlot}");
            return covering;
        }

        IBeaconSyncPeer[] overlapping = [.. peers.Where(p => p.EarliestAvailableSlot <= endSlot)];
        if (_logger.IsDebug) _logger.Debug($"No sync peer serves from {startSlot}; {overlapping.Length} of {peers.Count} serve from at or before {endSlot}");
        return overlapping;
    }

    private void LogNoCustodian(List<ulong> missing, int connectedPeers)
    {
        if (_logger.IsDebug) _logger.Debug($"No connected sync peer custodies the missing sampled columns [{string.Join(", ", missing)}]: 0 custodians among {connectedPeers} peers");
    }

    /// <summary>What a by-range column request delivered: every sidecar it read, also when the request then failed and the peer was penalized.</summary>
    private readonly record struct ColumnsReply(IReadOnlyList<DataColumnSidecar> Sidecars, bool Failed);

    private async Task<ColumnsReply> RequestColumnsByRangeAsync(IBeaconSyncPeer peer, ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
    {
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            IReadOnlyList<DataColumnSidecar> sidecars = await peer.RequestDataColumnSidecarsByRangeAsync(startSlot, count, columns, token);
            if (_logger.IsDebug) _logger.Debug($"Columns [{string.Join(", ", columns)}] of slots [{startSlot}, {startSlot + count}) by range from {peer.Id}: {sidecars.Count} sidecars in {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms");
            return new ColumnsReply(sidecars, Failed: false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            IReadOnlyList<DataColumnSidecar> kept = (e as PartialSidecarsException)?.Received ?? [];
            Exception cause = e is PartialSidecarsException { InnerException: { } inner } ? inner : e;
            if (_logger.IsDebug) _logger.Debug($"Columns [{string.Join(", ", columns)}] of slots [{startSlot}, {startSlot + count}) by range from {peer.Id} failed after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms, keeping {kept.Count} sidecars read: {PeerManager.DescribeFailure(cause)}");
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Data-column-sidecars-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return new ColumnsReply(kept, Failed: true);
        }
    }

    /// <summary>Adds each sidecar that verifies against the block it names in <paramref name="blocksByRoot"/>; any other penalizes <paramref name="peer"/>, once per (root, index) of the reply.</summary>
    private void AddVerifiedSidecars(IBeaconSyncPeer peer, IReadOnlyList<DataColumnSidecar> sidecars, Dictionary<Hash256, BeaconBlock> blocksByRoot)
    {
        HashSet<(Hash256 Root, ulong Index)> rejected = [];
        foreach (DataColumnSidecar sidecar in sidecars)
        {
            if (sidecar.SignedBlockHeader?.Message is not { } header)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, "Data column sidecar has no block header");
                continue;
            }

            Hash256 sidecarBlockRoot = SszRoots.HashTreeRoot(header);
            // A repeat of a (root, index) this reply already failed is neither verified nor penalized again.
            if (rejected.Contains((sidecarBlockRoot, sidecar.Index)))
            {
                continue;
            }

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
                rejected.Add((sidecarBlockRoot, sidecar.Index));
                continue;
            }

            sidecarPool.Add(sidecarBlockRoot, header.Slot, sidecar);
        }
    }

    /// <summary>
    /// Assigns each of <paramref name="columns"/> to one of <paramref name="peers"/> that custodies it, using at most
    /// <paramref name="maxPeers"/> distinct peers and at most <paramref name="maxColumnsPerPeer"/> columns each, so one supernode
    /// cannot take the whole batch. A column no usable peer with room custodies is left out.
    /// </summary>
    /// <remarks>
    /// Deterministic: a peer with advertised custody is preferred over one known only by its <c>CUSTODY_REQUIREMENT</c>
    /// floor, then the peer with the fewest columns so far, then the earlier peer in <paramref name="peers"/>.
    /// </remarks>
    /// <returns>One entry per chosen peer, in the order the peers were first chosen.</returns>
    internal static List<(IBeaconSyncPeer Peer, ulong[] Columns)> AssignColumns(IReadOnlyList<ulong> columns, IReadOnlyList<IBeaconSyncPeer> peers, int maxPeers, int maxColumnsPerPeer = int.MaxValue)
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
                if (!custodies[i].Custodies(column) || (assigned[i] is null && chosenOrder.Count >= maxPeers) || (assigned[i]?.Count ?? 0) >= maxColumnsPerPeer)
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
        IReadOnlyList<IBeaconSyncPeer> connected = peerPool.GetBestPeers(0);
        IBeaconSyncPeer[] custodians = [.. connected.Where(p => p.Custody.CountCustodied(missing) > 0).OrderByDescending(static p => p.Custody.IsAdvertised)];
        if (custodians.Length == 0 && missing.Count > 0)
        {
            LogNoCustodian(missing, connected.Count);
        }

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
            HashSet<(Hash256 Root, ulong Index)> rejected = [];
            foreach (DataColumnSidecarGloas sidecar in responses[i].Result ?? [])
            {
                // A repeat of a (root, index) this reply already failed is neither verified nor penalized again.
                if (sidecar.BeaconBlockRoot is { } sidecarRoot && rejected.Contains((sidecarRoot, sidecar.Index)))
                {
                    continue;
                }

                // A column an earlier reply already supplied is not verified again.
                if (sidecar.BeaconBlockRoot == blockRoot && sidecarPool.TryGetGloas(blockRoot, sidecar.Index, out _))
                {
                    continue;
                }

                if (sidecar.BeaconBlockRoot != blockRoot || !IsVerifiedGloasColumnOf(sidecar, bid.Slot, commitments, requested[i]))
                {
                    asked[i].ReportFailure(PeerFailureReason.ProtocolViolation, $"Gloas data column sidecar at slot {sidecar.Slot} column {sidecar.Index} failed verification");
                    if (sidecar.BeaconBlockRoot == blockRoot && ReachesKzgVerification(sidecar, bid.Slot, commitments, requested[i]))
                    {
                        rejected.Add((blockRoot, sidecar.Index));
                    }

                    continue;
                }

                sidecarPool.AddGloas(sidecar);
            }
        }

        return MissingGloasColumns(blockRoot, custody).Length == 0;
    }

    /// <summary>
    /// Requests and verifies this node's missing sampled Gloas data column sidecars for every blob-carrying Gloas block
    /// in <paramref name="blocks"/> inside the data availability window. Never fails the batch, as <see cref="FetchColumnsForBatchAsync"/>.
    /// </summary>
    /// <remarks>
    /// The request window spans only those blocks, so it lies wholly in Gloas epochs as the Gloas request requires, and each column is asked of
    /// one peer that custodies it as in <see cref="FetchColumnsForBatchAsync"/>. A sidecar must name a Gloas block of this batch and that block's slot,
    /// and pass gloas/p2p-interface.md <c>verify_data_column_sidecar</c> and <c>verify_data_column_sidecar_kzg_proofs</c> against the block's bid;
    /// a column already pooled is not verified again. As in <see cref="FetchColumnsForBatchAsync"/>, a peer is asked for at most an even share of the missing columns,
    /// one that fails or leaves a requested column unserved is not asked again for the batch, and up to <see cref="MaxColumnRounds"/> rounds request only what is still
    /// missing, within <see cref="ColumnBatchBudget"/>. A failed Gloas reply delivers no sidecars to keep.
    /// </remarks>
    private async Task FetchGloasColumnsForBatchAsync(IReadOnlyList<ForkedSignedBeaconBlock> blocks, Hash256[] roots, CancellationToken token)
    {
        ulong windowStartEpoch = DataAvailabilityStartEpoch();
        Dictionary<Hash256, (ulong Slot, ExecutionPayloadBid Bid)> bidsByRoot = [];
        ulong startSlot = ulong.MaxValue;
        ulong endSlot = 0;
        ulong lastBlobSlot = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not ForkedSignedBeaconBlock.OfGloas { Block.Message: { } message }
                || message.Body?.SignedExecutionPayloadBid?.Message is not { BlobKzgCommitments.Length: > 0 } bid
                || !IsInDataAvailabilityWindow(message.Slot, windowStartEpoch))
            {
                continue;
            }

            startSlot = Math.Min(startSlot, message.Slot);
            endSlot = Math.Max(endSlot, message.Slot);
            lastBlobSlot = Math.Max(lastBlobSlot, message.Slot);
            bidsByRoot[roots[i]] = (message.Slot, bid);
        }

        if (bidsByRoot.Count == 0 || _custodySource.Current is not { } custody)
        {
            return;
        }

        HashSet<string> spent = [];
        long startedAt = Stopwatch.GetTimestamp();
        for (int round = 0; round < MaxColumnRounds; round++)
        {
            List<ulong> missing = MissingGloasBatchColumns(custody, bidsByRoot, out ulong firstMissingSlot, out ulong lastMissingSlot);
            if (missing.Count == 0 || (round > 0 && Stopwatch.GetElapsedTime(startedAt) >= ColumnBatchBudget))
            {
                return;
            }

            // Later rounds ask only for the slots still lacking a column, not the whole batch again.
            ulong roundStart = round == 0 ? startSlot : firstMissingSlot;
            ulong roundEnd = round == 0 ? endSlot : lastMissingSlot;
            List<(IBeaconSyncPeer Peer, ulong[] Columns)> requests = AssignBatchColumns(missing, roundStart, round == 0 ? lastBlobSlot : lastMissingSlot, spent, boundPerPeer: true);
            if (requests.Count == 0)
            {
                return;
            }

            Task<IReadOnlyList<DataColumnSidecarGloas>?>[] responses = new Task<IReadOnlyList<DataColumnSidecarGloas>?>[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                // A peer serves from its earliest slot; a request below it may be answered ResourceUnavailable (fulu/p2p-interface.md).
                ulong from = Math.Max(roundStart, requests[i].Peer.EarliestAvailableSlot);
                responses[i] = RequestGloasColumnsByRangeAsync(requests[i].Peer, from, roundEnd - from + 1, requests[i].Columns, token);
            }

            await Task.WhenAll(responses);
            for (int i = 0; i < requests.Count; i++)
            {
                if (responses[i].Result is { } sidecars)
                {
                    AddVerifiedGloasSidecars(requests[i].Peer, sidecars, bidsByRoot, requests[i].Columns);
                }

                // A peer that failed, or left a column it was asked for unserved, is not asked again for this batch.
                if (responses[i].Result is null || requests[i].Columns.Any(column => !bidsByRoot.Keys.All(root => sidecarPool.TryGetGloas(root, column, out _))))
                {
                    spent.Add(requests[i].Peer.Id);
                }
            }
        }
    }

    /// <returns>The sampled columns some blob-carrying Gloas block of the batch lacks, and the first and last slot of a block lacking one.</returns>
    private List<ulong> MissingGloasBatchColumns(NodeColumnCustody custody, Dictionary<Hash256, (ulong Slot, ExecutionPayloadBid Bid)> bidsByRoot, out ulong firstSlot, out ulong lastSlot)
    {
        firstSlot = ulong.MaxValue;
        lastSlot = 0;
        List<ulong> missing = [];
        foreach (ulong column in custody.SampledColumns)
        {
            bool lacking = false;
            foreach ((Hash256 root, (ulong slot, _)) in bidsByRoot)
            {
                if (!sidecarPool.TryGetGloas(root, column, out _))
                {
                    lacking = true;
                    firstSlot = Math.Min(firstSlot, slot);
                    lastSlot = Math.Max(lastSlot, slot);
                }
            }

            if (lacking)
            {
                missing.Add(column);
            }
        }

        return missing;
    }

    /// <returns>The peer's sidecars, or <c>null</c> when the request failed and the peer was penalized.</returns>
    private static async Task<IReadOnlyList<DataColumnSidecarGloas>?> RequestGloasColumnsByRangeAsync(IBeaconSyncPeer peer, ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
    {
        try
        {
            return await peer.RequestGloasDataColumnSidecarsByRangeAsync(startSlot, count, columns, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Gloas data-column-sidecars-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return null;
        }
    }

    private void AddVerifiedGloasSidecars(IBeaconSyncPeer peer, IReadOnlyList<DataColumnSidecarGloas> sidecars, Dictionary<Hash256, (ulong Slot, ExecutionPayloadBid Bid)> bidsByRoot, ulong[] requestedColumns)
    {
        HashSet<(Hash256 Root, ulong Index)> rejected = [];
        foreach (DataColumnSidecarGloas sidecar in sidecars)
        {
            // A repeat of a (root, index) this reply already failed is neither verified nor penalized again.
            if (sidecar.BeaconBlockRoot is { } rejectedRoot && rejected.Contains((rejectedRoot, sidecar.Index)))
            {
                continue;
            }

            if (sidecar.BeaconBlockRoot is { } root && bidsByRoot.TryGetValue(root, out (ulong Slot, ExecutionPayloadBid Bid) block))
            {
                // A column an earlier reply already supplied is not verified again.
                if (sidecarPool.TryGetGloas(root, sidecar.Index, out _))
                {
                    continue;
                }

                if (IsVerifiedGloasColumnOf(sidecar, block.Slot, block.Bid.BlobKzgCommitments!, requestedColumns))
                {
                    sidecarPool.AddGloas(sidecar);
                    continue;
                }
            }

            peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Gloas data column sidecar at slot {sidecar.Slot} column {sidecar.Index} failed verification");
            // Only a sidecar that reached KZG verification shadows later copies; a cheap rejection must not hide a valid copy.
            if (sidecar.BeaconBlockRoot is { } failedRoot && bidsByRoot.TryGetValue(failedRoot, out (ulong Slot, ExecutionPayloadBid Bid) failedBlock) && ReachesKzgVerification(sidecar, failedBlock.Slot, failedBlock.Bid.BlobKzgCommitments!, requestedColumns))
            {
                rejected.Add((failedRoot, sidecar.Index));
            }
        }
    }

    /// <remarks>KZG does not bind <c>sidecar.slot</c>, so a sidecar naming another slot would still verify against the right bid.</remarks>
    private static bool IsVerifiedGloasColumnOf(DataColumnSidecarGloas sidecar, ulong blockSlot, SszKzgCommitment[] commitments, ulong[] requestedColumns) =>
        NamesRequestedColumn(sidecar, blockSlot, requestedColumns)
        && DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments);

    private static bool ReachesKzgVerification(DataColumnSidecarGloas sidecar, ulong blockSlot, SszKzgCommitment[] commitments, ulong[] requestedColumns) =>
        NamesRequestedColumn(sidecar, blockSlot, requestedColumns)
        && DataColumnSidecarVerifier.VerifyStructure(sidecar, commitments);

    private static bool NamesRequestedColumn(DataColumnSidecarGloas sidecar, ulong blockSlot, ulong[] requestedColumns) =>
        sidecar.Slot == blockSlot && Array.IndexOf(requestedColumns, sidecar.Index) >= 0;

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
    /// custodian was asked the rotation starts over, at most once per slot of <paramref name="clock"/>. The asked set accumulates
    /// across fetches and each fetch drops from it the peers that are no longer custodians. Not thread-safe.
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
