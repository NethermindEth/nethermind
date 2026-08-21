// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class SnapP2PCapabilityResolverTests
{
    private const ulong Pivot = 1000;

    private static readonly Capability Snap1 = new(Protocol.Snap, SnapVersions.Snap1);
    private static readonly Capability Snap2 = new(Protocol.Snap, SnapVersions.Snap2);
    private static readonly BlockHeader PivotHeader = Build.A.BlockHeader.TestObject;

    private static SnapP2PCapabilityResolver CreateResolver(
        out ISyncModeSelector syncModeSelector,
        out IStateSyncPivot stateSyncPivot,
        bool snapServing = false,
        bool snapSync = false,
        bool balEnabled = true,
        bool balHealingAvailable = true,
        BlockHeader? firstPivotHeader = null,
        params ulong[] bestFullStates)
    {
        ISyncConfig syncConfig = new SyncConfig { SnapServingEnabled = snapServing, SnapSync = snapSync };
        syncModeSelector = Substitute.For<ISyncModeSelector>();
        ISyncProgressResolver progressResolver = Substitute.For<ISyncProgressResolver>();
        progressResolver.SyncPivot.Returns((Pivot, Keccak.Zero));
        if (bestFullStates.Length > 0)
        {
            progressResolver.FindBestFullState().Returns(bestFullStates[0], bestFullStates[1..]);
        }

        stateSyncPivot = Substitute.For<IStateSyncPivot>();
        stateSyncPivot.FirstPivotHeader.Returns(firstPivotHeader);
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { IsEip7928Enabled = balEnabled });
        IBalHealing balHealing = balHealingAvailable ? Substitute.For<IBalHealing>() : NoopBalHealing.Instance;
        return new SnapP2PCapabilityResolver(syncConfig, syncModeSelector, progressResolver, stateSyncPivot, specProvider, balHealing, LimboLogs.Instance);
    }

    private static void PublishSyncProgress(ISyncModeSelector syncModeSelector) =>
        syncModeSelector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.None, SyncMode.None));

    private static void PublishFirstPivot(IStateSyncPivot stateSyncPivot, BlockHeader header) =>
        stateSyncPivot.FirstPivotSet += Raise.EventWith(new BlockHeaderEventArgs(header));

    private static HashSet<Capability> Resolve(SnapP2PCapabilityResolver resolver)
    {
        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);
        return capabilities;
    }

    [TestCase(true, false, true, TestName = "Serving advertises snap regardless of sync")]
    [TestCase(false, true, true, TestName = "Snap-syncing advertises snap")]
    [TestCase(false, false, false, TestName = "Neither serving nor snap-syncing")]
    public void Resolve_advertises_snap1(bool snapServing, bool snapSync, bool expected)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(out ISyncModeSelector syncModeSelector, out _, snapServing, snapSync);
        PublishSyncProgress(syncModeSelector);

        Assert.That(Resolve(resolver).Contains(Snap1), Is.EqualTo(expected));
    }

    [TestCase(Pivot, true, true, TestName = "State downloaded and BALs enabled")]
    [TestCase(Pivot, false, false, TestName = "BALs not enabled")]
    [TestCase(Pivot - 1, true, false, TestName = "Own state sync unfinished")]
    public void Resolve_serving_advertises_snap2_only_once_it_no_longer_needs_trie_nodes(ulong bestFullState, bool balEnabled, bool expected)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, out _, snapServing: true, balEnabled: balEnabled, bestFullStates: bestFullState);
        PublishSyncProgress(syncModeSelector);

        Assert.That(Resolve(resolver).Contains(Snap2), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_uses_the_initial_state_sync_progress_without_waiting_for_a_mode_change()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out _, out _, snapServing: true, bestFullStates: Pivot);

        Assert.That(Resolve(resolver).Contains(Snap2), Is.True);
    }

    [TestCase(true, true, true, true, TestName = "Syncing with BAL healing, BAL chain and known pivot advertises snap/2")]
    [TestCase(false, true, true, false, TestName = "Syncing without BAL healing withholds snap/2")]
    [TestCase(true, false, true, false, TestName = "Syncing on a non-BAL chain withholds snap/2")]
    [TestCase(true, true, false, false, TestName = "Syncing without a pivot withholds snap/2")]
    public void Resolve_syncing_advertises_snap2_only_when_BAL_healing_can_replace_trie_nodes(
        bool balHealingAvailable,
        bool balEnabled,
        bool hasPivot,
        bool expected)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out _,
            out _,
            snapSync: true,
            balEnabled: balEnabled,
            balHealingAvailable: balHealingAvailable,
            firstPivotHeader: hasPivot ? PivotHeader : null);

        Assert.That(Resolve(resolver).Contains(Snap2), Is.EqualTo(expected));
    }

    [Test]
    public void Advertises_snap2_and_raises_Changed_once_state_download_completes()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, out _, snapServing: true, bestFullStates: [Pivot - 1, Pivot - 1, Pivot, Pivot]);

        int fired = 0;
        resolver.Changed += () => fired++;

        PublishSyncProgress(syncModeSelector);
        Assert.Multiple(() =>
        {
            Assert.That(fired, Is.Zero);
            Assert.That(Resolve(resolver).Contains(Snap2), Is.False);
        });

        PublishSyncProgress(syncModeSelector);
        PublishSyncProgress(syncModeSelector);
        Assert.Multiple(() =>
        {
            Assert.That(fired, Is.EqualTo(1));
            Assert.That(Resolve(resolver).Contains(Snap2), Is.True);
        });
    }

    [Test]
    public void Advertises_snap2_and_raises_Changed_once_BAL_healing_becomes_available()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(out _, out IStateSyncPivot stateSyncPivot, snapSync: true);

        int fired = 0;
        resolver.Changed += () => fired++;

        Assert.That(Resolve(resolver).Contains(Snap2), Is.False);

        PublishFirstPivot(stateSyncPivot, PivotHeader);
        PublishFirstPivot(stateSyncPivot, PivotHeader);

        Assert.Multiple(() =>
        {
            Assert.That(fired, Is.EqualTo(1));
            Assert.That(Resolve(resolver).Contains(Snap2), Is.True);
        });
    }
}
