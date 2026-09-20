// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
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
    public void ForChainId_returns_sepolia_bootnodes_for_the_sepolia_chain_id() =>
        Assert.That(BeaconBootnodes.ForChainId(BlockchainIds.Sepolia), Is.SameAs(SepoliaBootnodes.Enrs));

    /// <summary>
    /// This selector and <see cref="BeaconChainSpec.ForChainId"/> are two switches over the same
    /// key, and Sepolia was added to one and not the other. A network the spec models but that has
    /// no bootnodes cannot start discovery, and nothing else in the driver would say so.
    /// </summary>
    [Test]
    public void Every_network_the_spec_models_also_has_bootnodes()
    {
        // Enumerated from BlockchainIds rather than a list held here, which would just be a third
        // place to forget the next network.
        ulong[] modelled = [.. typeof(BlockchainIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(ulong))
            .Select(f => (ulong)f.GetRawConstantValue()!)
            .Distinct()
            .Where(HasSpec)];

        Assert.That(modelled, Is.Not.Empty, "the enumeration itself broke; it is asserting nothing");
        Assert.Multiple(() =>
        {
            foreach (ulong chainId in modelled)
            {
                Assert.That(() => BeaconBootnodes.ForChainId(chainId), Throws.Nothing,
                    $"chain id {chainId} has a beacon spec but no bootnodes, so discovery cannot start");
            }
        });
    }

    private static bool HasSpec(ulong chainId)
    {
        try
        {
            _ = BeaconChainSpec.ForChainId(chainId);
            return true;
        }
        catch (UnsupportedBeaconNetworkException)
        {
            return false;
        }
    }

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
