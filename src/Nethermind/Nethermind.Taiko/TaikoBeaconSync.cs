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
/// Taiko-specific decorator for <see cref="IBeaconSyncStrategy"/> that widens the
/// <c>chainMerged</c> check in <see cref="BeaconSync.IsBeaconSyncHeadersFinished"/>
/// to also consider <see cref="IBlockTree.Head"/>.
/// </summary>
/// <remarks>
/// On Taiko, <see cref="IBlockTree.BestSuggestedHeader"/> stays at genesis because P2P-downloaded
/// blocks fail <c>BlockTree.BestSuggestedImprovementRequirementsSatisfied</c> (Taiko's
/// <c>TotalDifficulty</c> is 0 and <see cref="Core.BlockExtensions.IsPoS(BlockHeader)"/> returns
/// false for them). The default chain-merged check then locks the selector into
/// <see cref="Synchronization.ParallelSync.SyncMode.BeaconHeaders"/> on every second-and-onwards
/// beacon-sync trigger. Companion to <see cref="TaikoSyncProgressResolver"/>, which applies the
/// same widening for <see cref="Synchronization.ParallelSync.ISyncProgressResolver.FindBestHeader"/>.
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
    public bool ShouldBeInBeaconHeaders()
    {
        if (!inner.ShouldBeInBeaconHeaders()) return false;

        BlockHeader? lowestInsertedBeaconHeader = blockTree.LowestInsertedBeaconHeader;
        if (lowestInsertedBeaconHeader is null) return true;

        long suggested = blockTree.BestSuggestedHeader?.Number ?? long.MinValue;
        long head = blockTree.Head?.Number ?? long.MinValue;
        long bestKnownNumber = Math.Max(suggested, head);
        // Mirror the original `?? long.MaxValue` sentinel: with no header at all the
        // numeric comparison should pass trivially so chainMerged is gated only by IsKnownBlock.
        if (bestKnownNumber == long.MinValue) bestKnownNumber = long.MaxValue;

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
