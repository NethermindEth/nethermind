// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

public class BeaconBootnodesTests
{
    [Test]
    public void ForChainId_returns_mainnet_bootnodes_for_the_mainnet_chain_id() =>
        Assert.That(BeaconBootnodes.ForChainId(BlockchainIds.Mainnet), Is.SameAs(MainnetBootnodes.Enrs));

    [Test]
    public void ForChainId_returns_hoodi_bootnodes_for_the_hoodi_chain_id() =>
        Assert.That(BeaconBootnodes.ForChainId(BlockchainIds.Hoodi), Is.SameAs(HoodiBootnodes.Enrs));

    [Test]
    public void ForChainId_fails_loudly_for_an_unsupported_chain_id()
    {
        // A synthetic id, not a real network: naming one here makes the test fail the day we model it.
        const ulong unmodelledChainId = 0xDEADBEEF;

        UnsupportedBeaconNetworkException ex = Assert.Throws<UnsupportedBeaconNetworkException>(
            () => BeaconBootnodes.ForChainId(unmodelledChainId))!;

        Assert.Multiple(() =>
        {
            Assert.That(ex.ChainId, Is.EqualTo(unmodelledChainId));
            Assert.That(ex.Message, Does.Contain(unmodelledChainId.ToString()));
        });
    }
}
