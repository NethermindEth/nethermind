// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs;
using Nethermind.Stats.Model;
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

    private static SnapP2PCapabilityResolver CreateResolver(
        out ISyncModeSelector syncModeSelector,
        bool snapServing = false, bool snapSync = false, bool balEnabled = true,
        bool canHeal = true, bool pivotHeaderKnown = true, params ulong[] bestFullStates)
    {
        ISyncConfig syncConfig = new SyncConfig { SnapServingEnabled = snapServing, SnapSync = snapSync };
        syncModeSelector = Substitute.For<ISyncModeSelector>();
        ISyncProgressResolver progressResolver = Substitute.For<ISyncProgressResolver>();
        progressResolver.SyncPivot.Returns((Pivot, Keccak.Zero));
        if (bestFullStates.Length > 0)
        {
            progressResolver.FindBestFullState().Returns(bestFullStates[0], bestFullStates[1..]);
        }

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Keccak.Zero, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(pivotHeaderKnown ? Build.A.BlockHeader.WithNumber(Pivot).TestObject : null);

        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { IsEip7928Enabled = balEnabled });
        IBalHealing balHealing = Substitute.For<IBalHealing>();
        balHealing.CanHeal.Returns(canHeal);
        return new SnapP2PCapabilityResolver(
            syncConfig, syncModeSelector, progressResolver, specProvider, blockTree, balHealing, LimboLogs.Instance);
    }

    private static void PublishSyncProgress(ISyncModeSelector syncModeSelector) =>
        syncModeSelector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.None, SyncMode.None));

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
        using SnapP2PCapabilityResolver resolver = CreateResolver(out ISyncModeSelector syncModeSelector, snapServing, snapSync);
        PublishSyncProgress(syncModeSelector);

        Assert.That(Resolve(resolver).Contains(Snap1), Is.EqualTo(expected));
    }

    [TestCase(Pivot, true, true, TestName = "State downloaded and BALs enabled")]
    [TestCase(Pivot, false, false, TestName = "BALs not enabled")]
    [TestCase(Pivot - 1, true, false, TestName = "Own state sync unfinished")]
    public void Resolve_serving_advertises_snap2_only_once_it_no_longer_needs_trie_nodes(ulong bestFullState, bool balEnabled, bool expected)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, snapServing: true, balEnabled: balEnabled, bestFullStates: bestFullState);
        PublishSyncProgress(syncModeSelector);

        Assert.That(Resolve(resolver).Contains(Snap2), Is.EqualTo(expected));
    }

    [TestCase(true, true, true, TestName = "BAL heal available")]
    [TestCase(false, true, false, TestName = "BALs not enabled at the pivot")]
    [TestCase(true, false, false, TestName = "State backend cannot BAL heal")]
    public void Resolve_syncing_advertises_snap2_only_with_a_bal_heal_substitute(bool balEnabled, bool canHeal, bool expected)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, snapSync: true, balEnabled: balEnabled, canHeal: canHeal);
        PublishSyncProgress(syncModeSelector);

        Assert.That(Resolve(resolver).Contains(Snap2), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_syncing_still_advertises_snap1_when_the_backend_cannot_bal_heal()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, snapSync: true, canHeal: false);
        PublishSyncProgress(syncModeSelector);

        HashSet<Capability> capabilities = Resolve(resolver);
        Assert.Multiple(() =>
        {
            Assert.That(capabilities.Contains(Snap1), Is.True);
            Assert.That(capabilities.Contains(Snap2), Is.False);
        });
    }

    [Test]
    public void Resolve_syncing_withholds_snap2_until_the_pivot_header_is_known()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, snapSync: true, pivotHeaderKnown: false);
        PublishSyncProgress(syncModeSelector);

        Assert.That(Resolve(resolver).Contains(Snap2), Is.False);
    }

    [Test]
    public void Advertises_snap2_and_raises_Changed_once_state_download_completes()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(
            out ISyncModeSelector syncModeSelector, snapServing: true, bestFullStates: [Pivot - 1, Pivot, Pivot]);

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
}
