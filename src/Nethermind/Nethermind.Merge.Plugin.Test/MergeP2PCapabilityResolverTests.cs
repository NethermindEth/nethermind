// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class MergeP2PCapabilityResolverTests
{
    [TestCase(false, false, false, false, TestName = "Pre-merge: not advertised")]
    [TestCase(true, false, false, true, TestName = "Transition finished (post-merge restart): advertised")]
    [TestCase(false, true, false, true, TestName = "Live merge: terminal block reached before transition finished: advertised")]
    [TestCase(true, true, false, true, TestName = "Both: advertised")]
    [TestCase(true, false, true, true, TestName = "Blob support: eth72 advertised")]
    public void Resolve_advertises_post_merge_eth_capabilities_once_post_merge(
        bool transitionFinished,
        bool hasReachedTerminalBlock,
        bool blobsEnabled,
        bool expected)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(transitionFinished);
        poSSwitcher.HasEverReachedTerminalBlock().Returns(hasReachedTerminalBlock);
        TxPoolConfig txPoolConfig = new() { BlobsSupport = blobsEnabled ? BlobsSupportMode.StorageWithReorgs : BlobsSupportMode.Disabled };
        using MergeP2PCapabilityResolver resolver = new(poSSwitcher, txPoolConfig);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 69)), Is.EqualTo(expected));
        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 70)), Is.EqualTo(expected));
        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 71)), Is.EqualTo(expected));
        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 72)), Is.EqualTo(expected && blobsEnabled));
    }

    [Test]
    public void Raises_Changed_when_terminal_block_reached()
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        using MergeP2PCapabilityResolver resolver = new(poSSwitcher, new TxPoolConfig());

        bool changed = false;
        resolver.Changed += () => changed = true;

        poSSwitcher.TerminalBlockReached += Raise.Event();

        Assert.That(changed, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Resolve_advertises_eth72_only_after_eip7594(bool eip7594Enabled)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(true);
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsEip7594Enabled.Returns(eip7594Enabled);
        specProvider.GetCurrentHeadSpec().Returns(releaseSpec);
        using MergeP2PCapabilityResolver resolver = new(
            poSSwitcher,
            new TxPoolConfig { BlobsSupport = BlobsSupportMode.StorageWithReorgs },
            specProvider);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 72)), Is.EqualTo(eip7594Enabled));
    }

    [Test]
    public void Raises_Changed_when_eip7594_activation_changes()
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(true);
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        IChainHeadInfoProvider chainHeadInfoProvider = Substitute.For<IChainHeadInfoProvider>();
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        bool eip7594Enabled = false;
        releaseSpec.IsEip7594Enabled.Returns(_ => eip7594Enabled);
        specProvider.GetCurrentHeadSpec().Returns(releaseSpec);
        chainHeadInfoProvider.SpecProvider.Returns(specProvider);
        using MergeP2PCapabilityResolver resolver = new(
            poSSwitcher,
            new TxPoolConfig { BlobsSupport = BlobsSupportMode.StorageWithReorgs },
            specProvider,
            chainHeadInfoProvider);
        int changes = 0;
        resolver.Changed += () => changes++;

        chainHeadInfoProvider.HeadChanged += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));
        eip7594Enabled = true;
        chainHeadInfoProvider.HeadChanged += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));
        chainHeadInfoProvider.HeadChanged += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(capabilities, Does.Contain(new Capability(Protocol.Eth, 72)));
        }
    }
}
