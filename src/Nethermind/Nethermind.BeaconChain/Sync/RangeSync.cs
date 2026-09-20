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
/// column sidecars for the same slot window from the same peer before yielding, so the gossip-fed
/// <see cref="DataAvailability.CustodySamplingAvailability"/> gate has something to check against for
/// a range-synced block. A column-fetch failure penalizes the peer but does not fail the batch: a
/// block whose columns are still missing simply fails the importer's availability gate and is
/// retried next round, since the sync tip only advances past a successfully imported block.
/// </para>
/// </remarks>
public class RangeSync(IBeaconSyncPeerPool peerPool, ILogManager logManager, DataColumnSidecarPool sidecarPool, BeaconChainSpec spec, BeaconDiscovery? discovery = null)
{
    /// <summary>
    /// Half an epoch per request. Larger batches trip peers' response rate limits, time out, and
    /// burn the peer-failure budget — with few connected mainnet peers that spirals into starvation.
    /// </summary>
    public const ulong DefaultBatchSize = 16;

    private const int FailuresBeforeBatchShrink = 3;
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
            await FetchColumnsForBatchAsync(peer, batch.Value.Blocks, token);
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
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // Includes per-request timeouts, which cancel the request without cancelling the sync.
            peer.ReportFailure(ClassifyRequestFailure(e), $"Blocks-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
            return null;
        }

        // Slot bounds and ordering are already enforced at the protocol layer; verify parent linkage here.
        Hash256 expectedParent = parentRoot;
        foreach (SignedBeaconBlock block in batch)
        {
            if (block.Message!.ParentRoot != expectedParent)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Block at slot {block.Message.Slot} has parent {block.Message.ParentRoot}, expected {expectedParent}");
                return null;
            }

            expectedParent = SszRoots.HashTreeRoot(block.Message);
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
    private async Task FetchColumnsForBatchAsync(IBeaconSyncPeer peer, IReadOnlyList<SignedBeaconBlock> blocks, CancellationToken token)
    {
        Dictionary<Hash256, BeaconBlock> blobBlocksByRoot = [];
        foreach (SignedBeaconBlock block in blocks)
        {
            SszKzgCommitment[]? commitments = block.Message!.Body?.BlobKzgCommitments;
            if (commitments is { Length: > 0 })
            {
                blobBlocksByRoot[SszRoots.HashTreeRoot(block.Message)] = block.Message;
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

        ulong startSlot = blocks[0].Message!.Slot;
        ulong count = blocks[^1].Message!.Slot - startSlot + 1;

        IReadOnlyList<DataColumnSidecar> sidecars;
        try
        {
            sidecars = await peer.RequestDataColumnSidecarsByRangeAsync(startSlot, count, sampledColumns, token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ReportFailure(ClassifyRequestFailure(e), $"Data-column-sidecars-by-range [{startSlot}, {startSlot + count}) failed: {e.Message}");
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
    /// A dead session must be reported as <see cref="PeerFailureReason.SessionClosed"/>, or the peer manager keeps
    /// the zombie on a failure budget it can never work off; libp2p only surfaces that state through the message text.
    /// </summary>
    private static PeerFailureReason ClassifyRequestFailure(Exception e) =>
        e.Message.Contains("Channel closed", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            ? PeerFailureReason.SessionClosed
            : PeerFailureReason.RequestFailed;
}
