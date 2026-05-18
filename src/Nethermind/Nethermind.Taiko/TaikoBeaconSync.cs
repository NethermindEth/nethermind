// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko-specific decorator for <see cref="IBeaconSyncStrategy"/> that prevents the
/// post-pivot <c>BeaconHeaders</c> deadlock on second-and-onwards beacon-sync triggers.
/// </summary>
/// <remarks>
/// <para>
/// The default <see cref="BeaconSync.IsBeaconSyncHeadersFinished"/> computes <c>chainMerged</c>
/// against <see cref="IBlockTree.BestSuggestedHeader"/>. On Taiko that pointer never
/// advances past genesis because P2P-downloaded blocks fail
/// <c>BlockTree.BestSuggestedImprovementRequirementsSatisfied</c>: Taiko's <c>TotalDifficulty</c>
/// is always zero, and Taiko blocks pulled via <c>eth/68 GetBlockBodies</c> have
/// <see cref="BlockHeader.IsPostMerge"/> <c>= false</c> (the field is only set by
/// <c>NewPayloadHandler</c> / locally produced blocks) and a non-zero <see cref="BlockHeader.Difficulty"/>
/// (repurposed as per-block ZK gas), so <see cref="Core.BlockExtensions.IsPoS(BlockHeader)"/>
/// returns false. Result: <c>BestSuggestedHeader.Number == 0</c> indefinitely.
/// </para>
/// <para>
/// Symptom: on the first cold-start sync the headers feed exits via the
/// <c>LIBH &lt;= PivotDestinationNumber</c> branch (LIBH walks all the way down to <c>1</c>),
/// so the broken pointer is harmless. On any subsequent trigger LIBH stops at
/// <c>Head + 1</c> (the headers feed's slice logic truncates at the merge point) and
/// <c>PivotDestinationNumber</c> is <c>Head − Reorganization.MaxDepth + 1</c>; the
/// <c>LIBH &lt;= destination</c> branch fails, and the <c>chainMerged</c> branch fails because
/// <c>BestSuggestedHeader.Number == 0</c>. <see cref="BeaconSync.IsBeaconSyncHeadersFinished"/>
/// returns false forever, the selector stays in <see cref="Synchronization.ParallelSync.SyncMode.BeaconHeaders"/>,
/// and <see cref="Synchronization.Blocks.FullSyncFeed"/> never engages to forward-fill bodies.
/// </para>
/// <para>
/// This decorator re-runs the <c>chainMerged</c> check using
/// <c>Math.Max(BestSuggestedHeader.Number, Head.Number)</c>. <see cref="IBlockTree.Head"/>
/// is updated by the standard block-processing path even when <c>BestSuggestedHeader</c>
/// is not, so it reflects the real chain tip. All other <see cref="IBeaconSyncStrategy"/>
/// members delegate unchanged to the inner instance.
/// </para>
/// <para>
/// Companion to <see cref="TaikoSyncProgressResolver"/>, which applies the same widening
/// for <see cref="Synchronization.ParallelSync.ISyncProgressResolver.FindBestHeader"/>;
/// <see cref="BeaconSync"/> reads <see cref="IBlockTree"/> directly so that decorator does
/// not reach this code path.
/// </para>
/// </remarks>
public sealed class TaikoBeaconSync(
    IBeaconSyncStrategy inner,
    IBlockTree blockTree,
    IBeaconPivot beaconPivot,
    ISyncConfig syncConfig,
    ILogManager logManager) : IBeaconSyncStrategy
{
    private readonly ILogger _logger = logManager.GetClassLogger<TaikoBeaconSync>();

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <c>false</c> when the inner strategy returns <c>false</c>, or when the inner
    /// strategy returns <c>true</c> but the <c>chainMerged</c> / <c>LIBH-at-destination</c>
    /// check evaluated against <see cref="IBlockTree.Head"/> shows that beacon-headers
    /// download has actually finished. Otherwise delegates to the inner result.
    /// </remarks>
    public bool ShouldBeInBeaconHeaders()
    {
        if (!inner.ShouldBeInBeaconHeaders()) return false;

        BlockHeader? lowestInsertedBeaconHeader = blockTree.LowestInsertedBeaconHeader;
        if (lowestInsertedBeaconHeader is null) return true;

        long bestKnownNumber = Math.Max(
            blockTree.BestSuggestedHeader?.Number ?? 0,
            blockTree.Head?.Number ?? 0);

        bool reachedDestination = lowestInsertedBeaconHeader.Number <= beaconPivot.PivotDestinationNumber;

        bool chainMerged = !syncConfig.StrictMode
            && (lowestInsertedBeaconHeader.Number - 1) <= bestKnownNumber
            && blockTree.IsKnownBlock(lowestInsertedBeaconHeader.Number - 1, lowestInsertedBeaconHeader.ParentHash!);

        if (reachedDestination || chainMerged)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Head-widened headers-finished override fired. LIBH:{lowestInsertedBeaconHeader.Number}, " +
                    $"BestKnown:{bestKnownNumber}, Destination:{beaconPivot.PivotDestinationNumber}, " +
                    $"ReachedDestination:{reachedDestination}, ChainMerged:{chainMerged}");
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public void AllowBeaconHeaderSync() => inner.AllowBeaconHeaderSync();

    /// <inheritdoc/>
    public bool ShouldBeInBeaconModeControl() => inner.ShouldBeInBeaconModeControl();

    /// <inheritdoc/>
    public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => inner.IsBeaconSyncFinished(blockHeader);

    /// <inheritdoc/>
    public bool MergeTransitionFinished => inner.MergeTransitionFinished;

    /// <inheritdoc/>
    public long? GetTargetBlockHeight() => inner.GetTargetBlockHeight();

    /// <inheritdoc/>
    public Hash256? GetFinalizedHash() => inner.GetFinalizedHash();

    /// <inheritdoc/>
    public Hash256? GetHeadBlockHash() => inner.GetHeadBlockHash();
}
