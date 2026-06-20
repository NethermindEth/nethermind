// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class MergeP2PCapabilityResolverTests
{
    [TestCase(true, true)]
    [TestCase(false, false)]
    public void Resolve_advertises_post_merge_eth_capabilities_only_after_transition(bool transitionFinished, bool expected)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(transitionFinished);
        using MergeP2PCapabilityResolver resolver = new(poSSwitcher);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 69)), Is.EqualTo(expected));
        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 70)), Is.EqualTo(expected));
        Assert.That(capabilities.Contains(new Capability(Protocol.Eth, 71)), Is.EqualTo(expected));
    }

    [Test]
    public void Raises_Changed_when_terminal_block_reached()
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        using MergeP2PCapabilityResolver resolver = new(poSSwitcher);

        bool changed = false;
        resolver.Changed += () => changed = true;

        poSSwitcher.TerminalBlockReached += Raise.Event();

        Assert.That(changed, Is.True);
    }
}
