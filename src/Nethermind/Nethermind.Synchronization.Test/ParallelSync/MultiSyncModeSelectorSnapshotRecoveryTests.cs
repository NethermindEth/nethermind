// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

[Parallelizable(ParallelScope.All)]
public class MultiSyncModeSelectorSnapshotRecoveryTests
{
    /// <summary>
    /// If <see cref="ISyncProgressResolver"/> reports a snapshot that stays invalid even after
    /// <see cref="ISyncProgressResolver.RecalculateProgressPointers"/> (e.g. a corrupted/stale block-tree
    /// record - see the header-number-mismatch case in BlockTree.FindHeader), <see cref="MultiSyncModeSelector.Update"/>
    /// must not let that failure skip <c>UpdateSyncModes</c>. Doing so would leave <c>Current</c> frozen at
    /// whatever it was, so the exact same failure repeats every tick forever with no chance for the pointers to
    /// recover - this reproduces the "Cannot recalculate snapshot progress" infinite loop.
    /// </summary>
    [Test]
    public void Update_does_not_wedge_sync_mode_when_snapshot_stays_invalid_after_recalculation()
    {
        ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
        // Processed > Block is one of the IsSnapshotInvalid conditions, and it never clears - simulating a
        // corrupted record that recalculation cannot fix.
        syncProgressResolver.FindBestProcessedBlock().Returns(100UL);
        syncProgressResolver.FindBestFullBlock().Returns(0UL);
        syncProgressResolver.FindBestHeader().Returns(0UL);
        syncProgressResolver.FindBestFullState().Returns(0UL);
        syncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
        syncProgressResolver.SyncPivot.Returns((0UL, Keccak.Zero));

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.HeadNumber.Returns(200UL);
        syncPeer.TotalDifficulty.Returns((UInt256?)null);
        syncPeer.HeadHash.Returns(TestItem.KeccakA);
        syncPeerPool.InitializedPeers.Returns(new List<PeerInfo> { new(syncPeer) });

        ISyncConfig syncConfig = new SyncConfig { FastSync = false, SynchronizationEnabled = true };
        IBetterPeerStrategy betterPeerStrategy = new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance);

        MultiSyncModeSelector selector = new(
            syncProgressResolver,
            syncPeerPool,
            syncConfig,
            No.BeaconSync,
            betterPeerStrategy,
            LimboLogs.Instance);

        Assert.DoesNotThrow(() => selector.Update());

        // Current must have actually been updated (not left at its pre-Update() default) - proving
        // UpdateSyncModes ran instead of being skipped by a propagated exception.
        Assert.That(selector.Current, Is.EqualTo(SyncMode.Disconnected));

        // A second tick reproduces the exact same persistently-invalid snapshot; Update must keep
        // completing (and keep calling UpdateSyncModes) rather than wedging after the first failure.
        Assert.DoesNotThrow(() => selector.Update());
        Assert.That(selector.Current, Is.EqualTo(SyncMode.Disconnected));

        syncProgressResolver.Received(2).RecalculateProgressPointers();
    }
}
