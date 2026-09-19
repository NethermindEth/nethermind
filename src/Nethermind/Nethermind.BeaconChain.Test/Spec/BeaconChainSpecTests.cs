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

    [TestCase(BlockchainIds.Sepolia)]
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
}
