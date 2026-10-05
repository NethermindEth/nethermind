// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Core;

namespace Nethermind.BeaconChain.Test.Spec;

public class BeaconChainSpecTests
{
    [Test]
    public void A_configuration_copy_keeps_separate_identity()
    {
        BeaconChainSpec original = BeaconChainSpec.Mainnet;
        BeaconChainSpec copy = original with { };

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(copy == original, Is.False);
        Assert.That(copy.Equals((object)original), Is.False);
        Assert.That(copy.GetHashCode(), Is.EqualTo(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(copy)));
        Assert.That(copy.ToString(), Is.EqualTo(typeof(BeaconChainSpec).FullName));
    }

    [Test]
    public void ForChainId_returns_mainnet_for_the_mainnet_chain_id() =>
        Assert.That(BeaconChainSpec.ForChainId(BlockchainIds.Mainnet), Is.SameAs(BeaconChainSpec.Mainnet));

    [Test]
    public void ForChainId_returns_hoodi_for_the_hoodi_chain_id() =>
        Assert.That(BeaconChainSpec.ForChainId(BlockchainIds.Hoodi), Is.SameAs(BeaconChainSpec.Hoodi));

    [Test]
    public void ForChainId_returns_sepolia_for_the_sepolia_chain_id() =>
        Assert.That(BeaconChainSpec.ForChainId(BlockchainIds.Sepolia), Is.SameAs(BeaconChainSpec.Sepolia));

    [TestCase(BlockchainIds.Holesky)]
    [TestCase(0ul)]
    public void ForChainId_fails_loudly_for_an_unsupported_chain_id(ulong chainId)
    {
        UnsupportedBeaconNetworkException ex = Assert.Throws<UnsupportedBeaconNetworkException>(
            () => BeaconChainSpec.ForChainId(chainId))!;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ex.ChainId, Is.EqualTo(chainId));
        Assert.That(ex.Message, Does.Contain(chainId.ToString()),
            "the offending chain id must be visible in the failure so an operator can diagnose it");
    }

    // Independent sources: eth-clients/hoodi config.yaml and genesis_validators_root.txt; do not derive expectations from BeaconChainSpec.
    [Test]
    public void Hoodi_spec_matches_its_published_network_parameters() =>
        Assert.Multiple(() =>
        {
            Assert.That(BeaconChainSpec.Hoodi.ChainId, Is.EqualTo(BlockchainIds.Hoodi));
            Assert.That(BeaconChainSpec.Hoodi.GenesisValidatorsRoot.ToString(),
                Is.EqualTo("0x212f13fc4df078b6cb7db228f1c8307566dcecf900867401a92023d7ba99cb5f"));
            Assert.That(BeaconChainSpec.Hoodi.GenesisTime, Is.EqualTo(1742213400ul));
            Assert.That(BeaconChainSpec.Hoodi.SecondsPerSlot, Is.EqualTo(12ul));
            Assert.That(BeaconChainSpec.Hoodi.SlotsPerEpoch, Is.EqualTo(32ul));
            Assert.That(BeaconChainSpec.Hoodi.ElectraForkEpoch, Is.EqualTo(2048ul));
            Assert.That(BeaconChainSpec.Hoodi.FuluForkEpoch, Is.EqualTo(50688ul));
            Assert.That(BeaconChainSpec.Hoodi.MaxBlobsPerBlockElectra, Is.EqualTo(9ul));
            Assert.That(BeaconChainSpec.Hoodi.Forks, Has.Length.EqualTo(7));
            Assert.That(BeaconChainSpec.Hoodi.BlobSchedule, Has.Length.EqualTo(2));
        });

    // Independent sources: eth-clients/sepolia metadata, public beacon genesis/fork_schedule, Lighthouse/Prysm config; Gloas parameters: ethereum/pm#2205 and lodestar#10119.
    [Test]
    public void Sepolia_spec_matches_its_published_network_parameters() =>
        Assert.Multiple(() =>
        {
            Assert.That(BeaconChainSpec.Sepolia.ChainId, Is.EqualTo(BlockchainIds.Sepolia));
            Assert.That(BeaconChainSpec.Sepolia.GenesisValidatorsRoot.ToString(),
                Is.EqualTo("0xd8ea171f3c94aea21ebc42a1ed61052acf3f9209c00e4efbaaddac09ed9b8078"));
            Assert.That(BeaconChainSpec.Sepolia.GenesisTime, Is.EqualTo(1655733600ul));
            Assert.That(BeaconChainSpec.Sepolia.SecondsPerSlot, Is.EqualTo(12ul));
            Assert.That(BeaconChainSpec.Sepolia.SlotsPerEpoch, Is.EqualTo(32ul));
            Assert.That(BeaconChainSpec.Sepolia.ElectraForkEpoch, Is.EqualTo(222464ul));
            Assert.That(BeaconChainSpec.Sepolia.FuluForkEpoch, Is.EqualTo(272640ul));
            Assert.That(BeaconChainSpec.Sepolia.GloasForkEpoch, Is.EqualTo(353024ul));
            Assert.That(BeaconChainSpec.Sepolia.MaxBlobsPerBlockElectra, Is.EqualTo(9ul));
            Assert.That(BeaconChainSpec.Sepolia.Forks, Has.Length.EqualTo(8),
                "Sepolia has a confirmed Gloas entry, unlike Mainnet and Hoodi");
            Assert.That(BeaconChainSpec.Sepolia.BlobSchedule, Has.Length.EqualTo(2));
        });

    [TestCaseSource(nameof(ShippedNetworks))]
    public void Every_shipped_spec_carries_bootnodes(string name, BeaconChainSpec spec) =>
        Assert.That(spec.Bootnodes, Is.Not.Empty, $"{name} has no bootnode records");

    private static IEnumerable<object[]> ShippedNetworks() =>
    [
        ["Mainnet", BeaconChainSpec.Mainnet],
        ["Hoodi", BeaconChainSpec.Hoodi],
        ["Sepolia", BeaconChainSpec.Sepolia],
    ];
}
