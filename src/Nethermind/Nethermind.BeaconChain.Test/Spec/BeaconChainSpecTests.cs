// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Spec;

public class BeaconChainSpecTests
{
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

        Assert.Multiple(() =>
        {
            Assert.That(ex.ChainId, Is.EqualTo(chainId));
            Assert.That(ex.Message, Does.Contain(chainId.ToString()),
                "the offending chain id must be visible in the failure so an operator can diagnose it");
        });
    }

    /// <summary>
    /// Guards the Hoodi constants against later accidental edits. The expected values were taken from
    /// eth-clients/hoodi metadata (config.yaml and genesis_validators_root.txt); re-verify against
    /// those files rather than against the spec object if this ever fails, since both live in-repo.
    /// A wrong genesis validators root or fork version does not fail loudly, it silently partitions
    /// the node from the network.
    /// </summary>
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

    /// <summary>
    /// Guards the Sepolia constants the same way <see cref="Hoodi_spec_matches_its_published_network_parameters"/>
    /// guards Hoodi's. Sources (each value confirmed against two, independently): eth-clients/sepolia
    /// metadata/config.yaml, a public Sepolia beacon node's /eth/v1/beacon/genesis and
    /// /eth/v1/config/fork_schedule, sigp/lighthouse's built-in Sepolia config, and Prysm's
    /// testnet_sepolia_config.go; GloasForkEpoch/GloasForkVersion additionally from the merged
    /// ethereum/pm#2205 and ChainSafe/lodestar#10119. Re-verify against those, not against the spec
    /// object, if this ever fails.
    /// </summary>
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
}
